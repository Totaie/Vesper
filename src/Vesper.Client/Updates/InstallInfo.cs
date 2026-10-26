using System.Runtime.InteropServices;

namespace Vesper.Updates;

public enum InstallKind
{
    /// <summary>Installed on Windows by Vesper's Setup: updated by running the next Setup silently.</summary>
    WindowsSetup,

    /// <summary>Windows, from the portable zip (a launcher beside an "app" folder): updated in place by swapping that folder.</summary>
    WindowsPortable,

    /// <summary>Installed on Arch Linux by pacman, in /usr/lib/vesper (0.2.0): updated with pacman, after your password.</summary>
    LinuxPackage,

    /// <summary>Linux, run from Vesper's AppImage: updated by replacing that one file with the new one.</summary>
    LinuxAppImage,

    /// <summary>A development build or a copy Vesper didn't install: told about updates, but doesn't install them.</summary>
    Unmanaged,
}

/// <summary>How this copy of Vesper was installed, which decides how it updates.</summary>
public static class InstallInfo
{
    public const string LinuxInstallDirectory = "/usr/lib/vesper/";
    public const string LinuxLauncher = "/usr/bin/vesper";

    /// <summary>The marker a finished portable update leaves in app.new; the launcher looks for it.</summary>
    public const string ReadyMarker = ".vesper-ready";

    public static InstallKind Kind { get; } = Detect(AppContext.BaseDirectory);

    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Which download in an update description fits this install, if any.</summary>
    public static string? AssetKey => Kind switch
    {
        InstallKind.WindowsSetup => $"windows-{Architecture}-setup",
        InstallKind.WindowsPortable => $"windows-{Architecture}-portable",
        InstallKind.LinuxPackage when Architecture == "x64" => "linux-x64-pacman",
        InstallKind.LinuxAppImage when Architecture == "x64" => "linux-x64-appimage",
        _ => null,
    };

    /// <summary>
    /// The AppImage file this copy runs from. Its runtime says so in $APPIMAGE and mounts the app at
    /// $APPDIR; both must agree with where Vesper actually is, and the file must be one you can
    /// replace, for Vesper to update it.
    /// </summary>
    public static string? AppImagePath { get; } = FindAppImage(AppContext.BaseDirectory);

    /// <summary>For a portable copy: the folder with the launcher, Start here.txt and app.</summary>
    public static string? PortableRoot => Kind == InstallKind.WindowsPortable ? PortableRootOf(AppContext.BaseDirectory) : null;

    internal static InstallKind Detect(string baseDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(Path.Combine(baseDirectory, "unins000.exe"))) return InstallKind.WindowsSetup;
            if (PortableRootOf(baseDirectory) is not null) return InstallKind.WindowsPortable;
            return InstallKind.Unmanaged;
        }
        if (OperatingSystem.IsLinux() && baseDirectory == LinuxInstallDirectory) return InstallKind.LinuxPackage;
        if (OperatingSystem.IsLinux() && FindAppImage(baseDirectory) is not null) return InstallKind.LinuxAppImage;
        return InstallKind.Unmanaged;
    }

    private static string? FindAppImage(string baseDirectory)
    {
        if (!OperatingSystem.IsLinux()) return null;
        string? image = Environment.GetEnvironmentVariable("APPIMAGE");
        string? mount = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(mount) || !Path.IsPathRooted(image)) return null;
        string root = Path.TrimEndingDirectorySeparator(mount) + "/";
        if (!baseDirectory.StartsWith(root, StringComparison.Ordinal)) return null;
        var file = new FileInfo(image);
        return file.Exists && file.LinkTarget is null ? file.FullName : null;
    }

    private static string? PortableRootOf(string baseDirectory)
    {
        var app = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (!string.Equals(app.Name, "app", StringComparison.OrdinalIgnoreCase) || app.Parent is not { } root) return null;
        return File.Exists(Path.Combine(root.FullName, "Vesper.exe")) ? root.FullName : null;
    }
}
