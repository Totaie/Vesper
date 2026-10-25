using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace Vesper.Core.Transport;

/// <summary>An SSH host key as the server presented it (the key blob in SSH wire format).</summary>
public sealed record SshHostKey(string Algorithm, byte[] Blob)
{
    /// <summary>OpenSSH's format, so it can be compared with <c>ssh-keygen -lf</c> on the Mac.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    public string FriendlyAlgorithm => Algorithm switch
    {
        "ecdsa-sha2-nistp256" => "ECDSA P-256",
        "ecdsa-sha2-nistp384" => "ECDSA P-384",
        "ecdsa-sha2-nistp521" => "ECDSA P-521",
        "ssh-rsa" or "rsa-sha2-256" or "rsa-sha2-512" => "RSA",
        _ => Algorithm,
    };

    /// <summary>The command that prints this key's fingerprint on the Mac, for out-of-band checking.</summary>
    public string VerifyCommand => Algorithm.StartsWith("ecdsa", StringComparison.Ordinal)
        ? "ssh-keygen -lf /etc/ssh/ssh_host_ecdsa_key.pub"
        : "ssh-keygen -lf /etc/ssh/ssh_host_rsa_key.pub";

    public bool SameKeyAs(SshHostKey other) =>
        Algorithm == other.Algorithm && CryptographicOperations.FixedTimeEquals(Blob, other.Blob);
}

public interface IHostKeyVerifier
{
    /// <summary>Decide whether to trust the Mac's SSH host key. False aborts before any password is sent.</summary>
    ValueTask<bool> VerifyAsync(SshHostKey key, CancellationToken ct);
}

/// <summary>
/// An SSH connection to the Mac's Remote Login, used purely as an encrypted, authenticated pipe to
/// its Screen Sharing port. The forwarded stream is a direct-tcpip channel handed straight to the
/// RFB client: no local listening port is opened, so no other program on this PC can use the tunnel.
///
/// Algorithms are restricted to modern choices that macOS's OpenSSH accepts: ECDH key exchange,
/// AES-256-GCM (or CTR with encrypt-then-MAC), ECDSA or RSA-SHA2 host keys.
/// </summary>
public sealed class SshTunnel : IAsyncDisposable
{
    private readonly SshClientSession _session;
    private readonly NetworkStream _network;

    private SshTunnel(SshClientSession session, NetworkStream network, Socket socket, SshHostKey hostKey)
    {
        _session = session;
        _network = network;
        Socket = socket;
        HostKey = hostKey;
    }

    public SshHostKey HostKey { get; }

    /// <summary>The TCP connection under the tunnel, for round-trip measurements.</summary>
    public Socket Socket { get; }

    public bool IsConnected => _session.IsConnected;

    public static async Task<SshTunnel> OpenAsync(
        string host, int port, string username, string password, IHostKeyVerifier verifier, CancellationToken ct)
    {
        var socket = await TcpConnector.ConnectAsync(host, port, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var network = new NetworkStream(socket, ownsSocket: true);
        // Tracing off: nothing about the session, its keys or its data is written anywhere.
        var trace = new TraceSource("Vesper.Ssh", SourceLevels.Off);
        var session = new SshClientSession(CreateConfiguration(), trace);
        SshHostKey? presented = null;
        bool trusted = false;
        bool answeredPrompt = false;

        // The tunnel only ever carries what Vesper opens. A channel the Mac opens (direct-tcpip,
        // "connect to this address for me") would let a hostile Mac, or anyone holding a host key
        // that was trusted, reach services on this PC's loopback or network through it.
        var forwarding = session.ActivateService<PortForwardingService>();
        forwarding.AcceptRemoteConnectionsForNonForwardedPorts = false;
        forwarding.ForwardConnectionsToLocalPorts = false;
        forwarding.AcceptLocalConnectionsForForwardedPorts = false;
        session.ChannelOpening += (_, e) =>
        {
            if (!e.IsRemoteRequest) return;
            e.FailureReason = SshChannelOpenFailureReason.AdministrativelyProhibited;
            e.FailureDescription = "Vesper doesn't accept channels from the server.";
        };

        void OnAuthenticating(object? sender, SshAuthenticatingEventArgs e)
        {
            switch (e.AuthenticationType)
            {
                case SshAuthenticationType.ServerPublicKey when e.PublicKey is not null:
                    var key = e.PublicKey;
                    presented = new SshHostKey(key.KeyAlgorithmName, key.GetPublicKeyBytes(key.KeyAlgorithmName).ToArray());
                    e.AuthenticationTask = VerifyAsync(presented);
                    break;
                case SshAuthenticationType.ClientInteractive when e.InfoRequest is not null && trusted:
                    // macOS's PAM asks "Password:" through keyboard-interactive. The password
                    // answers one hidden prompt, once; anything else gets an empty answer.
                    var response = new AuthenticationInfoResponseMessage();
                    foreach (var (_, echo) in e.InfoRequest.Prompts)
                    {
                        bool give = !echo && !answeredPrompt;
                        answeredPrompt |= give;
                        response.Responses.Add(give ? password : "");
                    }
                    e.InfoResponse = response;
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
                    break;
            }
        }
        session.Authenticating += OnAuthenticating;

        async Task<ClaimsPrincipal?> VerifyAsync(SshHostKey key)
        {
            trusted = await verifier.VerifyAsync(key, ct).ConfigureAwait(false);
            return trusted ? new ClaimsPrincipal() : null;
        }

        try
        {
            await session.ConnectAsync(network, ct).ConfigureAwait(false);
            bool ok = await session.AuthenticateAsync(new SshClientCredentials(username, password), ct).ConfigureAwait(false);
            // Signed in or not, nothing should still be holding the password.
            session.Authenticating -= OnAuthenticating;
            if (!ok)
            {
                if (presented is null || !trusted)
                {
                    throw new Rfb.IdentityRejectedException("The Mac's SSH host key wasn't trusted, so no password was sent.");
                }
                throw new Rfb.AuthenticationFailedException(
                    "The Mac's Remote Login (SSH) didn't accept that user name and password.", credentialsRejected: true);
            }
            return new SshTunnel(session, network, socket, presented!);
        }
        catch (SshConnectionException e) when (presented is not null && !trusted)
        {
            await DisposeQuietlyAsync(session, network).ConfigureAwait(false);
            throw new Rfb.IdentityRejectedException("The Mac's SSH host key wasn't trusted, so no password was sent. " + e.Message);
        }
        catch (SshConnectionException e)
        {
            await DisposeQuietlyAsync(session, network).ConfigureAwait(false);
            throw new IOException("The SSH connection to the Mac failed: " + e.Message, e);
        }
        catch
        {
            await DisposeQuietlyAsync(session, network).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens an encrypted stream to <paramref name="remotePort"/> on the Mac itself.</summary>
    public async Task<Stream> OpenStreamAsync(int remotePort, CancellationToken ct)
    {
        try
        {
            return await _session.StreamToRemotePortAsync("127.0.0.1", remotePort, ct).ConfigureAwait(false);
        }
        catch (SshChannelException e)
        {
            throw new IOException($"The Mac refused to forward to its Screen Sharing port {remotePort}: {e.Message}", e);
        }
    }

    private static SshSessionConfiguration CreateConfiguration()
    {
        var config = new SshSessionConfiguration(useSecurity: true, enableCompression: false, enableReconnect: false);
        config.KeyExchangeAlgorithms.Clear();
        config.KeyExchangeAlgorithms.Add(SshAlgorithms.KeyExchange.EcdhNistp384);
        config.KeyExchangeAlgorithms.Add(SshAlgorithms.KeyExchange.EcdhNistp256);
        config.KeyExchangeAlgorithms.Add(SshAlgorithms.KeyExchange.EcdhNistp521);
        config.KeyExchangeAlgorithms.Add(SshAlgorithms.KeyExchange.DHGroup16Sha512);
        config.PublicKeyAlgorithms.Clear();
        config.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp256);
        config.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp384);
        config.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.ECDsaSha2Nistp521);
        config.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.RsaWithSha512);
        config.PublicKeyAlgorithms.Add(SshAlgorithms.PublicKey.RsaWithSha256);
        config.EncryptionAlgorithms.Clear();
        config.EncryptionAlgorithms.Add(SshAlgorithms.Encryption.Aes256Gcm);
        config.EncryptionAlgorithms.Add(SshAlgorithms.Encryption.Aes256Ctr);
        config.HmacAlgorithms.Clear();
        config.HmacAlgorithms.Add(SshAlgorithms.Hmac.HmacSha512Etm);
        config.HmacAlgorithms.Add(SshAlgorithms.Hmac.HmacSha256Etm);
        config.AuthenticationMethods.Clear();
        config.AuthenticationMethods.Add("password");
        config.AuthenticationMethods.Add("keyboard-interactive");
        config.AddService(typeof(PortForwardingService), null!);
        config.KeepAliveTimeoutInSeconds = 15;
        return config;
    }

    private static async ValueTask DisposeQuietlyAsync(SshClientSession session, NetworkStream network)
    {
        try { session.Dispose(); } catch { }
        try { await network.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _session.CloseAsync(SshDisconnectReason.ByApplication, "Vesper disconnected").ConfigureAwait(false);
        }
        catch
        {
        }
        await DisposeQuietlyAsync(_session, _network).ConfigureAwait(false);
    }
}
