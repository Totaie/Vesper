using System.Net;
using System.Security.Cryptography;
using Vesper.Services;

namespace Vesper.Updates;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpToDate,

    /// <summary>A newer version exists, but this copy can't install it itself (see <see cref="InstallKind.Unmanaged"/>).</summary>
    Available,

    Downloading,

    /// <summary>Downloaded and checked: installing needs only a restart.</summary>
    Ready,

    Failed,
}

/// <summary>
/// Looks for a newer Vesper on its public releases page and fetches it, checked.
///
/// The only things it sends anywhere are two HTTPS requests to GitHub for the latest release's
/// description and its signature (GitHub sees this computer's address and "Vesper/x.y.z"), and,
/// when there's an update, the download itself. The description must carry a valid signature
/// from Vesper's release key and a higher version than this one (so nobody can roll Vesper back);
/// the download must match the size and SHA-256 the description gives. Nothing is installed
/// until the user restarts Vesper or quits it.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "Totaie/Vesper";
    private const string ManifestName = "vesper-update.json";
    private const int MaxManifestBytes = 64 * 1024;
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly AppData _data;
    private readonly Version _current;
    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly string? _assetKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;

    public UpdateService(AppData data, Version current) : this(data, current, null, UpdateSigning.PublicKey, InstallInfo.AssetKey)
    {
    }

    /// <summary>For tests: another transport, release key and kind of install.</summary>
    internal UpdateService(AppData data, Version current, HttpMessageHandler? handler, string publicKey, string? assetKey)
    {
        _data = data;
        _publicKey = publicKey;
        _assetKey = assetKey;
        _current = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            // GitHub answers release downloads with a redirect to its file host; .NET never
            // follows one from HTTPS to plain HTTP.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Vesper/{_current.ToString(3)}");
    }

    public static Uri ReleasesPage => new($"https://github.com/{Repository}/releases/latest");

    public UpdateStatus Status { get; private set; }

    /// <summary>The newer version on offer, once one is found.</summary>
    public UpdateManifest? Offer { get; private set; }

    /// <summary>The checked download, when <see cref="Status"/> is Ready.</summary>
    public string? DownloadedFile { get; private set; }

    /// <summary>Why the last check failed, for the user.</summary>
    public string? Error { get; private set; }

    /// <summary>0 to 1 while downloading.</summary>
    public double Progress { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public Version CurrentVersion => _current;

    /// <summary>Raised on a background thread whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>Starts the automatic checks (when the setting allows them) and tidies earlier downloads.</summary>
    public void Start()
    {
        _timer ??= new Timer(_ =>
        {
            if (_data.Settings.CheckForUpdates) _ = CheckAsync(manual: false);
        }, null, FirstCheck, Interval);
    }

    /// <summary>
    /// Checks now. A manual check offers even a version the user skipped. Never throws: the outcome
    /// is in <see cref="Status"/>.
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (Status is UpdateStatus.Ready && !manual) return;
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        // A download that's already checked and waiting stays ready whatever this check finds.
        var ready = Status == UpdateStatus.Ready ? (Offer, DownloadedFile) : default;
        try
        {
            Error = null;
            Set(UpdateStatus.Checking);
            var manifest = await FetchManifestAsync(linked.Token).ConfigureAwait(false);
            LastChecked = DateTimeOffset.Now;
            if (ready.Offer is { } waiting && manifest.ParsedVersion <= waiting.ParsedVersion)
            {
                (Offer, DownloadedFile) = ready;
                Set(UpdateStatus.Ready);
                return;
            }
            if (manifest.ParsedVersion <= _current || (!manual && manifest.Version == _data.Settings.SkippedVersion))
            {
                Offer = null;
                Set(UpdateStatus.UpToDate);
                return;
            }
            Offer = manifest;
            if (_assetKey is not { } key || !manifest.Assets.TryGetValue(key, out var asset))
            {
                Set(UpdateStatus.Available);
                return;
            }
            Set(UpdateStatus.Downloading);
            DownloadedFile = await DownloadAsync(asset, manifest, linked.Token).ConfigureAwait(false);
            Set(UpdateStatus.Ready);
            Diagnostics.Add($"Update {manifest.Version} downloaded and checked; it installs when Vesper restarts.");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Restore(UpdateStatus.Idle);
        }
        catch (Exception e)
        {
            Error = Describe(e);
            Diagnostics.Add($"Update check failed: {e.GetType().Name}: {e.Message}");
            Restore(UpdateStatus.Failed);
        }
        finally
        {
            _gate.Release();
        }

        void Restore(UpdateStatus otherwise)
        {
            if (ready.Offer is not null)
            {
                (Offer, DownloadedFile) = ready;
                Set(UpdateStatus.Ready);
            }
            else
            {
                Set(otherwise);
            }
        }
    }

    /// <summary>Don't offer this version again (a newer one still will be).</summary>
    public void Skip()
    {
        if (Offer is null) return;
        _data.Settings.SkippedVersion = Offer.Version;
        _data.SaveSettings();
        Offer = null;
        DownloadedFile = null;
        Set(UpdateStatus.UpToDate);
    }

    private async Task<UpdateManifest> FetchManifestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        string latest = $"https://github.com/{Repository}/releases/latest/download/";
        byte[] json = await GetSmallAsync(new Uri(latest + ManifestName), timeout.Token);
        byte[] signature = await GetSmallAsync(new Uri(latest + ManifestName + ".sig"), timeout.Token);
        if (!UpdateSigning.Verify(json, System.Text.Encoding.ASCII.GetString(signature), _publicKey))
        {
            throw new UpdateRejectedException("The update description isn't signed by Vesper's release key, so it was ignored.");
        }
        return UpdateManifest.Parse(json);
    }

    private async Task<byte[]> GetSmallAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateRejectedException("No release has been published yet.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxManifestBytes) throw new UpdateRejectedException("The update description is too large.");
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    private async Task<string> DownloadAsync(UpdateAsset asset, UpdateManifest manifest, CancellationToken ct)
    {
        string folder = _data.UpdatesDirectory;
        AppData.CreatePrivateDirectory(folder);
        string target = Path.Combine(folder, asset.File);
        foreach (var old in Directory.EnumerateFiles(folder).Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(old); // earlier versions' downloads
        }
        if (File.Exists(target) && await MatchesAsync(target, asset, ct).ConfigureAwait(false)) return target;

        string partial = target + ".partial";
        try
        {
            var uri = new Uri($"https://github.com/{Repository}/releases/download/v{manifest.Version}/{Uri.EscapeDataString(asset.File)}");
            // No single read may stall for long: a sleep or network change mid-download otherwise
            // leaves it "downloading" for ever.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(StallTimeout);
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            {
                throw new UpdateRejectedException("The download isn't the size the signed description gives.");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                byte[] buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    total += n;
                    if (total > asset.Size) throw new UpdateRejectedException("The download is larger than the signed description says.");
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    double progress = (double)total / asset.Size;
                    if (progress - Progress >= 0.01 || total == asset.Size)
                    {
                        Progress = progress;
                        Changed?.Invoke();
                    }
                }
                if (total != asset.Size) throw new UpdateRejectedException("The download ended early.");
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
            {
                throw new UpdateRejectedException("The download doesn't match the checksum in the signed description, so it was thrown away.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Whether a file on disk is exactly the download the description pins.</summary>
    public static async Task<bool> MatchesAsync(string path, UpdateAsset asset, CancellationToken ct = default)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            if (file.Length != asset.Size) return false;
            byte[] digest = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest) == asset.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Describe(Exception e) => e switch
    {
        UpdateRejectedException or InvalidDataException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"GitHub answered {(int)code} {code}.",
        HttpRequestException => "Couldn't reach GitHub to check for updates.",
        TaskCanceledException or TimeoutException => "GitHub took too long to answer.",
        IOException or UnauthorizedAccessException => "The update couldn't be saved: " + e.Message,
        _ => e.Message,
    };

    private void Set(UpdateStatus status)
    {
        Status = status;
        if (status != UpdateStatus.Downloading) Progress = 0;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Dispose();
        _http.Dispose();
    }
}

/// <summary>An update that failed a check and was refused.</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);
