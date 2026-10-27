using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AirDrop.App.Setup;

/// <summary>Names and checks for the airdropwifi driver package. WPF-free.</summary>
public static class DriverPackage
{
    public const string InfName = "airdropwifi.inf";
    public const string SysName = "airdropwifi.sys";
    public const string CatName = "airdropwifi.cat";
    public const string ServiceName = "airdropwifi";
    public const string Provider = "airdrop-win";

    /// <summary>Files shipped with the app (the firmware is downloaded).</summary>
    public static IReadOnlyList<string> BundledFiles { get; } = new[] { InfName, SysName, CatName };

    /// <summary>Files the catalog covers.</summary>
    public static IReadOnlyList<string> CatalogMembers { get; } = new[] { InfName, SysName, FirmwareSpec.Firmware.Name };

    public static IReadOnlyList<string> AllFiles { get; } = new[] { InfName, SysName, CatName, FirmwareSpec.Firmware.Name };

    /// <summary>
    /// True for our INF (also once published as C:\Windows\INF\oemNN.inf, which keeps the original text):
    /// [Version] CatalogFile = airdropwifi.cat and Provider (resolved through [Strings]) = airdrop-win.
    /// </summary>
    public static bool IsOurInf(string infText)
    {
        var sections = ParseInf(infText);
        if (!sections.TryGetValue("version", out var version)) return false;
        string? Resolve(string? v)
        {
            if (v is null) return null;
            if (v.Length > 2 && v[0] == '%' && v[^1] == '%' && sections.TryGetValue("strings", out var strings)
                && strings.TryGetValue(v[1..^1].ToLowerInvariant(), out var s))
                return s;
            return v;
        }
        var cat = Resolve(version.GetValueOrDefault("catalogfile"));
        var provider = Resolve(version.GetValueOrDefault("provider"));
        return string.Equals(cat, CatName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Published copies of our INF (oemNN.inf) in %windir%\INF. Locale-independent, no admin needed.</summary>
    public static IReadOnlyList<string> FindPublishedInfs(string infDir)
    {
        var found = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(infDir, "oem*.inf"))
            {
                try
                {
                    if (IsOurInf(File.ReadAllText(f))) found.Add(Path.GetFileName(f));
                }
                catch (Exception) { /* unreadable INF: skip */ }
            }
        }
        catch (Exception ex) { SetupLog.Write($"INF klasörü okunamadı: {ex.Message}"); }
        return found.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Missing package files in <paramref name="dir"/>.</summary>
    public static IReadOnlyList<string> Missing(string dir) =>
        AllFiles.Where(f => !File.Exists(Path.Combine(dir, f))).ToList();

    /// <summary>Minimal INF reader: section → (key → value), keys lower-case, comments and quotes stripped.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseInf(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim().ToLowerInvariant();
                if (!result.TryGetValue(name, out current))
                    result[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (current is null) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = val[1..^1];
            current.TryAdd(key, val);
        }
        return result;
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ';' && !quoted) return line[..i];
        }
        return line;
    }
}
