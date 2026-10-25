using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vesper.Updates;

/// <summary>
/// What a release offers, published beside it as vesper-update.json and signed with Vesper's
/// release key (<see cref="UpdateSigning"/>). Every download it names is pinned by size and SHA-256,
/// so the signature covers the installers too.
/// </summary>
public sealed partial class UpdateManifest
{
    public const string ProductName = "Vesper";

    /// <summary>Always "Vesper": a manifest signed for anything else is refused.</summary>
    public string Product { get; set; } = "";

    /// <summary>Major.minor.patch, e.g. "0.2.0".</summary>
    public string Version { get; set; } = "";

    public DateTimeOffset Published { get; set; }

    /// <summary>A line or two about what changed, shown with the update.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Downloads by kind of install: see <see cref="InstallInfo.AssetKey"/>.</summary>
    public Dictionary<string, UpdateAsset> Assets { get; set; } = [];

    [JsonIgnore]
    public Version ParsedVersion => System.Version.Parse(Version);

    /// <summary>Parses and checks a manifest whose signature has already been verified.</summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize(json, UpdateJson.Default.UpdateManifest)
            ?? throw new InvalidDataException("The update description is empty.");
        manifest.Validate();
        return manifest;
    }

    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, UpdateJson.Default.UpdateManifest);

    private void Validate()
    {
        if (Product != ProductName) throw new InvalidDataException("The update isn't for Vesper.");
        if (!System.Version.TryParse(Version, out var v) || v.Build < 0 || v.Revision >= 0)
        {
            throw new InvalidDataException("The update's version number isn't valid.");
        }
        if (Summary.Length > 2000) throw new InvalidDataException("The update's description is too long.");
        foreach (var (key, asset) in Assets)
        {
            if (!SafeName().IsMatch(key)) throw new InvalidDataException("The update names an unknown kind of download.");
            asset.Validate();
        }
    }

    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex SafeName();
}

public sealed partial class UpdateAsset
{
    /// <summary>Installers are tens of megabytes; anything near this is refused outright.</summary>
    public const long MaxSize = 512L << 20;

    /// <summary>A plain file name in the release, never a path or URL.</summary>
    public string File { get; set; } = "";

    public long Size { get; set; }

    /// <summary>Lower-case hex SHA-256 of the file.</summary>
    public string Sha256 { get; set; } = "";

    internal void Validate()
    {
        if (!FileName().IsMatch(File) || File.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The update names a download Vesper won't fetch.");
        }
        if (Size <= 0 || Size > MaxSize) throw new InvalidDataException("The update's download size isn't valid.");
        if (!Hash().IsMatch(Sha256)) throw new InvalidDataException("The update's checksum isn't valid.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$")]
    private static partial Regex FileName();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Hash();
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class UpdateJson : JsonSerializerContext;
