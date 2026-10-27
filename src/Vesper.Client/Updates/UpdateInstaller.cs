using System.Diagnostics;
using System.IO.Compression;

namespace Vesper.Updates;

/// <summary>
/// Installs a checked download the way this copy of Vesper was installed. The download is checked
/// against the signed description again right before it's used.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Whether this copy can install updates itself.</summary>
    public static bool CanInstall => InstallInfo.AssetKey is not null;

    /// <summary>
    /// Starts installing. On Windows the caller should quit straight away (Setup or the launcher
    /// takes over and, with <paramref name="relaunch"/>, opens the new version); on Linux this
    /// waits for pacman, then starts the new version if asked. Throws with a message for the user.
    /// </summary>
    public static async Task InstallAsync(UpdateManifest manifest, string file, bool relaunch, CancellationToken ct = default)
    {
        if (InstallInfo.AssetKey is not { } key || !manifest.Assets.TryGetValue(key, out var asset))
        {
            throw new InvalidOperationException("This copy of Vesper can't install updates itself.");
        }
        if (!await UpdateService.MatchesAsync(file, asset, ct).ConfigureAwait(false))
        {
            throw new UpdateRejectedException("The downloaded update changed on disk since it was checked, so it wasn't used.");
        }
        switch (InstallInfo.Kind)
        {
            case InstallKind.WindowsSetup:
                RunSetup(file, relaunch);
                break;
            case InstallKind.WindowsPortable:
                await Task.Run(() => StagePortable(file, InstallInfo.PortableRoot!, manifest.Version), ct).ConfigureAwait(false);
                if (relaunch) StartLauncher(InstallInfo.PortableRoot!);
                break;
            case InstallKind.LinuxAppImage when !OperatingSystem.IsWindows():
                string image = ReplaceAppImage(file, InstallInfo.AppImagePath!);
                if (relaunch) Process.Start(new ProcessStartInfo(image) { UseShellExecute = false });
                break;
            case InstallKind.LinuxPackage:
                await RunPacmanAsync(file, ct).ConfigureAwait(false);
                if (relaunch) Process.Start(new ProcessStartInfo(InstallInfo.LinuxLauncher) { UseShellExecute = false });
                break;
            default:
                throw new InvalidOperationException("This copy of Vesper can't install updates itself.");
        }
    }

    /// <summary>
    /// Installs while the app is quitting, from a UI thread that's blocked waiting for it: the work
    /// runs on the thread pool so nothing tries to resume on that thread.
    /// </summary>
    public static void InstallOnExit(UpdateManifest manifest, string file) =>
        Task.Run(() => InstallAsync(manifest, file, relaunch: false)).GetAwaiter().GetResult();

    /// <summary>Removes what a finished portable update left behind (the previous app folder).</summary>
    public static void CleanUpAfterUpdate()
    {
        if (InstallInfo.PortableRoot is not { } root) return;
        foreach (var old in Directory.EnumerateDirectories(root, "app.old-*"))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use by another copy; next time.
            }
        }
    }

    private static void RunSetup(string setup, bool relaunch)
    {
        // Inno Setup, per-user (no administrator prompt). It waits for Vesper to close, and its
        // relaunch parameter is read by Vesper's own setup script.
        var start = new ProcessStartInfo(setup) { UseShellExecute = false };
        foreach (string arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CLOSEAPPLICATIONS" })
        {
            start.ArgumentList.Add(arg);
        }
        start.ArgumentList.Add(relaunch ? "/RELAUNCH=yes" : "/RELAUNCH=no");
        Process.Start(start);
    }

    /// <summary>
    /// Unpacks the new portable zip's app folder beside the current one as app.new, then marks it
    /// complete. The launcher swaps it in the next time it starts, once Vesper has quit.
    /// </summary>
    internal static void StagePortable(string zip, string root, string version)
    {
        string staging = Path.Combine(root, "app.new");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string stagingPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging)) + Path.DirectorySeparatorChar;
        string? launcher = null;

        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                // "Vesper-x.y.z-win-x64/app/...": drop the top folder.
                string name = entry.FullName.Replace('\\', '/');
                int slash = name.IndexOf('/');
                if (slash < 0) continue;
                string inside = name[(slash + 1)..];
                if (inside == "Vesper.exe")
                {
                    launcher = Path.Combine(root, "Vesper.exe.new");
                    entry.ExtractToFile(launcher, overwrite: true);
                    continue;
                }
                if (!inside.StartsWith("app/", StringComparison.Ordinal) || inside.EndsWith('/')) continue;
                string target = Path.GetFullPath(Path.Combine(staging, inside[4..]));
                if (!target.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateRejectedException("The update contains a file that would land outside Vesper's folder.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        if (!File.Exists(Path.Combine(staging, "Vesper.exe"))) throw new UpdateRejectedException("The update is missing Vesper itself.");

        // The launcher isn't running now (it exits as soon as it has started Vesper).
        if (launcher is not null) File.Move(launcher, Path.Combine(root, "Vesper.exe"), overwrite: true);
        File.WriteAllText(Path.Combine(staging, InstallInfo.ReadyMarker), version);
    }

    /// <summary>
    /// Puts the new AppImage where the running one is: written beside it, made executable, then
    /// renamed over it in one step, so the file is always either the old version or the new one.
    /// The running copy keeps its own (now unlinked) file until it quits.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string ReplaceAppImage(string download, string image)
    {
        string staging = image + ".new";
        try
        {
            File.Copy(download, staging, overwrite: true);
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staging, image, overwrite: true);
            return image;
        }
        catch (UnauthorizedAccessException)
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw new InvalidOperationException($"Vesper can't replace {image} (it isn't yours to change). Download the new AppImage from the releases page instead.");
        }
    }

    private static void StartLauncher(string root) =>
        Process.Start(new ProcessStartInfo(Path.Combine(root, "Vesper.exe")) { UseShellExecute = false, WorkingDirectory = root });

    private static async Task RunPacmanAsync(string package, CancellationToken ct)
    {
        const string pkexec = "/usr/bin/pkexec", pacman = "/usr/bin/pacman";
        if (!File.Exists(pkexec))
        {
            throw new InvalidOperationException($"Installing needs polkit's pkexec. You can install the update yourself with:\nsudo pacman -U {package}");
        }
        // Absolute paths, so nothing on PATH is consulted; pkexec asks for your password.
        var start = new ProcessStartInfo(pkexec) { UseShellExecute = false };
        foreach (string arg in new[] { pacman, "-U", "--noconfirm", "--", package }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pkexec didn't start.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        switch (process.ExitCode)
        {
            case 0:
                return;
            case 126 or 127:
                throw new InvalidOperationException("The update wasn't installed: authorisation was cancelled or refused.");
            default:
                throw new InvalidOperationException($"pacman couldn't install the update (exit code {process.ExitCode}). You can try it yourself with:\nsudo pacman -U {package}");
        }
    }
}
