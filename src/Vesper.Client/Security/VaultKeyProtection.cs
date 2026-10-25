using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vesper.Security;

/// <summary>
/// Seals the password vault's key to the signed-in user with the operating system's own secret
/// store, so the vault file alone, copied off this computer, opens for nobody.
/// </summary>
public interface IVaultKeyProtector
{
    /// <summary>Whether this computer can keep passwords at all (Linux needs a running keyring).</summary>
    bool IsAvailable { get; }

    /// <summary>Why passwords can't be saved, when <see cref="IsAvailable"/> is false.</summary>
    string UnavailableReason { get; }

    byte[] Protect(byte[] data);

    /// <summary>Throws <see cref="InvalidOperationException"/> with a message for the user if it can't.</summary>
    byte[] Unprotect(byte[] data);

    /// <summary>Wipes anything the protector keeps in memory (called when the vault locks).</summary>
    void Forget();

    public static IVaultKeyProtector ForThisComputer() =>
        OperatingSystem.IsWindows() ? new DpapiProtector() : new KeyringProtector();
}

/// <summary>Windows DPAPI: bound to this Windows account's logon secret.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class DpapiProtector : IVaultKeyProtector
{
    private static readonly byte[] Entropy = "Vesper vault v1"u8.ToArray();
    private const int UiForbidden = 0x1;

    public bool IsAvailable => true;
    public string UnavailableReason => "";

    public byte[] Protect(byte[] data)
    {
        fixed (byte* p = data)
        fixed (byte* e = Entropy)
        {
            var input = new DataBlob { Size = data.Length, Data = (nint)p };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (nint)e };
            DataBlob output;
            if (!CryptProtectData(&input, 0, &entropy, 0, 0, UiForbidden, &output))
            {
                throw new CryptographicException("Windows couldn't protect the vault key (DPAPI).");
            }
            try
            {
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    public byte[] Unprotect(byte[] data)
    {
        fixed (byte* p = data)
        fixed (byte* e = Entropy)
        {
            var input = new DataBlob { Size = data.Length, Data = (nint)p };
            var entropy = new DataBlob { Size = Entropy.Length, Data = (nint)e };
            DataBlob output;
            if (!CryptUnprotectData(&input, 0, &entropy, 0, 0, UiForbidden, &output))
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
                LocalFree(output.Data);
            }
        }
    }

    public void Forget()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(DataBlob* dataIn, nint description, DataBlob* entropy, nint reserved, nint prompt, int flags, DataBlob* dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}

/// <summary>
/// Linux: a random 256-bit key kept in your login keyring (GNOME Keyring, KWallet or any other
/// Secret Service), which the keyring encrypts with your login password. The vault key is sealed
/// under it with AES-256-GCM. Without a keyring Vesper doesn't save passwords at all, rather than
/// keep them somewhere weaker.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class KeyringProtector : IVaultKeyProtector
{
    private const int NonceSize = 12, TagSize = 16;
    private static readonly byte[] Context = "Vesper vault key wrap v1"u8.ToArray();
    private readonly object _sync = new();
    private byte[]? _wrapKey;

    public bool IsAvailable => SecretService.IsAvailable;

    public string UnavailableReason =>
        "Saving passwords needs a login keyring (GNOME Keyring, KWallet or another Secret Service) and libsecret. " +
        "Without one, Vesper asks for the Mac's password each time instead of storing it somewhere weaker.";

    public byte[] Protect(byte[] data)
    {
        byte[] key = WrapKey(create: true);
        byte[] output = new byte[NonceSize + TagSize + data.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceSize));
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(output.AsSpan(0, NonceSize), data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Context);
        return output;
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < NonceSize + TagSize) throw new InvalidOperationException("The password vault is damaged.");
        byte[] key = WrapKey(create: false);
        byte[] plain = new byte[data.Length - NonceSize - TagSize];
        try
        {
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, Context);
            return plain;
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The password vault doesn't match the key in your login keyring. It may belong to another account, or the keyring entry was replaced.");
        }
    }

    public void Forget()
    {
        lock (_sync)
        {
            if (_wrapKey is not null) CryptographicOperations.ZeroMemory(_wrapKey);
            _wrapKey = null;
        }
    }

    private byte[] WrapKey(bool create)
    {
        lock (_sync)
        {
            if (_wrapKey is not null) return _wrapKey;
            if (!SecretService.IsAvailable) throw new InvalidOperationException(UnavailableReason);
            string? stored = SecretService.Lookup();
            if (stored is not null)
            {
                byte[] key;
                try
                {
                    key = Convert.FromBase64String(stored);
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("Vesper's entry in your login keyring is damaged.");
                }
                if (key.Length != 32) throw new InvalidOperationException("Vesper's entry in your login keyring is damaged.");
                return _wrapKey = key;
            }
            if (!create)
            {
                throw new InvalidOperationException("The key that opens Vesper's saved passwords is missing from your login keyring, so they can't be read. Forget saved passwords in Settings and save them again.");
            }
            byte[] fresh = RandomNumberGenerator.GetBytes(32);
            SecretService.Store(Convert.ToBase64String(fresh));
            return _wrapKey = fresh;
        }
    }
}

/// <summary>
/// The few libsecret calls Vesper needs, through the non-variadic "v" functions. The secret is a
/// string in the default collection, found by Vesper's schema and one attribute.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static unsafe class SecretService
{
    private const string SchemaName = "io.github.totaie.Vesper";
    private const string Label = "Vesper (key for saved Mac passwords)";
    private static readonly Lazy<Api?> Loaded = new(Load);

    public static bool IsAvailable => !OperatingSystem.IsWindows() && Loaded.Value is not null;

    public static string? Lookup()
    {
        var api = Loaded.Value ?? throw new InvalidOperationException("libsecret isn't installed.");
        using var call = new Call(api);
        nint error = 0;
        nint result = api.Lookup(call.Schema, call.Attributes, 0, &error);
        call.ThrowIf(error);
        if (result == 0) return null;
        try
        {
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            api.Free(result); // secret_password_free also wipes it
        }
    }

    public static void Store(string secret)
    {
        var api = Loaded.Value ?? throw new InvalidOperationException("libsecret isn't installed.");
        using var call = new Call(api);
        nint label = Marshal.StringToCoTaskMemUTF8(Label);
        nint value = Marshal.StringToCoTaskMemUTF8(secret);
        try
        {
            nint error = 0;
            int ok = api.Store(call.Schema, call.Attributes, 0, label, value, 0, &error);
            call.ThrowIf(error);
            if (ok == 0) throw new InvalidOperationException("Your login keyring didn't store Vesper's key.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(label);
            new Span<byte>((void*)value, secret.Length).Clear();
            Marshal.FreeCoTaskMem(value);
        }
    }

    private static Api? Load()
    {
        if (OperatingSystem.IsWindows()) return null;
        if (!NativeLibrary.TryLoad("libsecret-1.so.0", out nint secret) || !NativeLibrary.TryLoad("libglib-2.0.so.0", out nint glib)) return null;
        try
        {
            return new Api(
                (delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint*, int>)NativeLibrary.GetExport(secret, "secret_password_storev_sync"),
                (delegate* unmanaged<nint, nint, nint, nint*, nint>)NativeLibrary.GetExport(secret, "secret_password_lookupv_sync"),
                (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(secret, "secret_password_free"),
                (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(glib, "g_hash_table_new"),
                (delegate* unmanaged<nint, nint, nint, int>)NativeLibrary.GetExport(glib, "g_hash_table_insert"),
                (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_hash_table_unref"),
                (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(glib, "g_error_free"),
                NativeLibrary.GetExport(glib, "g_str_hash"),
                NativeLibrary.GetExport(glib, "g_str_equal"));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private sealed class Api(
        delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint*, int> store,
        delegate* unmanaged<nint, nint, nint, nint*, nint> lookup,
        delegate* unmanaged<nint, void> free,
        delegate* unmanaged<nint, nint, nint> hashTableNew,
        delegate* unmanaged<nint, nint, nint, int> hashTableInsert,
        delegate* unmanaged<nint, void> hashTableUnref,
        delegate* unmanaged<nint, void> errorFree,
        nint strHash,
        nint strEqual)
    {
        public int Store(nint schema, nint attributes, nint collection, nint label, nint password, nint cancellable, nint* error) =>
            store(schema, attributes, collection, label, password, cancellable, error);

        public nint Lookup(nint schema, nint attributes, nint cancellable, nint* error) => lookup(schema, attributes, cancellable, error);
        public void Free(nint password) => free(password);
        public nint NewTable() => hashTableNew(strHash, strEqual);
        public void Insert(nint table, nint key, nint value) => hashTableInsert(table, key, value);
        public void Unref(nint table) => hashTableUnref(table);
        public void FreeError(nint error) => errorFree(error);
    }

    /// <summary>The schema and attribute table for one call, in unmanaged memory.</summary>
    private sealed class Call : IDisposable
    {
        // SecretSchema: name, flags, 32 × {name, type}, reserved int, 7 reserved pointers.
        private const int SchemaSize = 8 + 8 + 32 * 16 + 8 + 7 * 8;
        private readonly Api _api;
        private readonly List<nint> _strings = [];

        public Call(Api api)
        {
            _api = api;
            Schema = (nint)NativeMemory.AllocZeroed(SchemaSize);
            *(nint*)Schema = Utf8(SchemaName);
            // flags = SECRET_SCHEMA_NONE; one string attribute (type 0).
            *(nint*)(Schema + 16) = Utf8("purpose");
            Attributes = api.NewTable();
            api.Insert(Attributes, Utf8("purpose"), Utf8("vault-key"));
        }

        public nint Schema { get; }
        public nint Attributes { get; }

        private nint Utf8(string s)
        {
            nint p = Marshal.StringToCoTaskMemUTF8(s);
            _strings.Add(p);
            return p;
        }

        public void ThrowIf(nint error)
        {
            if (error == 0) return;
            // GError: GQuark domain, gint code, gchar *message.
            string message = Marshal.PtrToStringUTF8(*(nint*)(error + 8)) ?? "unknown error";
            _api.FreeError(error);
            throw new InvalidOperationException($"Your login keyring refused: {message}");
        }

        public void Dispose()
        {
            _api.Unref(Attributes);
            NativeMemory.Free((void*)Schema);
            foreach (nint p in _strings) Marshal.FreeCoTaskMem(p);
        }
    }
}
