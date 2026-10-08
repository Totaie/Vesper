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

    /// <summary>Encrypted, straight to the Mac's Tailscale address, where WireGuard proves which machine answers.</summary>
    Protected,

    /// <summary>Encrypted, but an active attacker on this network could impersonate the Mac.</summary>
    Unverified,

    /// <summary>Nothing of Apple's protects the session. Vesper refuses these outside an SSH tunnel; kept as a backstop.</summary>
    Unencrypted,
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
internal sealed class ConnectionService(AppData data, SecretVault vault)
{
    public async Task<ConnectedMac> ConnectAsync(
        MacProfile profile, string password, double pcPixelsPerDip, ITrustPrompts prompts,
        IProgress<ConnectStage> progress, CancellationToken ct)
    {
        // Pins are saved from network threads; they're applied where the profile lives.
        var ui = SynchronizationContext.Current;
        progress.Report(ConnectStage.Reaching);
        SshTunnel? tunnel = null;
        if (profile.Transport == TransportMode.Ssh)
        {
            progress.Report(ConnectStage.SecuringTunnel);
            tunnel = await SshTunnel.OpenAsync(profile.Host, profile.SshPort, profile.Username, password,
                new SshPin(profile, prompts, this, ui), ct);
        }

        try
        {
            var (stream, socket, path) = await OpenTransportAsync(profile, tunnel, ct);
            var policy = SignInPolicy.Decide(
                insideSsh: tunnel is not null,
                pathAuthenticatesMac: path.AuthenticatesPeer,
                allowLegacySignIn: profile.AllowLegacySignIn,
                identityPinned: profile.PinnedIdentityFingerprint is not null,
                rsaRefusedBefore: profile.RsaSignInUnsupported);
            Diagnostics.Add($"Connecting to {profile.Host} via {(tunnel is null ? path.Description : "SSH tunnel")}: " +
                $"RSA {(policy.Auth.AllowRsa ? "yes" : "no")}, Diffie-Hellman {(policy.Auth.AllowDiffieHellman ? "yes" : "no")}");

            RfbSession session;
            try
            {
                session = await StartSessionAsync(profile, password, pcPixelsPerDip, prompts, progress, stream, path, policy, policy.Auth, ui, ct);
            }
            catch (AuthenticationFailedException e) when (e.CredentialsRejected && e.Method == SignInMethod.AppleRsa && e.DiffieHellmanOffered && policy.RetryWithDiffieHellman)
            {
                // A Mac reports a refused RSA login exactly like a wrong password. Where the older
                // sign-in is allowed anyway, try it once with the same password.
                Diagnostics.Add($"RSA sign-in to {profile.Host} was refused; trying Apple's older sign-in once.");
                (stream, socket, path) = await OpenTransportAsync(profile, tunnel, ct);
                session = await StartSessionAsync(profile, password, pcPixelsPerDip, prompts, progress, stream, path, policy,
                    new AuthPolicy(AllowRsa: false, AllowDiffieHellman: true), ui, ct);
                if (policy.RememberRsaRefusal)
                {
                    profile.RsaSignInUnsupported = true;
                    Persist(profile, p => p.RsaSignInUnsupported = true);
                }
            }
            catch (SecurityRefusedException e) when (e.Reason == RefusalReason.OnlyUnverifiableSignIn && profile.PinnedIdentityFingerprint is not null)
            {
                throw new SecurityRefusedException(e.Reason,
                    "This Mac proved its identity on earlier connections, but this time it only offered Apple's older sign-in, which can't. " +
                    "That's what someone intercepting the connection would arrange, so Vesper stopped before sending your password. " +
                    "If the Mac's sharing settings really changed, connect once through an SSH tunnel or Tailscale, or forget this Mac's identity from its menu.");
            }
            return new ConnectedMac(session, tunnel, socket, Summarize(session.Info, path, tunnel));
        }
        catch
        {
            if (tunnel is not null) await tunnel.DisposeAsync();
            throw;
        }
    }

    private static async Task<(Stream Stream, Socket Socket, NetworkPath Path)> OpenTransportAsync(MacProfile profile, SshTunnel? tunnel, CancellationToken ct)
    {
        if (tunnel is not null)
        {
            return (await tunnel.OpenStreamAsync(profile.Port, ct), tunnel.Socket, NetworkPath.Classify(tunnel.Socket));
        }
        var socket = await TcpConnector.ConnectAsync(profile.Host, profile.Port, TimeSpan.FromSeconds(10), ct);
        return (new NetworkStream(socket, ownsSocket: true), socket, NetworkPath.Classify(socket));
    }

    private async Task<RfbSession> StartSessionAsync(
        MacProfile profile, string password, double pcPixelsPerDip, ITrustPrompts prompts, IProgress<ConnectStage> progress,
        Stream stream, NetworkPath path, SignInPolicy policy, AuthPolicy auth, SynchronizationContext? ui, CancellationToken ct)
    {
        progress.Report(ConnectStage.VerifyingIdentity);
        var options = new RfbSessionOptions
        {
            Username = profile.Username,
            Password = password,
            Auth = auth,
            AllowUnencrypted = policy.AllowUnencrypted,
            IdentityVerifier = new IdentityPin(profile, prompts, this, ui),
            ViewOnly = profile.ViewOnly,
            ClipboardFromMac = profile.ShareClipboard,
            Color = profile.Quality,
            Codec = ChooseCodec(profile, path),
            MatchPixelsPerPoint = profile.Resolution == ResolutionMode.MatchThisDisplay ? pcPixelsPerDip : null,
            StageChanged = stage => progress.Report(stage switch
            {
                SessionStage.SigningIn => ConnectStage.SigningIn,
                SessionStage.Encrypting => ConnectStage.Encrypting,
                _ => ConnectStage.Ready,
            }),
        };
        try
        {
            return await RfbSession.ConnectAsync(stream, options, ct);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static SecuritySummary Summarize(SessionInfo info, NetworkPath path, SshTunnel? tunnel)
    {
        if (tunnel is not null)
        {
            return new SecuritySummary(SecurityLevel.Verified, "Encrypted · SSH tunnel",
                $"Inside an SSH tunnel to the Mac ({tunnel.HostKey.FriendlyAlgorithm} host key, pinned)" +
                (info.Encrypted ? ", with Apple's AES encryption inside it." : "."));
        }
        if (!info.Encrypted)
        {
            // Vesper refuses a server without Apple's encryption unless it's inside the tunnel.
            return new SecuritySummary(SecurityLevel.Unencrypted, "Not encrypted",
                "Nothing protects this session: anyone on the network can see the screen and what you type.");
        }
        if (info.SignIn == SignInMethod.AppleRsa)
        {
            return new SecuritySummary(SecurityLevel.Verified, "Encrypted · identity verified",
                "Signed in with the Mac's pinned RSA identity key; the whole session is encrypted with AES-128" +
                (path.AuthenticatesPeer ? ", inside Tailscale." : "."));
        }
        if (path.AuthenticatesPeer)
        {
            return new SecuritySummary(SecurityLevel.Protected, "Encrypted · Tailscale",
                "Straight to the Mac's Tailscale address, where WireGuard proves which machine answers, with Apple's AES encryption inside it.");
        }
        return new SecuritySummary(SecurityLevel.Unverified, "Encrypted · identity not verified",
            "Apple's older sign-in protects against eavesdropping but can't prove this is your Mac. Use an SSH tunnel or Tailscale for full protection.");
    }

    /// <summary>zlib (Apple's own choice for Full quality) on fast local links, ZRLE when bandwidth matters.</summary>
    private static Codec ChooseCodec(MacProfile profile, NetworkPath path) => profile.Codec switch
    {
        CodecPreference.Zlib => Codec.Zlib,
        CodecPreference.Zrle => Codec.Zrle,
        _ => path.Kind is NetworkPathKind.Loopback or NetworkPathKind.LocalNetwork ? Codec.Zlib : Codec.Zrle,
    };

    /// <summary>
    /// Changes what vouches for a Mac (a pin), saves it, and carries a saved password over to it.
    /// Runs on the UI thread.
    /// </summary>
    private void ChangeTrust(MacProfile profile, Action<MacProfile> change)
    {
        byte[] before = SecretVault.Binding(profile);
        change(profile);
        Persist(profile, change);
        try
        {
            vault.Rebind(profile.Id, before, SecretVault.Binding(profile));
        }
        catch (Exception e)
        {
            Diagnostics.Add($"Couldn't update the saved password for {profile.Host}: {e.Message}");
        }
    }

    private void Persist(MacProfile profile, Action<MacProfile> apply)
    {
        var stored = data.Find(profile.Id);
        if (stored is null) return;
        if (!ReferenceEquals(stored, profile)) apply(stored);
        data.SaveProfiles();
    }

    private static void OnUi(SynchronizationContext? ui, Action action)
    {
        void Run()
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Diagnostics.Add($"Couldn't save a trusted key: {e.GetType().Name}: {e.Message}");
            }
        }
        if (ui is null) Run();
        else ui.Post(_ => Run(), null);
    }

    /// <summary>
    /// Trust on first use for the Mac's Screen Sharing identity key; a changed key needs the user.
    /// A trusted key is pinned only once the Mac has proven it holds it.
    /// </summary>
    private sealed class IdentityPin(MacProfile profile, ITrustPrompts prompts, ConnectionService owner, SynchronizationContext? ui) : IMacIdentityVerifier
    {
        public async ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct)
        {
            string? pinned = profile.PinnedIdentityFingerprint;
            return pinned == identity.Fingerprint || await prompts.TrustMacIdentityAsync(profile, identity, pinned);
        }

        public void Proven(MacIdentity identity) => OnUi(ui, () =>
        {
            if (profile.PinnedIdentityFingerprint == identity.Fingerprint) return;
            owner.ChangeTrust(profile, p => p.PinnedIdentityFingerprint = identity.Fingerprint);
            Diagnostics.Add($"Pinned the identity key of {profile.Host} after it proved it holds it.");
        });
    }

    /// <summary>
    /// The SSH host key. SSH's key exchange has already checked the host's signature by the time
    /// it's offered here, so a trusted key is pinned straight away, as OpenSSH does.
    /// </summary>
    private sealed class SshPin(MacProfile profile, ITrustPrompts prompts, ConnectionService owner, SynchronizationContext? ui) : IHostKeyVerifier
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
            OnUi(ui, () => owner.ChangeTrust(profile, p => { p.PinnedSshKeyAlgorithm = key.Algorithm; p.PinnedSshKey = blob; }));
            return true;
        }
    }
}
