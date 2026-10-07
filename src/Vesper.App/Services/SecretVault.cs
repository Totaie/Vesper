using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Vesper.Interop;

namespace Vesper.Services;

/// <summary>
/// Saved passwords, encrypted at rest.
///
/// The secrets are sealed with AES-256-GCM under a random vault key. That key is protected by
/// Windows DPAPI (bound to this Windows account), and, when Windows Hello is required, also
/// wrapped under a key derived from a Windows Hello signature, so another program running as you
/// can't open the vault without a visible Hello prompt. Unlocked secrets live in memory only
/// until the vault locks, and are wiped when it does.
///
/// File layout: "VSPR", version 1, mode (0 DPAPI, 1 DPAPI + Hello), u32 n, n bytes of DPAPI blob,
/// 12-byte nonce, 16-byte tag, u32 m, m bytes of ciphertext. The 6-byte header is authenticated.
/// </summary>
internal sealed class SecretVault
{
    private static readonly byte[] Magic = "VSPR"u8.ToArray();
    private static readonly byte[] DpapiEntropy = "Vesper vault v1"u8.ToArray();
    private static readonly byte[] HelloInfo = "Vesper vault key-encryption key v1"u8.ToArray();
    private const byte Version = 1;

    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<Guid, byte[]> _secrets = [];
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

    public bool UsesHello { get; private set; }

    public event Action? Locked;

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
            return true;
        }

        byte[] file = File.ReadAllBytes(_path);
        var (mode, protectedKey, nonce, tag, ciphertext) = Parse(file);
        byte[] unprotected = Unprotect(protectedKey);
        byte[] vaultKey;
        byte[]? helloSalt = null;
        try
        {
            if (mode == 1)
            {
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
                finally
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }
            else
            {
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
            var secrets = Deserialize(plain);
            lock (_sync)
            {
                _vaultKey = vaultKey;
                _helloSalt = helloSalt;
                _secrets = secrets;
                UsesHello = mode == 1;
            }
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            throw new InvalidOperationException("The password vault couldn't be opened. It may have been created by another Windows account or damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public bool TryGetPassword(Guid id, out string password)
    {
        lock (_sync)
        {
            if (_vaultKey is not null && _secrets.TryGetValue(id, out var bytes))
            {
                password = Encoding.UTF8.GetString(bytes);
                return true;
            }
        }
        password = "";
        return false;
    }

    public void SetPassword(Guid id, string password)
    {
        lock (_sync)
        {
            EnsureUnlocked();
            if (_secrets.TryGetValue(id, out var old)) CryptographicOperations.ZeroMemory(old);
            _secrets[id] = Encoding.UTF8.GetBytes(password);
            SaveLocked();
        }
    }

    public void Forget(Guid id)
    {
        lock (_sync)
        {
            if (_vaultKey is null) return;
            if (_secrets.Remove(id, out var old)) CryptographicOperations.ZeroMemory(old);
            SaveLocked();
        }
    }

    public void ForgetAll()
    {
        Lock();
        if (File.Exists(_path)) File.Delete(_path);
        UsesHello = false;
    }

    public void Lock()
    {
        bool was;
        lock (_sync)
        {
            was = _vaultKey is not null;
            foreach (var bytes in _secrets.Values)
            {
                CryptographicOperations.ZeroMemory(bytes);
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

    private static (byte Mode, byte[] ProtectedKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext) Parse(byte[] file)
    {
        var s = file.AsSpan();
        if (s.Length < 6 + 4 || !s[..4].SequenceEqual(Magic) || s[4] != Version)
        {
            throw new InvalidOperationException("The password vault file isn't one Vesper recognises.");
        }
        int at = 6;
        int keyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
        byte[] protectedKey = s.Slice(at, keyLength).ToArray(); at += keyLength;
        byte[] nonce = s.Slice(at, 12).ToArray(); at += 12;
        byte[] tag = s.Slice(at, 16).ToArray(); at += 16;
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
        return (s[5], protectedKey, nonce, tag, s.Slice(at, length).ToArray());
    }

    private static byte[] Serialize(Dictionary<Guid, byte[]> secrets)
    {
        using var m = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)secrets.Count);
        m.Write(u32);
        foreach (var (id, bytes) in secrets)
        {
            m.Write(id.ToByteArray());
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)bytes.Length);
            m.Write(u32);
            m.Write(bytes);
        }
        byte[] result = m.ToArray();
        CryptographicOperations.ZeroMemory(m.GetBuffer());
        return result;
    }

    private static Dictionary<Guid, byte[]> Deserialize(byte[] plain)
    {
        var s = plain.AsSpan();
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(s);
        int at = 4;
        var result = new Dictionary<Guid, byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            var id = new Guid(s.Slice(at, 16)); at += 16;
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(s[at..]); at += 4;
            result[id] = s.Slice(at, length).ToArray(); at += length;
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
