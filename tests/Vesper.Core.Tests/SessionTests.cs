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

    private int StartServer(bool offerRsa = true, bool offerDh = true, bool rfb38 = false)
    {
        _serverOptions = new FakeMacOptions("vesper", "evening-star", _identity, offerRsa, offerDh, rfb38, Verbose: false);
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

        public ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct)
        {
            Seen = identity;
            return ValueTask.FromResult(decide(identity));
        }
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
        await Assert.ThrowsAsync<AuthenticationFailedException>(() => ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.RsaOnly,
        }));

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
    public async Task Plain_rfb_38_servers_still_work()
    {
        int port = StartServer(offerRsa: false, rfb38: true);
        await using var session = await ConnectAsync(port, new RfbSessionOptions
        {
            Username = "vesper", Password = "evening-star", Auth = AuthPolicy.Any,
        });
        Assert.False(session.Info.AppleExtensions);
        _ = session.RunAsync(_cts.Token);
        await WaitForAsync(() => FramebufferMatchesDesktop(session));
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
