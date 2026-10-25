using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Vesper.FakeMac;

/// <summary>
/// A stand-in for the Mac's Remote Login (sshd), so Vesper's SSH tunnel can be tested: it accepts
/// the fake Mac's account by password and turns each direct-tcpip channel to the Screen Sharing
/// port into a FakeMac session. Loopback only.
/// </summary>
public sealed class SshFront(Desktop desktop, FakeMacOptions options, int rfbPort) : IDisposable
{
    private SshServer? _server;
    private readonly List<SshServerSession> _sessions = [];

    public string HostKeyFingerprint { get; private set; } = "";

    /// <summary>Every password a client sent, right or wrong (for tests that must prove none was).</summary>
    public List<string> PasswordsReceived { get; } = [];

    /// <summary>
    /// Acts like a hostile Mac: asks the client to open a TCP connection to <paramref name="host"/>
    /// port <paramref name="port"/> on the client's side (a server-initiated direct-tcpip channel).
    /// True if the client agreed.
    /// </summary>
    public async Task<bool> TryReverseConnectAsync(string host, int port, CancellationToken ct)
    {
        SshServerSession session;
        lock (_sessions) session = _sessions.Last();
        try
        {
            var request = new PortForwardChannelOpenMessage
            {
                ChannelType = "direct-tcpip",
                Host = host,
                Port = (uint)port,
                OriginatorIPAddress = "127.0.0.1",
                OriginatorPort = 4444,
            };
            using var channel = await session.OpenChannelAsync(request, null, ct);
            return true;
        }
        catch (SshChannelException)
        {
            return false;
        }
    }

    public Task StartAsync(int port, CancellationToken ct)
    {
        var config = new SshSessionConfiguration();
        _server = new SshServer(config, new TraceSource("FakeMac.Ssh", SourceLevels.Off));
        var hostKey = SshAlgorithms.PublicKey.ECDsaSha2Nistp256!.GenerateKeyPair();
        HostKeyFingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey.GetPublicKeyBytes(hostKey.KeyAlgorithmName).ToArray())).TrimEnd('=');
        _server.Credentials = new SshServerCredentials([hostKey]);

        _server.SessionOpened += (_, session) =>
        {
            lock (_sessions) _sessions.Add(session);
        };
        _server.SessionAuthenticating += (_, e) =>
        {
            if (e.AuthenticationType == SshAuthenticationType.ClientPassword && e.Password is { } sent)
            {
                lock (PasswordsReceived) PasswordsReceived.Add(sent);
            }
            if (e.AuthenticationType == SshAuthenticationType.ClientPassword && e.Username == options.Username && e.Password == options.Password)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ssh: password accepted for \"{e.Username}\"");
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity("password")));
            }
            else if (e.AuthenticationType == SshAuthenticationType.ClientPassword)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ssh: wrong password for \"{e.Username}\"");
            }
        };

        // Every direct-tcpip channel (the client's port forward) becomes a FakeMac session.
        _server.ChannelOpening += (_, e) =>
        {
            if (e.Channel.ChannelType != "direct-tcpip")
            {
                e.FailureReason = SshChannelOpenFailureReason.UnknownChannelType;
                return;
            }
            if (e.Request is PortForwardChannelOpenMessage forward && forward.Port != rfbPort)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ssh: refused a forward to port {forward.Port}");
                e.FailureReason = SshChannelOpenFailureReason.ConnectFailed;
                return;
            }
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ssh: direct-tcpip channel opened");
            var stream = new SshStream(e.Channel);
            _ = Task.Run(async () =>
            {
                await using var connection = new FakeMacConnection(stream, desktop, options, "ssh");
                try
                {
                    await connection.RunAsync(ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ssh session: {ex.GetType().Name}: {ex.Message}");
                }
            });
        };

        return _server.AcceptSessionsAsync(port, IPAddress.Loopback);
    }

    public void Dispose() => _server?.Dispose();
}
