using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Vesper.FakeMac;

// FakeMac: a stand-in for macOS Screen Sharing, for developing and testing Vesper without a Mac.
// It only ever listens on 127.0.0.1.

var options = Args.Parse(args);
if (options is null)
{
    Console.WriteLine("""
        Vesper.FakeMac — a loopback-only stand-in for macOS Screen Sharing.

          --port <n>          RFB port (default 5959)
          --user <name>       account name the fake Mac accepts (default "vesper")
          --password <pw>     its password (default "evening-star")
          --size <WxH>        screen size in points (default 1728x1117, a 16" MacBook Pro)
          --density <d>       pixels per point (default 2, Retina)
          --animate           keep part of the screen moving, to watch frame rates
          --auth <30|33|both> sign-in methods to offer (default both)
          --rfb38             speak plain RFB 3.8 instead of Apple's 003.889
          --new-identity      generate a new RSA identity key (tests Vesper's key-change warning)
          --verbose           log every protocol event
        """);
    return 1;
}

using var identity = LoadIdentity(options.NewIdentity);
using var desktop = new Desktop(options.Width, options.Height, options.Density, options.Animate);
var serverOptions = new FakeMacOptions(options.User, options.Password, identity,
    OfferRsa: options.Auth is "33" or "both", OfferDiffieHellman: options.Auth is "30" or "both",
    options.Rfb38, options.Verbose);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new TcpListener(IPAddress.Loopback, options.Port);
listener.Start();
Console.WriteLine($"FakeMac listening on 127.0.0.1:{options.Port} as \"{options.User}\" — {desktop.NativeWidth}×{desktop.NativeHeight} " +
                  $"({options.Width}×{options.Height} pt @{options.Density}x), {(options.Rfb38 ? "RFB 3.8" : "Apple 003.889")}, auth {options.Auth}");
Console.WriteLine($"Identity key fingerprint: SHA256:{Convert.ToBase64String(SHA256.HashData(identity.ExportRSAPublicKey())).TrimEnd('=')}");

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
    while (await timer.WaitForNextTickAsync(cts.Token)) desktop.Tick();
});

try
{
    while (!cts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        client.NoDelay = true;
        string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _ = Task.Run(async () =>
        {
            await using var connection = new FakeMacConnection(client.GetStream(), desktop, serverOptions, peer);
            try
            {
                await connection.RunAsync(cts.Token);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {peer}: {e.GetType().Name}: {e.Message}");
            }
        });
    }
}
catch (OperationCanceledException)
{
}
return 0;

static RSA LoadIdentity(bool regenerate)
{
    string path = Path.Combine(AppContext.BaseDirectory, "fakemac-identity.pem");
    var rsa = RSA.Create(2048);
    if (!regenerate && File.Exists(path))
    {
        rsa.ImportFromPem(File.ReadAllText(path));
    }
    else
    {
        File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
    }
    return rsa;
}

internal sealed record Args(int Port, string User, string Password, int Width, int Height, double Density,
    bool Animate, string Auth, bool Rfb38, bool NewIdentity, bool Verbose)
{
    public static Args? Parse(string[] argv)
    {
        var a = new Args(5959, "vesper", "evening-star", 1728, 1117, 2.0, false, "both", false, false, false);
        for (int i = 0; i < argv.Length; i++)
        {
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
            switch (argv[i])
            {
                case "--port": a = a with { Port = int.Parse(Next()) }; break;
                case "--user": a = a with { User = Next() }; break;
                case "--password": a = a with { Password = Next() }; break;
                case "--size":
                    var parts = Next().Split('x');
                    a = a with { Width = int.Parse(parts[0]), Height = int.Parse(parts[1]) };
                    break;
                case "--density": a = a with { Density = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "--animate": a = a with { Animate = true }; break;
                case "--auth": a = a with { Auth = Next() }; break;
                case "--rfb38": a = a with { Rfb38 = true }; break;
                case "--new-identity": a = a with { NewIdentity = true }; break;
                case "--verbose": a = a with { Verbose = true }; break;
                default: return null;
            }
        }
        return a;
    }
}
