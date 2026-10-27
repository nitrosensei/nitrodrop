using Nitro.Sync.Security;

namespace Nitro.Sync.Model;

/// <summary>Hard limits applied to every state we produce or accept (local extension input and remote peers).</summary>
public static class StateLimits
{
    public const int MaxTabs = 500;
    public const int MaxApps = 100;
    public const int MaxTitle = 300;
    public const int MaxName = 120;
    public const int MaxIconBase64 = 16 * 1024;
    /// <summary>Upper bound of a serialized state on the wire.</summary>
    public const int MaxStateBytes = 2 * 1024 * 1024;
}

public static class StateSanitizer
{
    /// <summary>Drops incognito/non-web tabs, clamps strings and counts. Never throws.</summary>
    public static IReadOnlyList<BrowserTab> Tabs(IEnumerable<BrowserTab>? tabs, string? forceBrowser = null)
    {
        var list = new List<BrowserTab>();
        if (tabs == null) return list;
        foreach (var t in tabs)
        {
            if (t == null || t.Incognito) continue;
            if (!UrlPolicy.TryNormalize(t.Url, out var url)) continue; // chrome://, file://, about: … stay local
            string? fav = t.FaviconUrl != null && UrlPolicy.TryNormalize(t.FaviconUrl, out var f) && f.Length <= 1024 ? f : null;
            list.Add(t with
            {
                Browser = BrowserKind.Normalize(forceBrowser ?? t.Browser),
                Title = Clamp(t.Title, StateLimits.MaxTitle),
                Url = url,
                FaviconUrl = fav,
                Incognito = false,
            });
            if (list.Count >= StateLimits.MaxTabs) break;
        }
        return list;
    }

    public static IReadOnlyList<AppInfo> Apps(IEnumerable<AppInfo>? apps)
    {
        var list = new List<AppInfo>();
        if (apps == null) return list;
        foreach (var a in apps)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.AppId)) continue;
            list.Add(a with
            {
                AppId = Clamp(a.AppId, 64),
                Name = Clamp(a.Name, StateLimits.MaxName),
                Exe = Clamp(Path.GetFileName(a.Exe ?? ""), StateLimits.MaxName),
                WindowTitle = a.WindowTitle == null ? null : Clamp(a.WindowTitle, StateLimits.MaxTitle),
                IconPng = a.IconPng != null && a.IconPng.Length <= StateLimits.MaxIconBase64 ? a.IconPng : null,
            });
            if (list.Count >= StateLimits.MaxApps) break;
        }
        return list;
    }

    public static DeviceState State(DeviceState s) => s with
    {
        DeviceId = Clamp(s.DeviceId, 64),
        DeviceName = Clamp(s.DeviceName, StateLimits.MaxName),
        Tabs = Tabs(s.Tabs),
        Apps = Apps(s.Apps),
    };

    public static string Clamp(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length <= max ? s : s[..max];
    }
}
