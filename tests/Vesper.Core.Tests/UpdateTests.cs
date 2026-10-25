using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vesper.Services;
using Vesper.Updates;

namespace Vesper.Core.Tests;

/// <summary>The updater installs nothing that isn't signed, newer, and exactly what the signature describes.</summary>
public sealed class UpdateTests : IDisposable
{
    private const string AssetKey = "windows-x64-setup";
    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vesper-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _installer = RandomNumberGenerator.GetBytes(200_000);

    private string PublicKey => Convert.ToBase64String(_releaseKey.ExportSubjectPublicKeyInfo());

    [Fact]
    public void A_signature_verifies_only_for_the_exact_description_and_key()
    {
        byte[] json = Manifest("0.9.0").ToJson();
        string signature = UpdateSigning.Sign(json, _releaseKey);
        Assert.True(UpdateSigning.Verify(json, signature, PublicKey));

        byte[] tampered = (byte[])json.Clone();
        tampered[^3] ^= 1;
        Assert.False(UpdateSigning.Verify(tampered, signature, PublicKey));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSigning.Verify(json, signature, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));

        // A signature over the bare bytes (no "Vesper update manifest" label) is someone else's.
        string unlabelled = Convert.ToBase64String(_releaseKey.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(UpdateSigning.Verify(json, unlabelled, PublicKey));
        Assert.False(UpdateSigning.Verify(json, "not base64!", PublicKey));
    }

    [Fact]
    public void The_built_in_release_key_is_a_P256_key() =>
        Assert.False(UpdateSigning.Verify("{}"u8, Convert.ToBase64String(new byte[64])));

    [Theory]
    [InlineData("""{"product":"Other","version":"1.0.0","assets":{}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0","assets":{}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0.1","assets":{}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"../evil.exe","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"a/b.exe","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"https:evil","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"a.exe","size":0,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"a.exe","size":99999999999,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"x":{"file":"a.exe","size":1,"sha256":"XYZ"}}}""")]
    [InlineData("""{"product":"Vesper","version":"1.0.0","assets":{"Bad Key":{"file":"a.exe","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}}}""")]
    public void Descriptions_naming_anything_odd_are_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task A_signed_newer_version_is_downloaded_and_checked()
    {
        var (service, _) = Service(Manifest("0.9.0"));
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Ready, service.Status);
        Assert.Equal(_installer, File.ReadAllBytes(service.DownloadedFile!));
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_never_offered()
    {
        foreach (string version in new[] { "0.2.0", "0.1.9" })
        {
            var (service, http) = Service(Manifest(version));
            await service.CheckAsync(manual: true);
            Assert.Equal(UpdateStatus.UpToDate, service.Status);
            Assert.Null(service.Offer);
            Assert.DoesNotContain(http.Requests, r => r.EndsWith(".exe"));
        }
    }

    [Fact]
    public async Task An_unsigned_description_is_ignored()
    {
        var (service, http) = Service(Manifest("0.9.0"), signWith: ECDsa.Create(ECCurve.NamedCurves.nistP256));
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Contains("isn't signed", service.Error);
        Assert.DoesNotContain(http.Requests, r => r.EndsWith(".exe"));
    }

    [Fact]
    public async Task A_download_that_doesnt_match_its_checksum_is_thrown_away()
    {
        var (service, http) = Service(Manifest("0.9.0"));
        http.Installer = (byte[])_installer.Clone();
        http.Installer[1000] ^= 0xFF;
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Contains("checksum", service.Error);
        Assert.Empty(Directory.Exists(UpdatesFolder) ? Directory.GetFiles(UpdatesFolder) : []);
    }

    [Fact]
    public async Task A_download_longer_than_described_is_refused()
    {
        var (service, http) = Service(Manifest("0.9.0"));
        http.Installer = [.. _installer, .. new byte[10]];
        http.HideLength = true;
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Failed, service.Status);
        Assert.Empty(Directory.Exists(UpdatesFolder) ? Directory.GetFiles(UpdatesFolder) : []);
    }

    [Fact]
    public async Task A_skipped_version_is_offered_again_only_when_asked()
    {
        var (service, _) = Service(Manifest("0.9.0"));
        _data!.Settings.SkippedVersion = "0.9.0";
        await service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.UpToDate, service.Status);
        await service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Ready, service.Status);
    }

    [Fact]
    public void A_portable_update_is_staged_beside_the_app_and_marked_ready_last()
    {
        string root = Path.Combine(_folder, "portable");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        string zip = Zip(("Vesper-0.9.0-win-x64/Vesper.exe", "launcher"), ("Vesper-0.9.0-win-x64/app/Vesper.exe", "app"),
            ("Vesper-0.9.0-win-x64/app/Assets/a.png", "png"), ("Vesper-0.9.0-win-x64/Start here.txt", "readme"));
        UpdateInstaller.StagePortable(zip, root, "0.9.0");
        Assert.Equal("app", File.ReadAllText(Path.Combine(root, "app.new", "Vesper.exe")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(root, "app.new", "Assets", "a.png")));
        Assert.Equal("0.9.0", File.ReadAllText(Path.Combine(root, "app.new", InstallInfo.ReadyMarker)));
        Assert.Equal("launcher", File.ReadAllText(Path.Combine(root, "Vesper.exe")));
        Assert.False(File.Exists(Path.Combine(root, "app.new", "Start here.txt")));
    }

    [Theory]
    [InlineData("Vesper-0.9.0-win-x64/app/../../evil.txt")]
    [InlineData("Vesper-0.9.0-win-x64/app/C:/evil.txt")]
    public void A_portable_update_cant_write_outside_its_folder(string entry)
    {
        string root = Path.Combine(_folder, "portable-evil");
        Directory.CreateDirectory(Path.Combine(root, "app"));
        string zip = Zip(("Vesper-0.9.0-win-x64/app/Vesper.exe", "app"), (entry, "evil"));
        Assert.ThrowsAny<Exception>(() => UpdateInstaller.StagePortable(zip, root, "0.9.0"));
        Assert.False(File.Exists(Path.Combine(root, InstallInfo.ReadyMarker)));
        Assert.False(File.Exists(Path.Combine(root, "app.new", InstallInfo.ReadyMarker)));
        Assert.False(File.Exists(Path.Combine(_folder, "evil.txt")));
    }

    private string UpdatesFolder => Path.Combine(_folder, "data", "updates");

    private UpdateManifest Manifest(string version) => new()
    {
        Product = "Vesper",
        Version = version,
        Published = DateTimeOffset.UtcNow,
        Summary = "Test",
        Assets =
        {
            [AssetKey] = new UpdateAsset
            {
                File = $"Vesper-{version}-Setup-x64.exe",
                Size = _installer.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(_installer)),
            },
        },
    };

    private AppData? _data;

    private (UpdateService Service, FakeGitHub Http) Service(UpdateManifest manifest, ECDsa? signWith = null)
    {
        byte[] json = manifest.ToJson();
        var http = new FakeGitHub(json, UpdateSigning.Sign(json, signWith ?? _releaseKey), _installer);
        _data = new AppData(Path.Combine(_folder, "data"));
        return (new UpdateService(_data, new Version(0, 2, 0), http, PublicKey, AssetKey), http);
    }

    private string Zip(params (string Name, string Text)[] entries)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    public void Dispose()
    {
        _releaseKey.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Answers like GitHub's release download URLs.</summary>
    private sealed class FakeGitHub(byte[] manifest, string signature, byte[] installer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public byte[] Installer { get; set; } = installer;
        public bool HideLength { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            Assert.StartsWith("https://github.com/Totaie/Vesper-releases/releases/", url);
            HttpContent content = url.EndsWith("/vesper-update.json") ? new ByteArrayContent(manifest)
                : url.EndsWith("/vesper-update.json.sig") ? new StringContent(signature)
                : url.Contains("/download/v") ? (HideLength ? new StreamContent(new MemoryStream(Installer)) : new ByteArrayContent(Installer))
                : null!;
            if (content is null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (HideLength) content.Headers.ContentLength = null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
