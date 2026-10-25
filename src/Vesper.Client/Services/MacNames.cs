using Vesper.Models;

namespace Vesper.Services;

/// <summary>How a saved Mac is named and described on screen.</summary>
public static class MacNames
{
    public static string DisplayName(MacProfile p) => string.IsNullOrWhiteSpace(p.Name) ? p.Host : p.Name;

    /// <summary>"Mac Studio" from "Mac13,1": the model identifier the Mac reports when connected.</summary>
    public static string Model(string? model)
    {
        if (string.IsNullOrEmpty(model)) return "Mac";
        string m = model;
        if (m.StartsWith("MacBookPro", StringComparison.Ordinal)) return "MacBook Pro";
        if (m.StartsWith("MacBookAir", StringComparison.Ordinal)) return "MacBook Air";
        if (m.StartsWith("Macmini", StringComparison.Ordinal)) return "Mac mini";
        if (m.StartsWith("MacPro", StringComparison.Ordinal)) return "Mac Pro";
        if (m.StartsWith("iMac", StringComparison.Ordinal)) return "iMac";
        return m switch
        {
            "Mac13,1" or "Mac13,2" or "Mac14,13" or "Mac14,14" or "Mac15,14" or "Mac16,9" or "Mac17,14" => "Mac Studio",
            "Mac14,3" or "Mac14,12" or "Mac16,10" or "Mac16,11" => "Mac mini",
            "Mac14,8" => "Mac Pro",
            "Mac15,4" or "Mac15,5" or "Mac16,2" or "Mac16,3" => "iMac",
            "Mac14,2" or "Mac14,15" or "Mac15,12" or "Mac15,13" or "Mac16,12" or "Mac16,13" => "MacBook Air",
            "Mac14,5" or "Mac14,6" or "Mac14,9" or "Mac14,10" or "Mac14,7" or "Mac15,3" or "Mac15,6" or "Mac15,7" or "Mac15,8"
                or "Mac15,9" or "Mac15,10" or "Mac15,11" or "Mac16,1" or "Mac16,5" or "Mac16,6" or "Mac16,7" or "Mac16,8" => "MacBook Pro",
            _ => "Mac",
        };
    }

    public static string LastConnected(MacProfile p) => p.LastConnected is { } t ? Relative(t) : "Not connected yet";

    private static string Relative(DateTimeOffset t)
    {
        var ago = DateTimeOffset.Now - t;
        return ago.TotalMinutes < 1 ? "just now"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalHours < 24 ? $"{(int)ago.TotalHours} h ago"
            : ago.TotalDays < 2 ? "yesterday"
            : ago.TotalDays < 7 ? $"{(int)ago.TotalDays} days ago"
            : t.ToString("d MMM");
    }

    /// <summary>Accepts "vnc://user@host:port/" and "host:port" as people paste them; returns the host and any port given.</summary>
    public static (string Host, int? Port) ParseAddress(string raw)
    {
        string host = raw.Trim();
        foreach (string scheme in new[] { "vnc://", "ard://", "ssh://" })
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) host = host[scheme.Length..];
        }
        if (host.Contains('@')) host = host[(host.IndexOf('@') + 1)..];
        host = host.TrimEnd('/');
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.Count(c => c == ':') == 1 && int.TryParse(host[(colon + 1)..], out int port) && port is > 0 and <= 65535)
        {
            return (host[..colon], port);
        }
        return (host, null);
    }

    /// <summary>"Studio" from "studio.local".</summary>
    public static string FriendlyName(string host)
    {
        string name = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host[..^6] : host;
        return name.Length > 0 && char.IsLetter(name[0]) ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }
}
