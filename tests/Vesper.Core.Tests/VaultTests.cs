using Vesper.Models;
using Vesper.Security;
using Vesper.Services;

namespace Vesper.Core.Tests;

/// <summary>
/// The password vault with this computer's own key protection: DPAPI on Windows, the login keyring
/// on Linux (only where VESPER_TEST_KEYRING=1 says an unlocked test keyring is running, as in CI,
/// since it uses the same keyring entry as Vesper itself).
/// </summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vesper-vault-tests-" + Guid.NewGuid().ToString("N"));

    public VaultTests() => Directory.CreateDirectory(_folder);

    private static IVaultKeyProtector Protector()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VESPER_TEST_KEYRING") == "1",
            "Needs DPAPI, or a test login keyring (VESPER_TEST_KEYRING=1)");
        var protector = IVaultKeyProtector.ForThisComputer();
        Assert.True(protector.IsAvailable, protector.UnavailableReason);
        return protector;
    }

    [Fact]
    public async Task A_saved_password_survives_locking_and_reopening()
    {
        var mac = new MacProfile { Host = "studio.local", Username = "me" };
        string path = Path.Combine(_folder, "vault.bin");
        var vault = new SecretVault(path, Protector());
        Assert.True(await vault.UnlockAsync());
        vault.SetPassword(mac, "correct horse");
        vault.Lock();
        Assert.Equal(VaultLookup.Missing, vault.TryGetPassword(mac, out _));

        var reopened = new SecretVault(path, IVaultKeyProtector.ForThisComputer());
        Assert.True(await reopened.UnlockAsync());
        Assert.Equal(VaultLookup.Found, reopened.TryGetPassword(mac, out string password));
        Assert.Equal("correct horse", password);
    }

    [Fact]
    public async Task A_password_isnt_released_for_a_Mac_changed_behind_Vesper()
    {
        var mac = new MacProfile { Host = "studio.local", Username = "me", PinnedIdentityFingerprint = "SHA256:abc" };
        var vault = new SecretVault(Path.Combine(_folder, "vault.bin"), Protector());
        Assert.True(await vault.UnlockAsync());
        vault.SetPassword(mac, "secret");

        var moved = mac.Clone();
        moved.Host = "evil.example";
        Assert.Equal(VaultLookup.Mismatch, vault.TryGetPassword(moved, out string leaked));
        Assert.Equal("", leaked);

        // Moving it with Vesper's own edit (before → after) carries it over.
        Assert.True(vault.Rebind(mac.Id, SecretVault.Binding(mac), SecretVault.Binding(moved)));
        Assert.Equal(VaultLookup.Found, vault.TryGetPassword(moved, out _));

        // But a stale "before" can't.
        var elsewhere = mac.Clone();
        elsewhere.Host = "other.example";
        Assert.False(vault.Rebind(mac.Id, SecretVault.Binding(mac), SecretVault.Binding(elsewhere)));
    }

    [Fact]
    public async Task A_tampered_vault_file_doesnt_open()
    {
        var mac = new MacProfile { Host = "studio.local", Username = "me" };
        string path = Path.Combine(_folder, "vault.bin");
        var vault = new SecretVault(path, Protector());
        Assert.True(await vault.UnlockAsync());
        vault.SetPassword(mac, "secret");
        vault.Lock();

        byte[] file = File.ReadAllBytes(path);
        file[^5] ^= 0x40;
        File.WriteAllBytes(path, file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SecretVault(path, IVaultKeyProtector.ForThisComputer()).UnlockAsync());
    }

    [Fact]
    public async Task Windows_unlocking_at_once_share_one_unlock()
    {
        var protector = new CountingProtector(Protector());
        string path = Path.Combine(_folder, "vault.bin");
        var mac = new MacProfile { Host = "studio.local", Username = "me" };
        var first = new SecretVault(path, protector);
        Assert.True(await first.UnlockAsync());
        first.SetPassword(mac, "secret");

        var vault = new SecretVault(path, protector);
        protector.Unprotects = 0;
        var a = vault.UnlockAsync();
        var b = vault.UnlockAsync();
        Assert.True(await a && await b);
        Assert.Equal(1, protector.Unprotects);
    }

    [Fact]
    public void A_second_copy_hands_over_to_the_first()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), "Unix domain sockets");
        int shown = 0;
        using var first = SingleInstance.Claim(_folder, () => Interlocked.Increment(ref shown));
        Assert.NotNull(first);
        Assert.Null(SingleInstance.Claim(_folder, () => { }));
        SpinWait.SpinUntil(() => Volatile.Read(ref shown) == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, shown);
    }

    private sealed class CountingProtector(IVaultKeyProtector inner) : IVaultKeyProtector
    {
        public int Unprotects;
        public bool IsAvailable => inner.IsAvailable;
        public string UnavailableReason => inner.UnavailableReason;
        public byte[] Protect(byte[] data) => inner.Protect(data);
        public byte[] Unprotect(byte[] data)
        {
            Interlocked.Increment(ref Unprotects);
            Thread.Sleep(50);
            return inner.Unprotect(data);
        }
        public void Forget() => inner.Forget();
    }

    [Fact]
    public void Settings_from_disk_are_brought_back_into_range()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{ "ScrollSpeed": 1e308, "LockAfterMinutes": 999999999 }""");
        File.WriteAllText(Path.Combine(_folder, "macs.json"), """[ { "Host": "studio.local", "Port": 99999, "SshPort": -4 } ]""");
        var data = new AppData(_folder);
        Assert.Equal(3, data.Settings.ScrollSpeed);
        Assert.Equal(15, data.Settings.LockAfterMinutes);
        Assert.Equal(5900, data.Profiles[0].Port);
        Assert.Equal(22, data.Profiles[0].SshPort);
    }

    [Fact]
    public void Linux_keeps_Vesper_data_private_to_its_user()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Unix permissions");
        string dir = Path.Combine(_folder, "loose");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
        var data = new AppData(dir);
        data.SaveSettings();
        Assert.Equal((UnixFileMode)0b111_000_000, File.GetUnixFileMode(dir));
        Assert.Equal((UnixFileMode)0b110_000_000, File.GetUnixFileMode(Path.Combine(dir, "settings.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
