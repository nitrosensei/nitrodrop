using Nitro.Sync.Security;

namespace Nitro.Sync.Model;

/// <summary>
/// What the iPhone reported via a Shortcut: the app that was just opened ("When Instagram is opened"
/// personal automation) and/or an exact URL shared from the share sheet. The screen/chat inside an app is
/// NOT knowable (iOS sandbox; Handoff payloads are iCloud-encrypted) unless the user shares a link.
/// </summary>
public sealed record IphoneActivity
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    /// <summary>App name as sent by the Shortcut ("Instagram") or a bundle id ("com.burbn.instagram"). May be null for pure link shares.</summary>
    public string? App { get; init; }
    /// <summary>Validated http(s) URL, when the user shared one.</summary>
    public string? Url { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Resolved continuation (null when the app is unknown and no URL was sent).</summary>
    public ContinuationTarget? Target { get; init; }
}

/// <summary>Where "PC'de devam et" goes. <see cref="AppUri"/> (e.g. whatsapp://) is tried first when that protocol handler is installed.</summary>
public sealed record ContinuationTarget(string AppKey, string DisplayName, string WebUrl, string? AppUri = null);

/// <summary>Table-driven iOS app → PC continuation map.</summary>
public static class ContinuationMap
{
    private sealed record Entry(string Key, string Display, string[] Names, string[] BundleIds, string HomeUrl,
        string? AppUri = null, string[]? Hosts = null);

    // Hosts: a shared URL on one of these hosts is opened as-is (e.g. an Instagram post link).
    private static readonly Entry[] Table =
    [
        new("safari", "Safari", ["safari"], ["com.apple.mobilesafari"], ""),
        new("chrome", "Chrome", ["chrome", "google chrome"], ["com.google.chrome.ios"], ""),
        new("instagram", "Instagram", ["instagram"], ["com.burbn.instagram"], "https://www.instagram.com/direct/inbox/",
            Hosts: ["instagram.com", "instagr.am"]),
        new("whatsapp", "WhatsApp", ["whatsapp", "whatsapp messenger", "whatsapp business"], ["net.whatsapp.WhatsApp", "net.whatsapp.WhatsAppSMB"],
            "https://web.whatsapp.com/", AppUri: "whatsapp://", Hosts: ["whatsapp.com", "wa.me"]),
        new("youtube", "YouTube", ["youtube"], ["com.google.ios.youtube"], "https://www.youtube.com/", Hosts: ["youtube.com", "youtu.be"]),
        new("x", "X", ["x", "twitter"], ["com.atebits.Tweetie2"], "https://x.com/home", Hosts: ["x.com", "twitter.com"]),
        new("telegram", "Telegram", ["telegram"], ["ph.telegra.Telegraph"], "https://web.telegram.org/", AppUri: "tg://", Hosts: ["t.me", "telegram.org"]),
        new("spotify", "Spotify", ["spotify"], ["com.spotify.client"], "https://open.spotify.com/", AppUri: "spotify:", Hosts: ["open.spotify.com", "spotify.link"]),
        new("maps", "Haritalar", ["maps", "haritalar", "apple maps"], ["com.apple.Maps"], "https://www.google.com/maps",
            Hosts: ["maps.apple.com"]),
        new("googlemaps", "Google Haritalar", ["google maps", "google haritalar"], ["com.google.Maps"], "https://www.google.com/maps",
            Hosts: ["maps.google.com", "maps.app.goo.gl", "goo.gl"]),
        new("mail", "Mail", ["mail", "posta"], ["com.apple.mobilemail"], "https://mail.google.com/", AppUri: "mailto:"),
        new("gmail", "Gmail", ["gmail"], ["com.google.Gmail"], "https://mail.google.com/", Hosts: ["mail.google.com"]),
        new("outlook", "Outlook", ["outlook"], ["com.microsoft.Office.Outlook"], "https://outlook.live.com/mail/", Hosts: ["outlook.live.com", "outlook.office.com"]),
        new("tiktok", "TikTok", ["tiktok"], ["com.zhiliaoapp.musically"], "https://www.tiktok.com/", Hosts: ["tiktok.com"]),
        new("facebook", "Facebook", ["facebook"], ["com.facebook.Facebook"], "https://www.facebook.com/", Hosts: ["facebook.com", "fb.com", "fb.watch"]),
        new("messenger", "Messenger", ["messenger"], ["com.facebook.Messenger"], "https://www.messenger.com/", Hosts: ["messenger.com", "m.me"]),
        new("linkedin", "LinkedIn", ["linkedin"], ["com.linkedin.LinkedIn"], "https://www.linkedin.com/feed/", Hosts: ["linkedin.com", "lnkd.in"]),
        new("reddit", "Reddit", ["reddit"], ["com.reddit.Reddit"], "https://www.reddit.com/", Hosts: ["reddit.com", "redd.it"]),
        new("netflix", "Netflix", ["netflix"], ["com.netflix.Netflix"], "https://www.netflix.com/browse", Hosts: ["netflix.com"]),
        new("discord", "Discord", ["discord"], ["com.hammerandchisel.discord"], "https://discord.com/app", AppUri: "discord://", Hosts: ["discord.com", "discord.gg"]),
        new("notion", "Notion", ["notion"], ["notion.id"], "https://www.notion.so/", AppUri: "notion://", Hosts: ["notion.so", "notion.site"]),
        new("github", "GitHub", ["github"], ["com.github.stormbreaker.prod"], "https://github.com/", Hosts: ["github.com"]),
        new("photos", "Fotoğraflar", ["photos", "fotoğraflar", "fotograflar"], ["com.apple.mobileslideshow"], "https://www.icloud.com/photos/"),
        new("notes", "Notlar", ["notes", "notlar"], ["com.apple.mobilenotes"], "https://www.icloud.com/notes/"),
    ];

    /// <summary>App URI schemes we may launch (fixed list; never taken from the network).</summary>
    public static IReadOnlyList<string> AllowedAppSchemes { get; } =
        Table.Where(e => e.AppUri != null).Select(e => e.AppUri!.TrimEnd('/').TrimEnd(':')).Distinct().ToList();

    public static ContinuationTarget? Resolve(string? app, string? url)
    {
        string? safeUrl = url != null && UrlPolicy.TryNormalize(url, out var u) ? u : null;
        var entry = Find(app);
        if (safeUrl != null)
        {
            // The user shared an exact link (post, page, video, chat invite): it always wins.
            var byHost = FindByHost(safeUrl) ?? entry;
            return new ContinuationTarget(byHost?.Key ?? "web", byHost?.Display ?? new Uri(safeUrl).Host, safeUrl);
        }
        if (entry == null || entry.HomeUrl.Length == 0) return null; // unknown app, or Safari without a URL
        return new ContinuationTarget(entry.Key, entry.Display, entry.HomeUrl, entry.AppUri);
    }

    private static Entry? Find(string? app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        var a = app.Trim();
        return Table.FirstOrDefault(e => e.BundleIds.Any(b => b.Equals(a, StringComparison.OrdinalIgnoreCase)))
               ?? Table.FirstOrDefault(e => e.Names.Any(n => n.Equals(a, StringComparison.InvariantCultureIgnoreCase)));
    }

    private static Entry? FindByHost(string url) =>
        Table.FirstOrDefault(e => e.Hosts != null && MatchesHost(url, e.Hosts));

    private static bool MatchesHost(string url, string[] hosts)
    {
        var h = new Uri(url).Host.ToLowerInvariant();
        return hosts.Any(x => h == x || h.EndsWith("." + x, StringComparison.Ordinal));
    }
}
