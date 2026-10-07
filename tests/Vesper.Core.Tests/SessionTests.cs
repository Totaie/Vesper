using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Vesper.Core.Rfb;
using Vesper.Core.Transport;
using Vesper.FakeMac;

namespace Vesper.Core.Tests;

/// <summary>End-to-end: Vesper's session against FakeMac over a real loopback TCP connection.</summary>
public sealed class SessionTests : IAsyncLifetime
{
    private readonly RSA _identity = RSA.Create(2048);
    private readonly Desktop _desktop = new(800, 500, 2.0, animate: false);
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private FakeMacOptions _serverOptions = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private int StartServer(bool offerRsa = true, bool offerDh = true, bool rfb38 = false, bool impersonate = false)
    {
        _serverOptions = new FakeMacOptions("vesper", "evening-star", _identity, offerRsa, offerDh, rfb38, Verbose: false, Impersonate: impersonate);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(async () =>
                {
                    await using var c = new FakeMacConnection(client.GetStream(), _desktop, _serverOptions, "test");
                    try { await c.RunAsync(_cts.Token); } catch { }
                });
            }
        });
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private sealed class Pin(Func<MacIdentity, bool> decide) : IMacIdentityVerifier
    {
        public MacIdentity? Seen { get; private set; }
        public MacIdentity? ProvenKey { get; private set; }

        public ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct)
        {
            Seen = identity;
            return ValueTask.FromResult(decide(identity));
        }

        public void Proven(MacIdentity identity) => ProvenKey = identity;
    }

    private static async Task<RfbSession> ConnectAsync(int port, RfbSessionOptions options)
    {
        var socket = await TcpConnector.ConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), default);
        return await RfbSession.ConnectAsync(new NetworkStream(socket, ownsSocket: true), options, default);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the session.");
            await Task.Delay(20);
        }
    }

    private bool FramebufferMatchesDesktop(RfbSession session)
    {
        lock (_desktop.Sync)
        lock (session.Framebuffer.Sync)
        {
            if (session.Framebuffer.Width != _desktop.Width || session.Framebuffer.Height != _desktop.Height) return false;
            var a = _desktop.Frame;
            var b = session.Framebuffer.Pixels;
            for (int i = 0; i < a.Length; i += 4)
            {
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false;
            }
            return true;
        }
    }

    [Theory]
    [InlineData(Codec.Zlib)]
    [InlineData(Codec.Zrle)]
    public async Task Rsa_sign_in_encrypts_and_paints_the_whole_screen(Codec codec)
    {
        int port = StartServer();
        var pin = new Pin(_ => true);
        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = pin, Codec = codec,
        });
        Assert.Equal(SignInMethod.AppleRsa, session.Info.SignIn);
        Assert.True(session.Info.Encrypted);
        Assert.True(session.Info.AppleExtensions);
        Assert.Equal(ArdRsaFingerprint(), pin.Seen!.Fingerprint);
        Assert.Equal("FakeMac Studio", session.Info.DesktopName);

        string? model = null;
        session.ModelIdentified += m => model = m;
        var run = session.RunAsync(_cts.Token);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));
        await WaitForAsync(() => model is not null);
        Assert.Equal("Mac14,13", model);
        Assert.NotNull(session.Layout);
    }

    [Fact]
    public async Task Diffie_hellman_is_used_only_when_allowed()
    {
        int port = StartServer(offerRsa: false);
        var refused = await Assert.ThrowsAsync<SecurityRefusedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.RsaOnly,
        }));
        Assert.Equal(RefusalReason.OnlyUnverifiableSignIn, refused.Reason);

        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.Any,
        });
        Assert.Equal(SignInMethod.AppleDiffieHellman, session.Info.SignIn);
        Assert.True(session.Info.Encrypted);
        _ = session.RunAsync(_cts.Token);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_rejected_credentials()
    {
        int port = StartServer();
        var e = await Assert.ThrowsAsync<AuthenticationFailedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "wrong", IdentityVerifier = new Pin(_ => true),
        }));
        Assert.True(e.CredentialsRejected);
    }

    [Fact]
    public async Task An_untrusted_identity_stops_before_the_password_is_sent()
    {
        int port = StartServer();
        await Assert.ThrowsAsync<IdentityRejectedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = new Pin(_ => false),
        }));
    }

    [Fact]
    public async Task Input_reaches_the_Mac_and_server_scaling_halves_the_framebuffer()
    {
        int port = StartServer();
        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = new Pin(_ => true),
            ServerScale = 0.5, ClipboardFromMac = true,
        });
        string? clipboard = null;
        session.ClipboardReceived += t => clipboard = t;
        _ = session.RunAsync(_cts.Token);

        // 800×500 points at 2x, scaled by 0.5: an 800×500 framebuffer.
        await WaitForAsync(() => session.Framebuffer.Width == 800 && session.Framebuffer.Height == 500);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));

        // A pointer at framebuffer (100, 50) is (200, 100) in the Mac's unscaled pixels.
        session.SendPointer(100, 50, PointerButtons.Right);
        foreach (char c in "hi") { session.SendKey(c, true); session.SendKey(c, false); }
        session.SendClipboard("from Windows");
        session.SendScroll(0, 120, PointerButtons.None);

        // Command-C on the Mac changes its pasteboard; Vesper fetches it.
        session.SendKey(0xFFEB, true);
        session.SendKey('c', true);
        session.SendKey('c', false);
        session.SendKey(0xFFEB, false);

        await WaitForAsync(() => _desktop.ClipboardText == "from Windows");
        await WaitForAsync(() => clipboard is not null && clipboard.StartsWith("Copied on FakeMac", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plain_rfb_38_servers_work_only_when_the_transport_encrypts()
    {
        int port = StartServer(offerRsa: false, rfb38: true);
        var refused = await Assert.ThrowsAsync<SecurityRefusedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.Any,
        }));
        Assert.Equal(RefusalReason.NotApple, refused.Reason);

        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.Any, AllowUnencrypted = true,
        });
        Assert.False(session.Info.AppleExtensions);
        Assert.False(session.Info.Encrypted);
        _ = session.RunAsync(_cts.Token);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));
    }

    [Fact]
    public async Task Rsa_sign_in_is_never_used_without_apples_record_layer()
    {
        // A server that answers as plain RFB 3.8 but offers type 33 could replay a Mac's public
        // key; without the record layer nothing would ever prove it holds the private half.
        int port = StartServer(offerRsa: true, offerDh: false, rfb38: true);
        var pin = new Pin(_ => true);
        await Assert.ThrowsAsync<SecurityRefusedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = pin,
        }));
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = pin, AllowUnencrypted = true,
        }));
        Assert.Null(pin.Seen);
    }

    [Fact]
    public async Task The_identity_is_proven_only_by_the_first_encrypted_record()
    {
        int port = StartServer();
        var pin = new Pin(_ => true);
        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = pin,
        });
        Assert.NotNull(pin.Seen);
        Assert.Null(pin.ProvenKey); // presented and trusted, but not yet proven
        _ = session.RunAsync(_cts.Token);
        await WaitForAsync(() => pin.ProvenKey is not null);
        Assert.Equal(ArdRsaFingerprint(), pin.ProvenKey!.Fingerprint);
    }

    [Fact]
    public async Task An_impersonator_replaying_the_public_key_is_caught_before_anything_is_trusted()
    {
        int port = StartServer(impersonate: true);
        var pin = new Pin(_ => true);
        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = pin,
        });
        var refused = await Assert.ThrowsAsync<SecurityRefusedException>(() => session.RunAsync(_cts.Token));
        Assert.Equal(RefusalReason.IdentityNotProven, refused.Reason);
        Assert.Null(pin.ProvenKey);
    }

    [Fact]
    public void Server_text_loses_control_characters_and_bidi_tricks()
    {
        Assert.Equal("Too many failures", RfbSession.Printable("Too many\r\n\tfailures", 200));
        Assert.Equal("evil.com", RfbSession.Printable("‮evil​.com‬", 200));
        Assert.Equal("abc…", RfbSession.Printable("abcdef", 3));
    }

    [Theory]
    // Unprotected network, no opt-in: identity-key sign-in only.
    [InlineData(false, false, false, false, false, true, false, false, false)]
    // Opt-in for a Mac that never proved an identity key: both, and one retry with the older one.
    [InlineData(false, false, true, false, false, true, true, true, false)]
    // Opt-in, but the Mac has proven its key before: identity-key only (a missing type 33 is a downgrade).
    [InlineData(false, false, true, true, false, true, false, false, false)]
    // Straight to a Tailscale node: both; a refusal there is worth remembering.
    [InlineData(false, true, false, true, false, true, true, true, true)]
    // A refusal learned over Tailscale: straight to the older sign-in there.
    [InlineData(false, true, false, false, true, false, true, false, true)]
    // ...but not on an open network, where it means nothing.
    [InlineData(false, false, false, false, true, true, false, false, false)]
    // SSH: both, unencrypted servers allowed inside it.
    [InlineData(true, false, false, true, false, true, true, true, true)]
    public void Sign_in_policy_never_offers_an_unverifiable_sign_in_on_an_open_network(
        bool ssh, bool tailscale, bool optIn, bool pinned, bool rsaRefused,
        bool rsa, bool dh, bool retry, bool remember)
    {
        var policy = SignInPolicy.Decide(ssh, tailscale, optIn, pinned, rsaRefused);
        Assert.Equal(rsa, policy.Auth.AllowRsa);
        Assert.Equal(dh, policy.Auth.AllowDiffieHellman);
        Assert.Equal(retry, policy.RetryWithDiffieHellman);
        Assert.Equal(remember, policy.RememberRsaRefusal);
        Assert.Equal(ssh, policy.AllowUnencrypted);
    }

    [Theory]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.100.100.100", false)]  // Tailscale's own service address
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.20", false)]     // e.g. behind a subnet router
    [InlineData("fd7a:115c:a1e0:ab12:4843:cd96:6258:b240", true)]
    [InlineData("fd7a:115c:a1e0:b1a:0:7:c0a8:114", false)] // 4via6: an IPv4 subnet behind a router
    [InlineData("fd7a:115c:a1e0::53", false)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    public void Only_tailscale_node_addresses_count_as_authenticated(string address, bool expected) =>
        Assert.Equal(expected, NetworkPath.IsTailscaleNodeAddress(IPAddress.Parse(address)));

    private sealed class HostKeyCheck(Func<SshHostKey, bool> decide) : IHostKeyVerifier
    {
        public SshHostKey? Seen { get; private set; }

        public ValueTask<bool> VerifyAsync(SshHostKey key, CancellationToken ct)
        {
            Seen = key;
            return ValueTask.FromResult(decide(key));
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Ssh_tunnel_carries_an_encrypted_session_without_a_local_listener()
    {
        int rfbPort = StartServer();
        int sshPort = FreePort();
        using var front = new SshFront(_desktop, _serverOptions, rfbPort);
        _ = front.StartAsync(sshPort, _cts.Token);
        await Task.Delay(200);

        var check = new HostKeyCheck(_ => true);
        await using var tunnel = await SshTunnel.OpenAsync("127.0.0.1", sshPort, "vesper", "evening-star", check, _cts.Token);
        Assert.Equal(front.HostKeyFingerprint, check.Seen!.Fingerprint);
        Assert.Equal("ecdsa-sha2-nistp256", tunnel.HostKey.Algorithm);

        var stream = await tunnel.OpenStreamAsync(rfbPort, _cts.Token);
        await using var session = await RfbSession.ConnectAsync(stream, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", IdentityVerifier = new Pin(_ => true), Auth = AuthPolicy.Any,
        }, _cts.Token);
        Assert.True(session.Info.Encrypted);
        _ = session.RunAsync(_cts.Token);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));
    }

    [Fact]
    public async Task Ssh_tunnel_refuses_an_untrusted_host_key_before_sending_the_password()
    {
        int rfbPort = StartServer();
        int sshPort = FreePort();
        using var front = new SshFront(_desktop, _serverOptions, rfbPort);
        _ = front.StartAsync(sshPort, _cts.Token);
        await Task.Delay(200);
        await Assert.ThrowsAsync<IdentityRejectedException>(() =>
            SshTunnel.OpenAsync("127.0.0.1", sshPort, "vesper", "evening-star", new HostKeyCheck(_ => false), _cts.Token));
    }

    [Fact]
    public async Task Ssh_tunnel_reports_a_wrong_password()
    {
        int rfbPort = StartServer();
        int sshPort = FreePort();
        using var front = new SshFront(_desktop, _serverOptions, rfbPort);
        _ = front.StartAsync(sshPort, _cts.Token);
        await Task.Delay(200);
        var e = await Assert.ThrowsAsync<AuthenticationFailedException>(() =>
            SshTunnel.OpenAsync("127.0.0.1", sshPort, "vesper", "wrong", new HostKeyCheck(_ => true), _cts.Token));
        Assert.True(e.CredentialsRejected);
    }

    private string ArdRsaFingerprint() => Vesper.Core.Security.ArdRsa.Fingerprint(_identity.ExportRSAPublicKey());

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        await Task.Delay(50);
        _desktop.Dispose();
        _identity.Dispose();
        _cts.Dispose();
    }
}
