using System.Text.Json;
using Vesper.Models;

namespace Vesper.Services;

/// <summary>
/// Vesper's saved state under %LOCALAPPDATA%\Vesper: Macs and settings as JSON (no secrets), and the
/// encrypted password vault. Writes are atomic: a crash mid-save leaves the previous file intact.
/// </summary>
internal sealed class AppData
{
    private readonly object _sync = new();

    public AppData()
    {
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper");
        System.IO.Directory.CreateDirectory(Directory);
        Profiles = Load(ProfilesPath, VesperJson.Default.ListMacProfile) ?? [];
        Settings = Load(SettingsPath, VesperJson.Default.AppSettings) ?? new AppSettings();
    }

    public string Directory { get; }
    private string ProfilesPath => Path.Combine(Directory, "macs.json");
    private string SettingsPath => Path.Combine(Directory, "settings.json");
    public string VaultPath => Path.Combine(Directory, "vault.bin");

    public List<MacProfile> Profiles { get; }
    public AppSettings Settings { get; }

    public event Action? ProfilesChanged;

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
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
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
