namespace Vesper.Core.Rfb;

public enum ColorQuality
{
    /// <summary>Lossless 24-bit colour.</summary>
    Full,

    /// <summary>Apple's RGB 5-5-5 ("thousands of colours"): about half the bytes of Full.</summary>
    Thousands,

    /// <summary>Apple's 4-bit greyscale, for very slow links.</summary>
    Grayscale,
}

public enum Codec
{
    /// <summary>zlib over raw pixels: least work for the Mac, best on a fast local network. Apple's Full quality uses it.</summary>
    Zlib,

    /// <summary>ZRLE: palettes and runs before deflate. Fewer bytes on the wire, more CPU on the Mac.</summary>
    Zrle,
}

/// <summary>Which sign-in methods the session may use. The app decides from the transport's security.</summary>
public sealed record AuthPolicy(bool AllowRsa, bool AllowDiffieHellman)
{
    public static AuthPolicy RsaOnly { get; } = new(true, false);
    public static AuthPolicy Any { get; } = new(true, true);
}

/// <summary>The Mac's RSA identity key, as presented during type-33 sign-in.</summary>
public sealed record MacIdentity(string Fingerprint, byte[] PublicKeyDer, int KeySizeBits);

public interface IMacIdentityVerifier
{
    /// <summary>
    /// Decide whether to trust the key. Pinned keys should match silently; a first sighting or a
    /// change should involve the user. Returning false aborts the connection before any credential
    /// is sent.
    /// </summary>
    ValueTask<bool> VerifyAsync(MacIdentity identity, CancellationToken ct);
}

public sealed class RfbSessionOptions
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    public AuthPolicy Auth { get; init; } = AuthPolicy.RsaOnly;
    public IMacIdentityVerifier? IdentityVerifier { get; init; }
    public bool ViewOnly { get; init; }
    public bool ClipboardFromMac { get; init; }
    public ColorQuality Color { get; init; } = ColorQuality.Full;
    public Codec Codec { get; init; } = Codec.Zlib;

    /// <summary>Server-side scale in (0, 1], or null for the Mac's native pixels.</summary>
    public double? ServerScale { get; init; }

    /// <summary>Shortest gap between screen updates the Mac pushes unasked. 16,667 µs is 60 per second.</summary>
    public uint PushIntervalMicroseconds { get; init; } = 16_667;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public enum SignInMethod { AppleRsa, AppleDiffieHellman }

public sealed record SessionInfo(
    string DesktopName,
    bool AppleExtensions,
    SignInMethod SignIn,
    bool Encrypted,
    MacIdentity? Identity,
    bool ObserveOnly);

[Flags]
public enum PointerButtons : byte
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
}

public sealed class SessionStats
{
    public double UpdatesPerSecond { get; internal set; }
    public double MegabitsPerSecond { get; internal set; }
    public double DecodeMilliseconds { get; internal set; }
    public long BytesReceived { get; internal set; }
    public long BytesSent { get; internal set; }
}
