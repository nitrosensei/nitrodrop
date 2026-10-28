using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;

namespace AirDrop.App.Localization;

/// <summary>A UI language: two-letter code, its name in itself (for the selector) and the culture used for formatting.</summary>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, string DefaultCulture);

/// <summary>Objects that cache translated text and must refresh it when the UI language changes.</summary>
public interface ILanguageAware
{
    void OnLanguageChanged();
}

/// <summary>
/// UI language and string lookup. Strings live in <c>Localization/&lt;Module&gt;.resx</c> (Turkish, the source) and
/// <c>&lt;Module&gt;.&lt;lang&gt;.resx</c>; <c>tools/l10n.py gen</c> writes a strongly-typed <c>&lt;Module&gt;Strings</c>
/// class for each (Localization/Generated). Keys are unique across modules, so XAML can look them up by key alone
/// (<c>{l:Loc Key}</c>, Localization/Wpf).
/// <para>
/// Resource lookup always goes through <see cref="Culture"/> (the generated classes pass it explicitly), never the thread
/// culture, so async continuations started before a language switch still read the new language.
/// </para>
/// WPF-free (linked into AirDrop.Tests).
/// </summary>
public static class Loc
{
    public const string SourceLanguage = "tr";

    /// <summary>Same set as nitrobyte.com.tr. Order = order in Ayarlar → Dil.</summary>
    public static IReadOnlyList<LanguageInfo> Languages { get; } = new[]
    {
        new LanguageInfo("tr", "Türkçe", "Turkish", "tr-TR"),       // l10n-allow: native name
        new LanguageInfo("en", "English", "English", "en-US"),
        new LanguageInfo("de", "Deutsch", "German", "de-DE"),
        new LanguageInfo("fr", "Français", "French", "fr-FR"),      // l10n-allow: native name
        new LanguageInfo("ru", "Русский", "Russian", "ru-RU"),
        new LanguageInfo("pl", "Polski", "Polish", "pl-PL"),
    };

    private static readonly object Gate = new();
    private static readonly List<WeakReference<ILanguageAware>> Aware = new();
    private static (Type Type, ResourceManager Rm)[]? _modules;
    private static Dictionary<string, ResourceManager>? _index;

    /// <summary>Culture for lookups and formatting (dates, numbers). Starts as Turkish until <see cref="SetLanguage"/>.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Two-letter code of the current UI language.</summary>
    public static string Language => Culture.TwoLetterISOLanguageName;

    /// <summary>Raised after the language changed (on the caller's thread — the UI thread in the app).</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// The Windows display language as it was when the app first asked — captured before UiLanguage overwrites the
    /// thread cultures, so "Windows diliyle aynı" still means Windows after a manual switch.
    /// </summary>
    public static CultureInfo SystemUiCulture => _systemUi ??= CultureInfo.CurrentUICulture;
    private static CultureInfo? _systemUi;

    public static bool IsSupported(string? code) => code is not null && Languages.Any(l => l.Code == code);

    /// <summary>The Windows display language if we have it, otherwise English.</summary>
    public static string ResolveDefault(CultureInfo? uiCulture = null)
    {
        var ui = uiCulture ?? SystemUiCulture;
        for (var c = ui; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
            if (IsSupported(c.TwoLetterISOLanguageName)) return c.TwoLetterISOLanguageName;
        return "en";
    }

    /// <summary>
    /// Formatting culture for a UI language: the user's own regional variant when it is the same language
    /// (en-GB stays en-GB, de-AT stays de-AT), otherwise the language's default culture.
    /// </summary>
    public static CultureInfo CultureFor(string code, CultureInfo? userCulture = null)
    {
        var user = userCulture ?? SystemUiCulture;
        if (user.TwoLetterISOLanguageName == code && !user.IsNeutralCulture) return CultureInfo.GetCultureInfo(user.Name);
        var info = Languages.FirstOrDefault(l => l.Code == code) ?? Languages[1];
        return CultureInfo.GetCultureInfo(info.DefaultCulture);
    }

    /// <summary>
    /// Switches the UI language (<c>null</c>/empty = follow Windows). Raises
    /// <see cref="LanguageChanged"/> and calls every live <see cref="ILanguageAware"/>.
    /// </summary>
    public static void SetLanguage(string? code, CultureInfo? userCulture = null)
    {
        var lang = IsSupported(code) ? code! : ResolveDefault(userCulture);
        var culture = CultureFor(lang, userCulture);
        bool changed = !Equals(culture, Culture);
        Culture = culture;
        if (!changed) return;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
        ILanguageAware[] live;
        lock (Gate)
        {
            Aware.RemoveAll(w => !w.TryGetTarget(out _));
            live = Aware.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<ILanguageAware>().ToArray();
        }
        foreach (var a in live) a.OnLanguageChanged();
    }

    /// <summary>Weakly registers an object to be told about language changes (view models, item VMs).</summary>
    public static void Subscribe(ILanguageAware target)
    {
        lock (Gate)
        {
            if (Aware.Count > 256) Aware.RemoveAll(w => !w.TryGetTarget(out _));
            Aware.Add(new WeakReference<ILanguageAware>(target));
        }
    }

    /// <summary>String by resource key from any module; the key itself when missing (tests guarantee none are).</summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        return Index.TryGetValue(key, out var rm) ? rm.GetString(key, Culture) ?? key : key;
    }

    /// <summary>True if <paramref name="key"/> exists in some module.</summary>
    public static bool Has(string key) => Index.ContainsKey(key);

    /// <summary><see cref="string.Format(IFormatProvider, string, object[])"/> in the UI culture.</summary>
    public static string Format(string pattern, params object?[] args) => string.Format(Culture, pattern, args);

    /// <summary>Plural pattern (forms separated by <c>|</c>, see <see cref="Plural"/>): {0} = n, {1}… = <paramref name="extra"/>.</summary>
    public static string Plural(string pattern, long n, params object?[] extra)
    {
        var form = Localization.Plural.Select(Language, pattern, n);
        var args = new object?[extra.Length + 1];
        args[0] = n;
        Array.Copy(extra, 0, args, 1, extra.Length);
        return string.Format(Culture, form, args);
    }

    /// <summary>"2,4 MB" / "2.4 MB" / "2,4 Mo" / "2,4 МБ": one decimal below 100, units from Common.resx.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = { CommonStrings.Unit_Byte, CommonStrings.Unit_KB, CommonStrings.Unit_MB, CommonStrings.Unit_GB, CommonStrings.Unit_TB };
        if (bytes < 0) bytes = 0;
        double v = Math.Max(0, bytes);
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1024; u++; }
        var num = u == 0 ? v.ToString("0", Culture) : v < 100 ? v.ToString("0.#", Culture) : v.ToString("0", Culture);
        return CommonStrings.Unit_Format(num, units[u]);
    }

    /// <summary>Short clock time in the UI culture ("14:05", "2:05 PM").</summary>
    public static string Time(DateTime t) => t.ToString("t", Culture);

    /// <summary>
    /// Compact timestamp for lists: time today, "Dün"/"Yesterday" + time, weekday within a week, else a short date.
    /// </summary>
    public static string When(DateTime t, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        var day = t.Date;
        if (day == n.Date) return Time(t);
        if (day == n.Date.AddDays(-1)) return CommonStrings.Time_YesterdayAt(Time(t));
        if (day > n.Date.AddDays(-7) && day < n.Date) return Culture.DateTimeFormat.GetDayName(t.DayOfWeek);
        return day.Year == n.Year
            ? t.ToString(MonthDayPattern(), Culture)
            : t.ToString("d", Culture);
    }

    /// <summary>Day + abbreviated month without the year ("28 Eki", "Oct 28", "28. Okt.", "28 окт.").</summary>
    private static string MonthDayPattern()
    {
        var md = Culture.DateTimeFormat.MonthDayPattern;   // "d MMMM", "MMMM d", "d. MMMM", "d MMMM"
        return md.Replace("MMMM", "MMM");
    }

    // ---- module registry ----

    /// <summary>
    /// ResourceManager for a module, by manifest name suffix (<c>….Localization.&lt;module&gt;.resources</c>): the app
    /// embeds them as AirDrop.App.Localization.*, the tests as AirDrop.Tests.App.Localization.*.
    /// </summary>
    public static ResourceManager CreateManager(string module, Assembly assembly)
    {
        var suffix = ".Localization." + module + ".resources";
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return new ResourceManager(name is null ? "AirDrop.App.Localization." + module : name[..^".resources".Length], assembly);
    }

    private static (Type Type, ResourceManager Rm)[] Modules
    {
        get
        {
            if (_modules is not null) return _modules;
            var list = new List<(Type, ResourceManager)>();
            foreach (var t in typeof(Loc).Assembly.GetTypes())
            {
                if (t.Namespace != typeof(Loc).Namespace || !t.Name.EndsWith("Strings", StringComparison.Ordinal)) continue;
                var p = t.GetProperty("ResourceManager", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p?.GetValue(null) is ResourceManager rm) list.Add((t, rm));
            }
            return _modules = list.ToArray();
        }
    }

    /// <summary>Generated strings classes (for tests).</summary>
    public static IEnumerable<(string Module, ResourceManager Rm)> ModuleManagers =>
        Modules.Select(m => (m.Type.Name[..^"Strings".Length], m.Rm));

    private static Dictionary<string, ResourceManager> Index
    {
        get
        {
            if (_index is not null) return _index;
            var d = new Dictionary<string, ResourceManager>(StringComparer.Ordinal);
            foreach (var (_, rm) in Modules)
            {
                var set = rm.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
                if (set is null) continue;
                foreach (DictionaryEntry e in set)
                    if (e.Key is string k) d.TryAdd(k, rm);
            }
            return _index = d;
        }
    }
}
