using System.Net;
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

/// <summary>
/// The Mac presented a different identity key (or SSH host key) from the one pinned, and the user
/// accepted it, but the password on hand came from the vault. A saved password is sealed for the
/// key it was saved with, so it isn't sent to a new one: the user types it this once instead.
/// </summary>
public sealed class SavedPasswordWithheldException() : Exception(
    "This Mac's identity changed and you chose to trust the new one, so Vesper won't send the saved password to it without you. Type the password to continue.");

/// <summary>Decisions only a person can make, asked on the UI thread.</summary>
public interface ITrustPrompts
{
    Task<bool> TrustMacIdentityAsync(MacProfile profile, MacIdentity identity, string? previousFingerprint);
    Task<bool> TrustSshHostKeyAsync(MacProfile profile, SshHostKey key, string? previousFingerprint);
}

public sealed class ConnectedMac(RfbSession session, SshTunnel? tunnel, Socket socket, SecuritySummary security) : IAsyncDisposable
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
public sealed class ConnectionService(AppData data, SecretVault vault)
{
    /// <param name="passwordFromVault">
    /// The password came from the vault rather than being typed for this connection: it's then
    /// withheld from a Mac whose pinned key changed, even if the user accepts the new key.
    /// </param>
    public async Task<ConnectedMac> ConnectAsync(
        MacProfile profile, string password, bool passwordFromVault, (int Width, int Height) displayPixels, ITrustPrompts prompts,
        IProgress<ConnectStage> progress, CancellationToken ct)
    {
        // Pins are saved from network threads; they're applied where the profile lives.
        var ui = SynchronizationContext.Current;
        progress.Report(ConnectStage.Reaching);
        SshTunnel? tunnel = null;
        if (profile.Transport == TransportMode.Ssh)
        {
            progress.Report(ConnectStage.SecuringTunnel);
            var sshPin = new SshPin(profile, prompts, this, ui, passwordFromVault);
            try
            {
                tunnel = await SshTunnel.OpenAsync(profile.Host, profile.SshPort, profile.Username, password, sshPin, ct);
            }
            catch (IdentityRejectedException) when (sshPin.WithheldSavedPassword)
            {
                throw new SavedPasswordWithheldException();
            }
        }

        try
        {
            var identityPin = new IdentityPin(profile, prompts, this, ui, passwordFromVault);
            var (stream, socket, path) = await OpenTransportAsync(profile, tunnel, ct);
            var policy = Policy(profile, tunnel, path);
            Diagnostics.Add($"Connecting to {profile.Host} via {(tunnel is null ? path.Description : "SSH tunnel")}: " +
                $"RSA {(policy.Auth.AllowRsa ? "yes" : "no")}, Diffie-Hellman {(policy.Auth.AllowDiffieHellman ? "yes" : "no")}");

            RfbSession session;
            try
            {
                session = await StartSessionAsync(profile, password, displayPixels, identityPin, progress, stream, path, policy, policy.Auth, ct);
            }
            catch (IdentityRejectedException) when (identityPin.WithheldSavedPassword)
            {
                throw new SavedPasswordWithheldException();
            }
            catch (AuthenticationFailedException e) when (e.CredentialsRejected && e.Method == SignInMethod.AppleRsa && e.DiffieHellmanOffered && policy.RetryWithDiffieHellman)
            {
                // A Mac reports a refused RSA login exactly like a wrong password. Where the older
                // sign-in is allowed anyway, try it once with the same password, on a connection
                // that goes to the same place and is judged afresh.
                Diagnostics.Add($"RSA sign-in to {profile.Host} was refused; trying Apple's older sign-in once.");
                var first = (socket.RemoteEndPoint as IPEndPoint)?.Address;
                (stream, socket, path) = await OpenTransportAsync(profile, tunnel, ct);
                var retryPolicy = Policy(profile, tunnel, path);
                bool moved = tunnel is null && !Equals(first, (socket.RemoteEndPoint as IPEndPoint)?.Address);
                if (moved || !retryPolicy.Auth.AllowDiffieHellman)
                {
                    await stream.DisposeAsync();
                    throw;
                }
                session = await StartSessionAsync(profile, password, displayPixels, identityPin, progress, stream, path, retryPolicy,
                    new AuthPolicy(AllowRsa: false, AllowDiffieHellman: true), ct);
                if (retryPolicy.RememberRsaRefusal)
                {
                    // Remembered only once the session proves itself: until then, whoever
                    // answered could have staged the refusal.
                    session.Secured += () => OnUi(ui, () =>
                    {
                        profile.RsaSignInUnsupported = true;
                        Persist(profile, p => p.RsaSignInUnsupported = true);
                    });
                }
            }
            catch (SecurityRefusedException e) when (e.Reason == RefusalReason.OnlyUnverifiableSignIn && profile.PinnedIdentityFingerprint is not null)
            {
                throw new SecurityRefusedException(e.Reason,
                    "This Mac proved its identity on earlier connections, but this time it only offered Apple's older sign-in, which can't. " +
                    "That's what someone intercepting the connection would arrange, so Vesper stopped before sending your password. " +
                    "If the Mac's sharing settings really changed, connect once through an SSH tunnel, or forget this Mac's identity from its menu.");
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
            return (await tunnel.OpenStreamAsync(profile.Port, ct), tunnel.Socket, NetworkPath.Classify(tunnel.Socket, profile.Host));
        }
        var socket = await TcpConnector.ConnectAsync(profile.Host, profile.Port, TimeSpan.FromSeconds(10), ct);
        return (new NetworkStream(socket, ownsSocket: true), socket, NetworkPath.Classify(socket, profile.Host));
    }

    private static SignInPolicy Policy(MacProfile profile, SshTunnel? tunnel, NetworkPath path) => SignInPolicy.Decide(
        insideSsh: tunnel is not null,
        pathAuthenticatesMac: path.AuthenticatesPeer,
        allowLegacySignIn: profile.AllowLegacySignIn,
        identityPinned: profile.PinnedIdentityFingerprint is not null,
        rsaRefusedBefore: profile.RsaSignInUnsupported);

    private static async Task<RfbSession> StartSessionAsync(
        MacProfile profile, string password, (int Width, int Height) displayPixels, IdentityPin identityPin, IProgress<ConnectStage> progress,
        Stream stream, NetworkPath path, SignInPolicy policy, AuthPolicy auth, CancellationToken ct)
    {
        progress.Report(ConnectStage.VerifyingIdentity);
        var options = new RfbSessionOptions
        {
            Username = profile.Username,
            Password = password,
            Auth = auth,
            AllowUnencrypted = policy.AllowUnencrypted,
            IdentityVerifier = identityPin,
            ViewOnly = profile.ViewOnly,
            ClipboardFromMac = profile.ShareClipboard,
            Color = profile.Quality,
            Codec = ChooseCodec(profile, path),
            FitPixels = profile.Resolution == ResolutionMode.MatchThisDisplay ? displayPixels : null,
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
    private sealed class IdentityPin(MacProfile profile, ITrustPrompts prompts, ConnectionService owner, SynchronizationContext? ui, bool passwordFromVault) : IMacIdentityVerifier
    {
        /// <summary>The user trusted a changed key, but the password is a saved one: stopped before sending it.</summary>
        public bool WithheldSavedPassword { get; private set; }

        public async ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct)
        {
            string? pinned = profile.PinnedIdentityFingerprint;
            if (pinned == identity.Fingerprint) return true;
            if (!await prompts.TrustMacIdentityAsync(profile, identity, pinned)) return false;
            WithheldSavedPassword = pinned is not null && passwordFromVault;
            return !WithheldSavedPassword;
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
    private sealed class SshPin(MacProfile profile, ITrustPrompts prompts, ConnectionService owner, SynchronizationContext? ui, bool passwordFromVault) : IHostKeyVerifier
    {
        /// <summary>The user trusted a changed host key, but the password is a saved one: stopped before sending it.</summary>
        public bool WithheldSavedPassword { get; private set; }

        public async ValueTask<bool> VerifyAsync(SshHostKey key, CancellationToken ct)
        {
            string fingerprint = key.Fingerprint;
            string? pinnedFingerprint = profile.PinnedSshKey is { } pinned
                ? new SshHostKey(profile.PinnedSshKeyAlgorithm ?? "", Convert.FromBase64String(pinned)).Fingerprint
                : null;
            if (pinnedFingerprint == fingerprint && profile.PinnedSshKeyAlgorithm == key.Algorithm) return true;
            if (!await prompts.TrustSshHostKeyAsync(profile, key, pinnedFingerprint)) return false;
            if (pinnedFingerprint is not null && passwordFromVault)
            {
                WithheldSavedPassword = true;
                return false;
            }
            string blob = Convert.ToBase64String(key.Blob);
            OnUi(ui, () => owner.ChangeTrust(profile, p => { p.PinnedSshKeyAlgorithm = key.Algorithm; p.PinnedSshKey = blob; }));
            return true;
        }
    }
}
