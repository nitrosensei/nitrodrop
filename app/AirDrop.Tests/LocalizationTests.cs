using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AirDrop.App.Localization;

namespace AirDrop.Tests;

/// <summary>
/// UI localization (app/AirDrop.App/Localization): every key in every language, matching placeholders, plural forms,
/// generated accessors in sync, XAML keys resolvable, plural rules, culture defaults, the browser extension's
/// _locales and the MSIX manifest languages — and no hard-coded Turkish left in the app's sources.
/// Reads resources with explicit cultures; never switches the process-wide <see cref="Loc"/> language.
/// </summary>
public class LocalizationTests
{
    private static readonly string[] Languages = { "en", "de", "fr", "ru", "pl" };
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "app", "AirDrop.slnx"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    private static string AppDir => Path.Combine(RepoRoot, "app", "AirDrop.App");
    private static string LocDir => Path.Combine(AppDir, "Localization");

    private static Dictionary<string, string> Neutral(ResourceManager rm) =>
        Set(rm, CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Set(ResourceManager rm, CultureInfo c)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var set = rm.GetResourceSet(c, createIfNotExists: true, tryParents: false);
        if (set is null) return d;
        foreach (DictionaryEntry e in set)
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    private static IEnumerable<int> Placeholders(string s) =>
        Placeholder.Matches(s).Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i);

    [Fact]
    public void Modules_are_all_registered()
    {
        var modules = Loc.ModuleManagers.Select(m => m.Module).OrderBy(m => m).ToArray();
        var files = Directory.GetFiles(LocDir, "*.resx").Select(Path.GetFileNameWithoutExtension)
            .Where(n => !n!.Contains('.')).OrderBy(n => n).ToArray();
        Assert.Equal(files, modules);
        Assert.Contains("Common", modules);
    }

    [Fact]
    public void Every_key_exists_and_is_non_empty_in_every_language()
    {
        var problems = new List<string>();
        foreach (var (module, rm) in Loc.ModuleManagers)
        {
            var src = Neutral(rm);
            foreach (var lang in Languages)
            {
                var tr = Set(rm, CultureInfo.GetCultureInfo(lang));
                foreach (var (key, value) in src)
                {
                    if (string.IsNullOrWhiteSpace(value)) problems.Add($"{module}.tr: {key} empty");
                    if (!tr.TryGetValue(key, out var t) || string.IsNullOrWhiteSpace(t)) problems.Add($"{module}.{lang}: {key} missing");
                }
                foreach (var key in tr.Keys.Except(src.Keys)) problems.Add($"{module}.{lang}: {key} not in the neutral resx");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Format_placeholders_match_across_languages()
    {
        var problems = new List<string>();
        foreach (var (module, rm) in Loc.ModuleManagers)
        {
            var src = Neutral(rm);
            foreach (var lang in Languages)
            {
                var tr = Set(rm, CultureInfo.GetCultureInfo(lang));
                foreach (var (key, value) in src)
                {
                    if (!tr.TryGetValue(key, out var t)) continue;
                    var a = string.Join(",", Placeholders(value));
                    var b = string.Join(",", Placeholders(t));
                    if (a != b) problems.Add($"{module}.{lang}: {key} has {{{b}}}, tr has {{{a}}}");
                    // string.Format must accept it (no stray braces)
                    try { _ = string.Format(CultureInfo.InvariantCulture, t.Split(Plural.Separator)[0], "a", "b", "c", "d", "e", "f"); }
                    catch (FormatException) { problems.Add($"{module}.{lang}: {key} is not a valid format string"); }
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Plural_keys_have_the_right_number_of_forms()
    {
        var problems = new List<string>();
        foreach (var (module, rm) in Loc.ModuleManagers)
        {
            var src = Neutral(rm);
            foreach (var lang in new[] { "tr" }.Concat(Languages))
            {
                var tr = lang == "tr" ? src : Set(rm, CultureInfo.GetCultureInfo(lang));
                foreach (var (key, value) in tr)
                {
                    int forms = value.Split(Plural.Separator).Length;
                    if (key.EndsWith("_Plural", StringComparison.Ordinal))
                    {
                        if (forms != Plural.FormCount(lang)) problems.Add($"{module}.{lang}: {key} has {forms} forms, needs {Plural.FormCount(lang)}");
                    }
                    else if (forms > 1 && !key.EndsWith("Filter", StringComparison.Ordinal)) problems.Add($"{module}.{lang}: {key} contains '|' but is not a _Plural key");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Keys_are_unique_across_modules()
    {
        var seen = new Dictionary<string, string>();
        var dups = new List<string>();
        foreach (var (module, rm) in Loc.ModuleManagers)
            foreach (var key in Neutral(rm).Keys)
                if (!seen.TryAdd(key, module)) dups.Add($"{key}: {seen[key]} and {module}");
        Assert.True(dups.Count == 0, string.Join(Environment.NewLine, dups));
    }

    [Fact]
    public void Generated_accessors_match_the_resx_keys()
    {
        var asm = typeof(Loc).Assembly;
        var problems = new List<string>();
        foreach (var (module, rm) in Loc.ModuleManagers)
        {
            var type = asm.GetType($"AirDrop.App.Localization.{module}Strings")!;
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m is PropertyInfo or MethodInfo { IsSpecialName: false })
                .Select(m => m.Name).Where(n => n != "ResourceManager").ToHashSet();
            var keys = Neutral(rm).Keys.ToHashSet();
            foreach (var k in keys.Except(members)) problems.Add($"{module}: {k} has no accessor (run tools/l10n.py gen)");
            foreach (var k in members.Except(keys)) problems.Add($"{module}: accessor {k} has no resource (run tools/l10n.py gen)");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_key_used_in_xaml_and_code_exists()
    {
        var used = new Regex(@"\{l:Loc\s+(?:Key=)?(\w+)|l:Rich\.Key=""(\w+)""|Loc\.Get\(""(\w+)""\)", RegexOptions.Compiled);
        var missing = new List<string>();
        foreach (var file in SourceFiles("*.xaml").Concat(SourceFiles("*.cs")).Where(f => !Rel(f).StartsWith("Localization/", StringComparison.Ordinal)))
        {
            foreach (Match m in used.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                if (!Loc.Has(key)) missing.Add($"{Rel(file)}: {key}");
            }
        }
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    // ---- plural rules ----

    [Theory]
    [InlineData(0, 2)] [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(4, 1)] [InlineData(5, 2)] [InlineData(11, 2)]
    [InlineData(12, 2)] [InlineData(14, 2)] [InlineData(21, 0)] [InlineData(22, 1)] [InlineData(25, 2)] [InlineData(101, 0)]
    [InlineData(111, 2)] [InlineData(112, 2)] [InlineData(1001, 0)] [InlineData(1004, 1)]
    public void Russian_plural_rules(long n, int form) => Assert.Equal(form, Plural.FormIndex("ru", n));

    [Theory]
    [InlineData(0, 2)] [InlineData(1, 0)] [InlineData(2, 1)] [InlineData(4, 1)] [InlineData(5, 2)] [InlineData(12, 2)]
    [InlineData(14, 2)] [InlineData(21, 2)] [InlineData(22, 1)] [InlineData(24, 1)] [InlineData(101, 2)] [InlineData(102, 1)]
    [InlineData(112, 2)] [InlineData(1001, 2)]
    public void Polish_plural_rules(long n, int form) => Assert.Equal(form, Plural.FormIndex("pl", n));

    [Theory]
    [InlineData("tr", 0, 0)] [InlineData("tr", 1, 0)] [InlineData("tr", 7, 0)]
    [InlineData("en", 0, 1)] [InlineData("en", 1, 0)] [InlineData("en", 2, 1)] [InlineData("en", 21, 1)]
    [InlineData("de", 1, 0)] [InlineData("de", 3, 1)]
    [InlineData("fr", 0, 0)] [InlineData("fr", 1, 0)] [InlineData("fr", 2, 1)]
    public void Other_plural_rules(string lang, long n, int form) => Assert.Equal(form, Plural.FormIndex(lang, n));

    [Fact]
    public void Plural_select_formats_with_the_count()
    {
        Assert.Equal("{0} файла", Plural.Select("ru", "{0} файл|{0} файла|{0} файлов", 3));
        Assert.Equal("{0} plików", Plural.Select("pl", "{0} plik|{0} pliki|{0} plików", 5));
        Assert.Equal("{0} dosya", Plural.Select("tr", "{0} dosya", 5));
        // fewer forms than the rule asks for: last form wins (never throws)
        Assert.Equal("b", Plural.Select("ru", "a|b", 5));
        // the current (tr) culture through the generated accessor
        Assert.Equal("3 dosya", CommonStrings.Files_Plural(3));
    }

    [Fact]
    public void Plural_resources_read_naturally_in_each_language()
    {
        var rm = CommonStrings.ResourceManager;
        string F(string lang, long n) => string.Format(CultureInfo.GetCultureInfo(lang),
            Plural.Select(lang, rm.GetString("Files_Plural", CultureInfo.GetCultureInfo(lang))!, n), n);
        Assert.Equal("1 file", F("en", 1));
        Assert.Equal("2 files", F("en", 2));
        Assert.Equal("0 fichier", F("fr", 0));
        Assert.Equal("21 файл", F("ru", 21));
        Assert.Equal("3 файла", F("ru", 3));
        Assert.Equal("11 файлов", F("ru", 11));
        Assert.Equal("22 pliki", F("pl", 22));
        Assert.Equal("12 plików", F("pl", 12));
        Assert.Equal("5 Dateien", F("de", 5));
    }

    // ---- language choice and formatting ----

    [Theory]
    [InlineData("tr-TR", "tr")] [InlineData("de-AT", "de")] [InlineData("en-GB", "en")] [InlineData("fr-CA", "fr")]
    [InlineData("ru-RU", "ru")] [InlineData("pl-PL", "pl")] [InlineData("ja-JP", "en")] [InlineData("uk-UA", "en")]
    public void Default_language_follows_windows_or_falls_back_to_english(string windows, string expected) =>
        Assert.Equal(expected, Loc.ResolveDefault(CultureInfo.GetCultureInfo(windows)));

    [Fact]
    public void Formatting_culture_keeps_the_users_regional_variant()
    {
        Assert.Equal("en-GB", Loc.CultureFor("en", CultureInfo.GetCultureInfo("en-GB")).Name);
        Assert.Equal("de-DE", Loc.CultureFor("de", CultureInfo.GetCultureInfo("tr-TR")).Name);
        Assert.Equal("pl-PL", Loc.CultureFor("pl", CultureInfo.GetCultureInfo("en-US")).Name);
        Assert.Equal(6, Loc.Languages.Count);
        Assert.Equal(new[] { "Türkçe", "English", "Deutsch", "Français", "Русский", "Polski" }, Loc.Languages.Select(l => l.NativeName));
    }

    [Fact]
    public void Bytes_and_times_use_the_ui_culture()
    {
        Assert.Equal("tr", Loc.Language);
        Assert.Equal("2,3 MB", Loc.Bytes(2_400_000));
        Assert.Equal("512 B", Loc.Bytes(512));
        var now = new DateTime(2026, 10, 28, 18, 0, 0);
        Assert.Equal("14:05", Loc.When(new DateTime(2026, 10, 28, 14, 5, 0), now));
        Assert.Equal("Dün 09:30", Loc.When(new DateTime(2026, 10, 27, 9, 30, 0), now));
        Assert.Equal("Pazartesi", Loc.When(new DateTime(2026, 10, 26, 9, 30, 0), now));
    }

    // ---- browser extension and MSIX ----

    [Fact]
    public void Extension_locales_are_complete_and_consistent()
    {
        var ext = Path.Combine(RepoRoot, "extension");
        var manifest = File.ReadAllText(Path.Combine(ext, "manifest.json"));
        Assert.Contains("\"__MSG_extName__\"", manifest);
        Assert.Contains("\"default_locale\": \"en\"", manifest);
        Dictionary<string, string> Read(string lang)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ext, "_locales", lang, "messages.json")));
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("message").GetString()!);
        }
        var src = Read("tr");
        var dollar = new Regex(@"\$[A-Z]+\$");
        foreach (var lang in Languages)
        {
            var t = Read(lang);
            Assert.Equal(src.Keys.OrderBy(k => k), t.Keys.OrderBy(k => k));
            foreach (var (k, v) in t)
            {
                Assert.False(string.IsNullOrWhiteSpace(v), $"{lang}: {k} empty");
                Assert.Equal(dollar.Matches(src[k]).Select(m => m.Value).OrderBy(x => x), dollar.Matches(v).Select(m => m.Value).OrderBy(x => x));
            }
        }
        var popup = File.ReadAllText(Path.Combine(ext, "popup.js")) + File.ReadAllText(Path.Combine(ext, "popup.html"));
        foreach (Match m in Regex.Matches(popup, @"i18n\('(\w+)'|data-i18n=""(\w+)"""))
            Assert.Contains(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, src.Keys);
    }

    [Fact]
    public void Msix_manifest_lists_every_ui_language_with_strings()
    {
        var pkg = Path.Combine(RepoRoot, "packaging");
        var doc = XDocument.Load(Path.Combine(pkg, "AppxManifest.xml"));
        var langs = doc.Descendants().Where(e => e.Name.LocalName == "Resource").Select(e => e.Attribute("Language")!.Value).ToArray();
        Assert.Equal(Loc.Languages.Select(l => l.DefaultCulture), langs);
        foreach (var l in langs)
        {
            var resw = XDocument.Load(Path.Combine(pkg, "Strings", l, "Resources.resw"));
            var names = resw.Root!.Elements("data").Select(d => d.Attribute("name")!.Value).ToArray();
            Assert.Contains("AppDescription", names);
            Assert.Contains("AppTileDescription", names);
        }
        Assert.Contains("SatelliteResourceLanguages=tr%3Ben%3Bde%3Bfr%3Bru%3Bpl", File.ReadAllText(Path.Combine(pkg, "build-release.ps1")));
    }

    // ---- no hard-coded Turkish ----

    /// <summary>Sample data (names, demo messages, sample file names) and Debug-only snapshot fixtures.</summary>
    private static readonly string[] AllowedFiles =
    {
        "Services/DemoAirDropService.cs", "Services/DemoContinuityService.cs", "Services/DemoSyncSource.cs",
        "Snapshot.cs", "ContinuitySnapshots.cs", "SnapshotMatrix.cs",
        // Nitro.Sync returns fixed Turkish error strings; this maps them (by value) to localized text.
        "Services/SyncErrors.cs",
    };

    /// <summary>Brand lockup text that is the same in every language.</summary>
    private static readonly string[] AllowedLiterals = { "MADE IN TÜRKİYE" };

    private static readonly Regex TurkishLetters = new("[ğüşöçıİĞÜŞÖÇ]", RegexOptions.Compiled);

    [Fact]
    public void No_hard_coded_turkish_in_app_sources()
    {
        var hits = new List<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            if (AllowedFiles.Contains(Rel(file))) continue;
            var lines = File.ReadAllLines(file);
            bool inBlock = false;
            for (int i = 0; i < lines.Length; i++)
            {
                var code = StripCsComments(lines[i], ref inBlock);
                if (lines[i].Contains("l10n-allow")) continue;
                foreach (Match m in CsString.Matches(code))
                    if (TurkishLetters.IsMatch(m.Value) && !AllowedLiterals.Any(a => m.Value.Contains(a)))
                        hits.Add($"{Rel(file)}:{i + 1}: {m.Value.Trim()}");
            }
        }
        foreach (var file in SourceFiles("*.xaml"))
        {
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var a in AllowedLiterals) line = line.Replace(a, "");
                if (TurkishLetters.IsMatch(line)) hits.Add($"{Rel(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(hits.Count == 0, $"{hits.Count} hard-coded Turkish strings (move them to Localization/*.resx):{Environment.NewLine}" +
                                     string.Join(Environment.NewLine, hits.Take(80)));
    }

    private static readonly Regex CsString = new(@"@""(?:[^""]|"""")*""|\$?""(?:[^""\\\n]|\\.)*""", RegexOptions.Compiled);

    /// <summary>Removes // and /* */ comments from one line (strings are respected well enough for this scan).</summary>
    private static string StripCsComments(string line, ref bool inBlock)
    {
        var sb = new System.Text.StringBuilder();
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            char n = i + 1 < line.Length ? line[i + 1] : '\0';
            if (inBlock)
            {
                if (c == '*' && n == '/') { inBlock = false; i++; }
                continue;
            }
            if (inStr)
            {
                sb.Append(c);
                if (c == '\\') { if (i + 1 < line.Length) sb.Append(line[++i]); }
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && n == '/') break;
            if (c == '/' && n == '*') { inBlock = true; i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(f =>
            {
                var r = Rel(f);
                return !r.StartsWith("bin", StringComparison.Ordinal) && !r.StartsWith("obj", StringComparison.Ordinal)
                       && !r.StartsWith("Localization/Generated/", StringComparison.Ordinal);
            });

    private static string Rel(string f) => Path.GetRelativePath(AppDir, f).Replace('\\', '/');
}
