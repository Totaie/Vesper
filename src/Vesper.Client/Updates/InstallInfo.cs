using System.Runtime.InteropServices;

namespace Vesper.Updates;

public enum InstallKind
{
    /// <summary>Installed on Windows by Vesper's Setup: updated by running the next Setup silently.</summary>
    WindowsSetup,

    /// <summary>Windows, from the portable zip (a launcher beside an "app" folder): updated in place by swapping that folder.</summary>
    WindowsPortable,

    /// <summary>Installed on Arch Linux by pacman, in /usr/lib/vesper: updated with pacman, after your password.</summary>
    LinuxPackage,

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
        _ => null,
    };

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
        return InstallKind.Unmanaged;
    }

    private static string? PortableRootOf(string baseDirectory)
    {
        var app = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (!string.Equals(app.Name, "app", StringComparison.OrdinalIgnoreCase) || app.Parent is not { } root) return null;
        return File.Exists(Path.Combine(root.FullName, "Vesper.exe")) ? root.FullName : null;
    }
}
