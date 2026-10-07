using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vesper.Interop;
using Vesper.Models;

namespace Vesper.Services;

internal enum VaultLookup
{
    Found,
    Missing,

    /// <summary>
    /// A password is saved, but for a different address, account or trusted identity than the
    /// Mac's settings now hold, changed without Vesper re-sealing it. It isn't handed out.
    /// </summary>
    Mismatch,
}

/// <summary>
/// Saved passwords, encrypted at rest.
///
/// The secrets are sealed with AES-256-GCM under a random vault key. That key is protected by
/// Windows DPAPI (bound to this Windows account), and, when Windows Hello is required, also
/// wrapped under a key derived from a Windows Hello signature, so another program running as you
/// can't open the vault without a visible Hello prompt. Unlocked secrets live in memory only
/// until the vault locks, and are wiped when it does.
///
/// Each password is sealed together with a fingerprint of where it may go: the Mac's address,
/// ports, account, transport, sign-in allowance and pinned keys. Those settings live in
/// macs.json, which any program running as you can edit; if they no longer match, the password
/// isn't released, so an edited profile can't quietly send a saved password somewhere else.
///
/// File layout: "VSPR", version (2; 1 had no fingerprints), mode (0 DPAPI, 1 DPAPI + Hello),
/// u32 n, n bytes of DPAPI blob, 12-byte nonce, 16-byte tag, u32 m, m bytes of ciphertext. The
/// 6-byte header is authenticated.
/// </summary>
internal sealed class SecretVault
{
    private static readonly byte[] Magic = "VSPR"u8.ToArray();
    private static readonly byte[] DpapiEntropy = "Vesper vault v1"u8.ToArray();
    private static readonly byte[] HelloInfo = "Vesper vault key-encryption key v1"u8.ToArray();
    private static readonly byte[] BindingDomain = "Vesper password binding v1"u8.ToArray();
    private const byte Version = 2;
    private const int BindingLength = 32;

    private sealed class Entry(byte[] binding, byte[] secret)
    {
        public byte[] Binding { get; set; } = binding;
        public byte[] Secret { get; } = secret;
    }

    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<Guid, Entry> _secrets = [];
    private byte[]? _vaultKey;
    private byte[]? _helloSalt;

    public SecretVault(string path)
    {
        _path = path;
        UsesHello = File.Exists(path) && ReadMode() == 1;
    }

    public bool IsUnlocked
    {
        get { lock (_sync) return _vaultKey is not null; }
    }

    public bool Exists => File.Exists(_path);

    public bool UsesHello { get; private set; }

    /// <summary>The user's setting: a new vault is created Hello-protected, and an unprotected one isn't trusted.</summary>
    public bool RequireHello { get; set; }

    public event Action? Locked;

    /// <summary>
    /// Where a profile's password may be sent. Anything that changes the destination or what
    /// vouches for it changes this.
    /// </summary>
    public static byte[] Binding(MacProfile p)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BindingDomain);
        Field(p.Id.ToString("N"));
        Field(p.Host);
        Field(p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(p.Username);
        Field(p.Transport.ToString());
        Field(p.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Field(p.AllowLegacySignIn ? "legacy" : "");
        Field(p.PinnedIdentityFingerprint);
        Field(p.PinnedSshKeyAlgorithm);
        Field(p.PinnedSshKey);
        return h.GetHashAndReset();

        void Field(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "\0none");
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            h.AppendData(length);
            h.AppendData(bytes);
        }
    }

    /// <summary>Opens the vault. With Hello required this shows the Hello prompt; false if declined.</summary>
    public async Task<bool> UnlockAsync()
    {
        if (IsUnlocked) return true;
        if (!File.Exists(_path))
        {
            lock (_sync)
            {
                _vaultKey = RandomNumberGenerator.GetBytes(32);
                _secrets = [];
            }
            try
            {
                if (RequireHello && !await EnableHelloAsync())
                {
                    Lock();
                    return false;
                }
            }
            catch
            {
                Lock();
                throw;
            }
            return true;
        }

        byte[] file = File.ReadAllBytes(_path);
        var (version, mode, protectedKey, nonce, tag, ciphertext) = Parse(file);
        if (RequireHello && mode != 1)
        {
            throw new InvalidOperationException("The password vault isn't protected by Windows Hello, though Vesper is set to require it. Vesper didn't make it that way, so it isn't used.");
        }
        byte[] unprotected = Unprotect(protectedKey);
        byte[] vaultKey;
        byte[]? helloSalt = null;
        try
        {
            if (mode == 1)
            {
                if (unprotected.Length != 32 + 12 + 16 + 32) throw new InvalidOperationException("The password vault is damaged.");
                helloSalt = unprotected[..32];
                byte[]? kek = await DeriveHelloKeyAsync(helloSalt, create: false);
                if (kek is null) return false;
                try
                {
                    vaultKey = new byte[32];
                    using var gcm = new AesGcm(kek, 16);
                    // salt(32) | nonce(12) | tag(16) | wrapped vault key(32)
                    gcm.Decrypt(unprotected.AsSpan(32, 12), unprotected.AsSpan(60, 32), unprotected.AsSpan(44, 16), vaultKey);
                }
                catch (CryptographicException)
                {
                    throw new InvalidOperationException("Windows Hello opened, but its key doesn't fit this vault. If Vesper's Hello key was reset, forget saved passwords in Settings and save them again.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }
            else
            {
                if (unprotected.Length != 32) throw new InvalidOperationException("The password vault is damaged.");
                vaultKey = unprotected[..32];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(unprotected);
        }

        byte[] plain = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(vaultKey, 16);
            gcm.Decrypt(nonce, ciphertext, tag, plain, file.AsSpan(0, 6));
            var secrets = Deserialize(plain, version);
            lock (_sync)
            {
                _vaultKey = vaultKey;
                _helloSalt = helloSalt;
                _secrets = secrets;
                UsesHello = mode == 1;
            }
            return true;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            throw new InvalidOperationException("The password vault couldn't be opened. It may have been created by another Windows account or damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The saved password for <paramref name="profile"/>, if the vault is unlocked and its settings still match.</summary>
    public VaultLookup TryGetPassword(MacProfile profile, out string password)
    {
        password = "";
        byte[] binding = Binding(profile);
        lock (_sync)
        {
            if (_vaultKey is null || !_secrets.TryGetValue(profile.Id, out var entry)) return VaultLookup.Missing;
            if (!CryptographicOperations.FixedTimeEquals(entry.Binding, binding)) return VaultLookup.Mismatch;
            password = Encoding.UTF8.GetString(entry.Secret);
            return VaultLookup.Found;
        }
    }

    /// <summary>Saves a password for the Mac as <paramref name="profile"/> describes it now.</summary>
    public void SetPassword(MacProfile profile, string password)
    {
        byte[] binding = Binding(profile);
        lock (_sync)
        {
            EnsureUnlocked();
            if (_secrets.TryGetValue(profile.Id, out var old)) CryptographicOperations.ZeroMemory(old.Secret);
            _secrets[profile.Id] = new Entry(binding, Encoding.UTF8.GetBytes(password));
            SaveLocked();
        }
    }

    /// <summary>
    /// Carries a saved password over to a Mac's changed settings (an edit, a newly pinned key), but
    /// only if it was sealed for the settings as they were before: a profile changed behind Vesper's
    /// back stays unmatched. False if the vault is locked or nothing moved.
    /// </summary>
    public bool Rebind(Guid id, byte[] before, byte[] after)
    {
        lock (_sync)
        {
            if (_vaultKey is null || !_secrets.TryGetValue(id, out var entry)) return false;
            if (!CryptographicOperations.FixedTimeEquals(entry.Binding, before)) return false;
            if (CryptographicOperations.FixedTimeEquals(entry.Binding, after)) return true;
            entry.Binding = after;
            SaveLocked();
            return true;
        }
    }

    public void Forget(Guid id)
    {
        lock (_sync)
        {
            if (_vaultKey is null) return;
            if (_secrets.Remove(id, out var old)) CryptographicOperations.ZeroMemory(old.Secret);
            SaveLocked();
        }
    }

    public void ForgetAll()
    {
        Lock();
        if (File.Exists(_path)) File.Delete(_path);
        UsesHello = false;
    }

    /// <summary>Moves a vault Vesper can't trust out of the way, keeping it for inspection.</summary>
    public void SetAside()
    {
        Lock();
        if (File.Exists(_path)) File.Move(_path, _path + $".untrusted-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        UsesHello = false;
    }

    public void Lock()
    {
        bool was;
        lock (_sync)
        {
            was = _vaultKey is not null;
            foreach (var entry in _secrets.Values)
            {
                CryptographicOperations.ZeroMemory(entry.Secret);
            }
            _secrets = [];
            if (_vaultKey is not null) CryptographicOperations.ZeroMemory(_vaultKey);
            if (_helloSalt is not null) CryptographicOperations.ZeroMemory(_helloSalt);
            _vaultKey = null;
            _helloSalt = null;
        }
        if (was) Locked?.Invoke();
    }

    /// <summary>
    /// Requires Windows Hello from now on. Creates the Hello key and checks that it signs
    /// deterministically (the vault key is derived from the signature), which takes two prompts.
    /// </summary>
    public async Task<bool> EnableHelloAsync()
    {
        if (!IsUnlocked && !await UnlockAsync()) return false;
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[]? first = await DeriveHelloKeyAsync(salt, create: true);
        if (first is null) return false;
        byte[]? second = await DeriveHelloKeyAsync(salt, create: false);
        if (second is null || !CryptographicOperations.FixedTimeEquals(first, second))
        {
            CryptographicOperations.ZeroMemory(first);
            if (second is not null) CryptographicOperations.ZeroMemory(second);
            throw new InvalidOperationException("Windows Hello on this PC can't be used to protect the vault (its signatures aren't repeatable). Your passwords stay protected by your Windows account.");
        }
        CryptographicOperations.ZeroMemory(second);
        lock (_sync)
        {
            EnsureUnlocked();
            _helloSalt = salt;
            UsesHello = true;
            SaveLocked(first);
        }
        CryptographicOperations.ZeroMemory(first);
        return true;
    }

    public void DisableHello()
    {
        lock (_sync)
        {
            EnsureUnlocked();
            UsesHello = false;
            _helloSalt = null;
            SaveLocked();
        }
    }

    private void EnsureUnlocked()
    {
        if (_vaultKey is null) throw new InvalidOperationException("The vault is locked.");
    }

    /// <summary>Re-seals and writes the vault. In Hello mode the key-encryption key must be supplied or re-derived.</summary>
    private void SaveLocked(byte[]? helloKek = null)
    {
        byte[] header = [.. Magic, Version, (byte)(UsesHello ? 1 : 0)];
        byte[] plain = Serialize(_secrets);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[plain.Length];
        using (var gcm = new AesGcm(_vaultKey!, 16))
        {
            gcm.Encrypt(nonce, plain, ciphertext, tag, header);
        }
        CryptographicOperations.ZeroMemory(plain);

        byte[] keyMaterial;
        if (UsesHello)
        {
            if (helloKek is null)
            {
                // Hello mode without a fresh KEK: keep the previously wrapped key from disk.
                var existing = Parse(File.ReadAllBytes(_path));
                keyMaterial = Unprotect(existing.ProtectedKey);
            }
            else
            {
                byte[] keyNonce = RandomNumberGenerator.GetBytes(12);
                byte[] keyTag = new byte[16];
                byte[] wrapped = new byte[32];
                using (var gcm = new AesGcm(helloKek, 16))
                {
                    gcm.Encrypt(keyNonce, _vaultKey!, wrapped, keyTag);
                }
                keyMaterial = [.. _helloSalt!, .. keyNonce, .. keyTag, .. wrapped];
            }
        }
        else
        {
            keyMaterial = (byte[])_vaultKey!.Clone();
        }
        byte[] protectedKey = Protect(keyMaterial);
        CryptographicOperations.ZeroMemory(keyMaterial);

        var file = new byte[header.Length + 4 + protectedKey.Length + 12 + 16 + 4 + ciphertext.Length];
        var s = file.AsSpan();
        header.CopyTo(s);
        int at = header.Length;
        BinaryPrimitives.WriteUInt32BigEndian(s[at..], (uint)protectedKey.Length); at += 4;
        protectedKey.CopyTo(s[at..]); at += protectedKey.Length;
        nonce.CopyTo(s[at..]); at += 12;
        tag.CopyTo(s[at..]); at += 16;
        BinaryPrimitives.WriteUInt32BigEndian(s[at..], (uint)ciphertext.Length); at += 4;
        ciphertext.CopyTo(s[at..]);
        AppData.WriteAtomic(_path, file);
    }

    private static async Task<byte[]?> DeriveHelloKeyAsync(byte[] salt, bool create)
    {
        byte[]? signature = await WindowsHello.SignAsync(salt, create);
        if (signature is null) return null;
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, salt, HelloInfo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private byte ReadMode()
    {
        try
        {
            using var f = File.OpenRead(_path);
            Span<byte> head = stackalloc byte[6];
            return f.Read(head) == 6 && head[..4].SequenceEqual(Magic) ? head[5] : (byte)0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static (byte Version, byte Mode, byte[] ProtectedKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext) Parse(byte[] file)
    {
        var s = file.AsSpan();
        if (s.Length < 6 + 4 || !s[..4].SequenceEqual(Magic) || s[4] is not (1 or Version) || s[5] > 1)
        {
            throw new InvalidOperationException("The password vault file isn't one Vesper recognises.");
        }
        int at = 6;
        long keyLength = BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
        if (keyLength > 4096 || at + keyLength + 12 + 16 + 4 > s.Length) throw Damaged();
        byte[] protectedKey = s.Slice(at, (int)keyLength).ToArray(); at += (int)keyLength;
        byte[] nonce = s.Slice(at, 12).ToArray(); at += 12;
        byte[] tag = s.Slice(at, 16).ToArray(); at += 16;
        long length = BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
        if (at + length != s.Length) throw Damaged();
        return (s[4], s[5], protectedKey, nonce, tag, s.Slice(at, (int)length).ToArray());

        static InvalidOperationException Damaged() => new("The password vault file is damaged.");
    }

    private static byte[] Serialize(Dictionary<Guid, Entry> secrets)
    {
        using var m = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)secrets.Count);
        m.Write(u32);
        foreach (var (id, entry) in secrets)
        {
            m.Write(id.ToByteArray());
            m.Write(entry.Binding);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)entry.Secret.Length);
            m.Write(u32);
            m.Write(entry.Secret);
        }
        byte[] result = m.ToArray();
        CryptographicOperations.ZeroMemory(m.GetBuffer());
        return result;
    }

    private static Dictionary<Guid, Entry> Deserialize(byte[] plain, byte version)
    {
        var s = plain.AsSpan();
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(s);
        int at = 4;
        var result = new Dictionary<Guid, Entry>();
        for (int i = 0; i < count; i++)
        {
            var id = new Guid(s.Slice(at, 16)); at += 16;
            // Version 1 sealed no fingerprint: such a password matches nothing until it's saved again.
            byte[] binding = version >= 2 ? s.Slice(at, BindingLength).ToArray() : new byte[BindingLength];
            if (version >= 2) at += BindingLength;
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
            result[id] = new Entry(binding, s.Slice(at, length).ToArray()); at += length;
        }
        return result;
    }

    private static unsafe byte[] Protect(byte[] data)
    {
        fixed (byte* p = data)
        fixed (byte* e = DpapiEntropy)
        {
            var input = new Native.DataBlob { Size = data.Length, Data = (nint)p };
            var entropy = new Native.DataBlob { Size = DpapiEntropy.Length, Data = (nint)e };
            Native.DataBlob output;
            if (!Native.CryptProtectData(&input, 0, &entropy, 0, 0, Native.CRYPTPROTECT_UI_FORBIDDEN, &output))
            {
                throw new CryptographicException("Windows couldn't protect the vault key (DPAPI).");
            }
            try
            {
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                Native.LocalFree(output.Data);
            }
        }
    }

    private static unsafe byte[] Unprotect(byte[] data)
    {
        fixed (byte* p = data)
        fixed (byte* e = DpapiEntropy)
        {
            var input = new Native.DataBlob { Size = data.Length, Data = (nint)p };
            var entropy = new Native.DataBlob { Size = DpapiEntropy.Length, Data = (nint)e };
            Native.DataBlob output;
            if (!Native.CryptUnprotectData(&input, 0, &entropy, 0, 0, Native.CRYPTPROTECT_UI_FORBIDDEN, &output))
            {
                throw new InvalidOperationException("The password vault belongs to a different Windows account, or was damaged.");
            }
            try
            {
                var span = new Span<byte>((void*)output.Data, output.Size);
                byte[] result = span.ToArray();
                span.Clear();
                return result;
            }
            finally
            {
                Native.LocalFree(output.Data);
            }
        }
    }
}
