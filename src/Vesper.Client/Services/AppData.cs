using System.Text.Json;
using Vesper.Models;

namespace Vesper.Services;

/// <summary>
/// Vesper's saved state: Macs and settings as JSON (no secrets), and the encrypted password vault.
/// On Windows it lives in %LOCALAPPDATA%\Vesper; on Linux in ~/.config/vesper (or under
/// $XDG_CONFIG_HOME), readable only by you. Writes are atomic: a crash mid-save leaves the
/// previous file intact.
/// </summary>
public sealed class AppData
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly object _sync = new();

    public AppData() : this(DefaultDirectory())
    {
    }

    public AppData(string directory)
    {
        Directory = directory;
        CreatePrivateDirectory(Directory);
        Profiles = Load(ProfilesPath, VesperJson.Default.ListMacProfile) ?? [];
        Settings = Load(SettingsPath, VesperJson.Default.AppSettings) ?? new AppSettings();
    }

    public string Directory { get; }
    private string ProfilesPath => Path.Combine(Directory, "macs.json");
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    public string VaultPath => Path.Combine(Directory, "vault.bin");

    /// <summary>Where downloaded updates wait, checked, until they're installed.</summary>
    public string UpdatesDirectory => Path.Combine(Directory, "updates");

    public List<MacProfile> Profiles { get; }
    public AppSettings Settings { get; }

    public event Action? ProfilesChanged;

    public static string DefaultDirectory()
    {
#if DEBUG
        // Development builds can keep their Macs and passwords apart from the installed app's.
        if (Environment.GetEnvironmentVariable("VESPER_DEV_DATA") is { Length: > 0 } dev) return dev;
#endif
        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper")
            // .NET resolves this to $XDG_CONFIG_HOME, or ~/.config.
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vesper");
    }

    /// <summary>Creates a folder only you can open (on Linux; Windows profiles are already private).</summary>
    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(path);
            return;
        }
        System.IO.Directory.CreateDirectory(path, PrivateDirectory);
        // A folder made earlier, or by something else, is tightened too.
        if ((File.GetUnixFileMode(path) & ~PrivateDirectory) != 0) File.SetUnixFileMode(path, PrivateDirectory);
    }

    public MacProfile? Find(Guid id)
    {
        lock (_sync) return Profiles.FirstOrDefault(p => p.Id == id);
    }

    public void Upsert(MacProfile profile)
    {
        lock (_sync)
        {
            int i = Profiles.FindIndex(p => p.Id == profile.Id);
            if (i >= 0) Profiles[i] = profile; else Profiles.Add(profile);
            SaveProfilesLocked();
        }
        ProfilesChanged?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            Profiles.RemoveAll(p => p.Id == id);
            SaveProfilesLocked();
        }
        ProfilesChanged?.Invoke();
    }

    public void SaveProfiles()
    {
        lock (_sync) SaveProfilesLocked();
        ProfilesChanged?.Invoke();
    }

    public void SaveSettings()
    {
        lock (_sync) WriteAtomic(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(Settings, VesperJson.Default.AppSettings));
    }

    private void SaveProfilesLocked() =>
        WriteAtomic(ProfilesPath, JsonSerializer.SerializeToUtf8Bytes(Profiles, VesperJson.Default.ListMacProfile));

    public static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        using (var f = new FileStream(temp, options))
        {
            f.Write(bytes);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), type) : null;
        }
        catch (JsonException)
        {
            // A damaged file is set aside rather than overwritten, so nothing is lost silently.
            File.Move(path, path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return null;
        }
    }
}
