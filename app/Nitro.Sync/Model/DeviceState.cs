using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nitro.Sync.Model;

/// <summary>Kind of browser a tab came from (as reported by the extension).</summary>
public static class BrowserKind
{
    public const string Chrome = "chrome";
    public const string Edge = "edge";
    public const string Other = "other";

    public static string Normalize(string? b) => b?.Trim().ToLowerInvariant() switch
    {
        "chrome" => Chrome,
        "edge" => Edge,
        _ => Other,
    };
}

/// <summary>One open browser tab. Private/incognito tabs are never collected (extension + sanitizer).</summary>
public sealed record BrowserTab
{
    public string Browser { get; init; } = BrowserKind.Other;
    public int WindowId { get; init; }
    public int TabId { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string? FaviconUrl { get; init; }
    public bool Active { get; init; }
    /// <summary>Unix milliseconds (chrome.tabs.Tab.lastAccessed).</summary>
    public long LastAccessed { get; init; }
    /// <summary>Set only by the extension; sanitizer drops tabs where this is true.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Incognito { get; init; }

    /// <summary>Identity used for diffing (a tab keeps its id while navigating).</summary>
    [JsonIgnore] public string Key => $"{Browser}:{WindowId}:{TabId}";
}

/// <summary>A user-visible top-level window / running app.</summary>
public sealed record AppInfo
{
    /// <summary>Stable id derived from the executable path (see <see cref="ForExecutable"/>).</summary>
    public string AppId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>File name only ("Code.exe"); the full path never leaves the PC.</summary>
    public string Exe { get; init; } = "";
    public string? WindowTitle { get; init; }
    public bool Foreground { get; init; }
    /// <summary>16×16 PNG, base64. Optional.</summary>
    public string? IconPng { get; init; }

    public static string ForExecutable(string fullPath)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
        return Convert.ToHexStringLower(h, 0, 8);
    }
}

/// <summary>Everything one device shares with its paired peers.</summary>
public sealed record DeviceState
{
    public int Version { get; init; } = 1;
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    /// <summary>Unix milliseconds of the last local change.</summary>
    public long UpdatedAt { get; init; }
    public IReadOnlyList<BrowserTab> Tabs { get; init; } = [];
    public IReadOnlyList<AppInfo> Apps { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static DeviceState? FromJson(ReadOnlySpan<byte> utf8)
    {
        try { return JsonSerializer.Deserialize<DeviceState>(utf8, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Strong ETag over the content (UpdatedAt excluded so a no-op refresh keeps the tag).</summary>
    public string ComputeETag()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { UpdatedAt = 0 }, Json);
        return "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes), 0, 12) + "\"";
    }
}

/// <summary>What changed between two states of the same device.</summary>
public sealed record StateDiff(
    IReadOnlyList<BrowserTab> AddedTabs,
    IReadOnlyList<BrowserTab> RemovedTabs,
    IReadOnlyList<BrowserTab> ChangedTabs,
    IReadOnlyList<AppInfo> AddedApps,
    IReadOnlyList<AppInfo> RemovedApps,
    IReadOnlyList<AppInfo> ChangedApps)
{
    public bool IsEmpty => AddedTabs.Count + RemovedTabs.Count + ChangedTabs.Count
                           + AddedApps.Count + RemovedApps.Count + ChangedApps.Count == 0;

    public static StateDiff Compute(DeviceState? before, DeviceState? after)
    {
        var bTabs = (before?.Tabs ?? []).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
        var aTabs = (after?.Tabs ?? []).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
        var bApps = (before?.Apps ?? []).GroupBy(a => a.AppId).ToDictionary(g => g.Key, g => g.First());
        var aApps = (after?.Apps ?? []).GroupBy(a => a.AppId).ToDictionary(g => g.Key, g => g.First());
        return new StateDiff(
            aTabs.Where(kv => !bTabs.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList(),
            bTabs.Where(kv => !aTabs.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList(),
            aTabs.Where(kv => bTabs.TryGetValue(kv.Key, out var o) && o != kv.Value).Select(kv => kv.Value).ToList(),
            aApps.Where(kv => !bApps.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList(),
            bApps.Where(kv => !aApps.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList(),
            aApps.Where(kv => bApps.TryGetValue(kv.Key, out var o) && o != kv.Value).Select(kv => kv.Value).ToList());
    }
}
