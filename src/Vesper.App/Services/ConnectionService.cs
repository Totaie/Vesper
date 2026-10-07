using System.Net.Sockets;
using Vesper.Core.Rfb;
using Vesper.Core.Transport;
using Vesper.Models;

namespace Vesper.Services;

public enum ConnectStage { Reaching, SecuringTunnel, VerifyingIdentity, SigningIn, Encrypting, Ready }

public enum SecurityLevel
{
    /// <summary>Encrypted, and the Mac's identity is pinned (Apple RSA sign-in, or SSH host key).</summary>
    Verified,

    /// <summary>Encrypted inside a WireGuard VPN that authenticates the Mac's node.</summary>
    Protected,

    /// <summary>Encrypted, but an active attacker on this network could impersonate the Mac.</summary>
    Unverified,
}

public sealed record SecuritySummary(SecurityLevel Level, string Headline, string Detail);

/// <summary>Decisions only a person can make, asked on the UI thread.</summary>
internal interface ITrustPrompts
{
    Task<bool> TrustMacIdentityAsync(MacProfile profile, MacIdentity identity, string? previousFingerprint);
    Task<bool> TrustSshHostKeyAsync(MacProfile profile, SshHostKey key, string? previousFingerprint);
}

internal sealed class ConnectedMac(RfbSession session, SshTunnel? tunnel, Socket socket, SecuritySummary security) : IAsyncDisposable
{
    public RfbSession Session { get; } = session;
    public SshTunnel? Tunnel { get; } = tunnel;

    /// <summary>The TCP connection carrying the session, for round-trip time.</summary>
    public Socket Socket { get; } = socket;

    public SecuritySummary Security { get; } = security;

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        if (Tunnel is not null) await Tunnel.DisposeAsync();
    }
}

/// <summary>
/// Builds a connection the way the Mac's settings and Vesper's security policy say: the transport
/// (direct or SSH), which sign-in methods that transport makes safe, identity pinning, and the
/// session options.
/// </summary>
internal sealed class ConnectionService(AppData data)
{
    public async Task<ConnectedMac> ConnectAsync(
        MacProfile profile, string password, double pcPixelsPerDip, ITrustPrompts prompts,
        IProgress<ConnectStage> progress, CancellationToken ct)
    {
        progress.Report(ConnectStage.Reaching);
        Stream stream;
        Socket socket;
        SshTunnel? tunnel = null;
        AuthPolicy auth;
        Func<SignInMethod, SecuritySummary> summarize;

        if (profile.Transport == TransportMode.Ssh)
        {
            progress.Report(ConnectStage.SecuringTunnel);
            tunnel = await SshTunnel.OpenAsync(profile.Host, profile.SshPort, profile.Username, password,
                new SshPin(profile, prompts, data), ct);
            try
            {
                stream = await tunnel.OpenStreamAsync(profile.Port, ct);
            }
            catch
            {
                await tunnel.DisposeAsync();
                throw;
            }
            socket = tunnel.Socket;
            auth = AuthPolicy.Any; // inside a host-verified SSH tunnel, Apple's older sign-in is safe
            summarize = _ => new SecuritySummary(SecurityLevel.Verified, "Encrypted · SSH tunnel",
                $"Inside an SSH tunnel to the Mac ({tunnel.HostKey.FriendlyAlgorithm} host key, pinned), with Apple's AES encryption inside it.");
        }
        else
        {
            socket = await TcpConnector.ConnectAsync(profile.Host, profile.Port, TimeSpan.FromSeconds(10), ct);
            var path = NetworkPath.Classify(socket);
            stream = new NetworkStream(socket, ownsSocket: true);
            auth = path.IsEncrypted || profile.AllowLegacySignIn ? AuthPolicy.Any : AuthPolicy.RsaOnly;
            summarize = method => (method, path.Kind) switch
            {
                (SignInMethod.AppleRsa, _) => new SecuritySummary(SecurityLevel.Verified, "Encrypted · identity verified",
                    "Signed in with the Mac's pinned RSA identity key; the whole session is encrypted with AES-128."),
                (_, NetworkPathKind.WireGuardVpn) => new SecuritySummary(SecurityLevel.Protected, $"Encrypted · {path.Description}",
                    $"Inside {path.Description}, with Apple's AES encryption inside that."),
                (_, NetworkPathKind.Loopback) => new SecuritySummary(SecurityLevel.Protected, "Encrypted · this PC", "A connection to this PC."),
                _ => new SecuritySummary(SecurityLevel.Unverified, "Encrypted · identity not verified",
                    "Apple's older sign-in protects against eavesdropping but can't prove this is your Mac. Use an SSH tunnel or a VPN for full protection."),
            };
        }

        progress.Report(ConnectStage.VerifyingIdentity);
        var options = new RfbSessionOptions
        {
            Username = profile.Username,
            Password = password,
            Auth = auth,
            IdentityVerifier = new IdentityPin(profile, prompts, data),
            ViewOnly = profile.ViewOnly,
            ClipboardFromMac = profile.ShareClipboard,
            Color = profile.Color,
            Codec = ChooseCodec(profile, socket),
            MatchPixelsPerPoint = profile.Resolution == ResolutionMode.MatchThisDisplay ? pcPixelsPerDip : null,
            StageChanged = stage => progress.Report(stage switch
            {
                SessionStage.SigningIn => ConnectStage.SigningIn,
                SessionStage.Encrypting => ConnectStage.Encrypting,
                _ => ConnectStage.Ready,
            }),
        };

        RfbSession session;
        try
        {
            session = await RfbSession.ConnectAsync(stream, options, ct);
        }
        catch
        {
            await stream.DisposeAsync();
            if (tunnel is not null) await tunnel.DisposeAsync();
            throw;
        }
        return new ConnectedMac(session, tunnel, socket, summarize(session.Info.SignIn));
    }

    /// <summary>zlib (Apple's own choice for Full quality) on fast local links, ZRLE when bandwidth matters.</summary>
    private static Codec ChooseCodec(MacProfile profile, Socket socket) => profile.Codec switch
    {
        CodecPreference.Zlib => Codec.Zlib,
        CodecPreference.Zrle => Codec.Zrle,
        _ => NetworkPath.Classify(socket).Kind is NetworkPathKind.Loopback or NetworkPathKind.LocalNetwork ? Codec.Zlib : Codec.Zrle,
    };

    /// <summary>Trust on first use for the Mac's Screen Sharing identity key; a changed key needs the user.</summary>
    private sealed class IdentityPin(MacProfile profile, ITrustPrompts prompts, AppData data) : IMacIdentityVerifier
    {
        public async ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct)
        {
            string? pinned = profile.PinnedIdentityFingerprint;
            if (pinned == identity.Fingerprint) return true;
            if (!await prompts.TrustMacIdentityAsync(profile, identity, pinned)) return false;
            profile.PinnedIdentityFingerprint = identity.Fingerprint;
            Persist(profile, data, p => p.PinnedIdentityFingerprint = identity.Fingerprint);
            return true;
        }
    }

    private sealed class SshPin(MacProfile profile, ITrustPrompts prompts, AppData data) : IHostKeyVerifier
    {
        public async ValueTask<bool> VerifyAsync(SshHostKey key, CancellationToken ct)
        {
            string fingerprint = key.Fingerprint;
            string? pinnedFingerprint = profile.PinnedSshKey is { } pinned
                ? new SshHostKey(profile.PinnedSshKeyAlgorithm ?? "", Convert.FromBase64String(pinned)).Fingerprint
                : null;
            if (pinnedFingerprint == fingerprint && profile.PinnedSshKeyAlgorithm == key.Algorithm) return true;
            if (!await prompts.TrustSshHostKeyAsync(profile, key, pinnedFingerprint)) return false;
            string blob = Convert.ToBase64String(key.Blob);
            profile.PinnedSshKeyAlgorithm = key.Algorithm;
            profile.PinnedSshKey = blob;
            Persist(profile, data, p => { p.PinnedSshKeyAlgorithm = key.Algorithm; p.PinnedSshKey = blob; });
            return true;
        }
    }

    private static void Persist(MacProfile profile, AppData data, Action<MacProfile> apply)
    {
        var stored = data.Find(profile.Id);
        if (stored is null) return;
        apply(stored);
        data.SaveProfiles();
    }
}
