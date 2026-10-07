using System.Text.Json.Serialization;
using Vesper.Core.Rfb;

namespace Vesper.Models;

public enum TransportMode
{
    /// <summary>Straight to Screen Sharing; Apple's own encryption, with the Mac's identity key pinned.</summary>
    Direct,

    /// <summary>Inside an SSH tunnel to the Mac's Remote Login, with its host key pinned.</summary>
    Ssh,
}

public enum CodecPreference { Auto, Zlib, Zrle }

/// <summary>How the Mac sizes the screen it sends.</summary>
public enum ResolutionMode
{
    /// <summary>The Mac scales its Retina screen to this PC's pixel density before sending.</summary>
    MatchThisDisplay,

    /// <summary>Every native pixel (e.g. full 5K). Sharpest, heaviest.</summary>
    Native,
}

public enum ViewScaling { Fit, ActualSize }

/// <summary>
/// Which Windows key plays which Mac modifier. Mac-style puts Command on Ctrl, so Ctrl+C, Ctrl+V,
/// Ctrl+Tab and friends do on the Mac what they do on Windows.
/// </summary>
public enum ModifierLayout { MacStyle, Literal }

/// <summary>A saved Mac. Holds no secrets: passwords live in <see cref="Services.SecretVault"/>.</summary>
public sealed class MacProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5900;
    public string Username { get; set; } = "";
    public TransportMode Transport { get; set; } = TransportMode.Direct;
    public int SshPort { get; set; } = 22;

    /// <summary>
    /// Allow Apple's older Diffie-Hellman sign-in on a network Vesper can't vouch for. It's still
    /// encrypted, but a man in the middle could impersonate the Mac. Off unless the user opts in.
    /// </summary>
    public bool AllowLegacySignIn { get; set; }

    /// <summary>
    /// Set when this Mac refused Apple's RSA sign-in but accepted the same password through the
    /// older method (only ever tried where that method is safe). Skips straight to it next time.
    /// </summary>
    public bool RsaSignInUnsupported { get; set; }

    public ColorQuality Color { get; set; } = ColorQuality.Full;
    public CodecPreference Codec { get; set; } = CodecPreference.Auto;
    public ResolutionMode Resolution { get; set; } = ResolutionMode.MatchThisDisplay;
    public ViewScaling Scaling { get; set; } = ViewScaling.Fit;
    public bool ShareClipboard { get; set; }
    public ModifierLayout Modifiers { get; set; } = ModifierLayout.MacStyle;
    public bool ViewOnly { get; set; }

    /// <summary>The Mac's Screen Sharing identity key (SHA-256 fingerprint and DER), pinned on first use.</summary>
    public string? PinnedIdentityFingerprint { get; set; }

    /// <summary>The Mac's SSH host key, pinned on first use.</summary>
    public string? PinnedSshKeyAlgorithm { get; set; }
    public string? PinnedSshKey { get; set; }

    /// <summary>Whether the vault holds a password for this Mac. Not a secret, and lets the UI know without unlocking.</summary>
    public bool HasSavedPassword { get; set; }

    public string? MacModel { get; set; }
    public DateTimeOffset? LastConnected { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public string Address => Port == 5900 ? Host : $"{Host}:{Port}";

    public MacProfile Clone() => (MacProfile)MemberwiseClone();
}

public enum ShortcutCapture
{
    /// <summary>Alt+Tab, the Windows key and friends go to the Mac only in full screen (like Remote Desktop).</summary>
    FullScreenOnly,
    Always,
    Never,
}

public enum ThemeChoice { System, Dark, Light }

public sealed class AppSettings
{
    /// <summary>Exclude session windows from screenshots, screen recording, screen sharing and Recall.</summary>
    public bool HideSessionsFromCapture { get; set; } = true;

    /// <summary>Saved passwords open only after Windows Hello (face, fingerprint or PIN).</summary>
    public bool RequireWindowsHello { get; set; }

    /// <summary>Lock the vault again after this many minutes without a connected session.</summary>
    public int LockAfterMinutes { get; set; } = 15;

    public ShortcutCapture WindowsShortcuts { get; set; } = ShortcutCapture.FullScreenOnly;
    public bool FindMacsNearby { get; set; } = true;
    public double ScrollSpeed { get; set; } = 1.0;
    public bool ShowConnectionStats { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<MacProfile>))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class VesperJson : JsonSerializerContext;
