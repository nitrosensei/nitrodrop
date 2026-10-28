using System;

namespace AirDrop.App.Localization;

/// <summary>
/// Integer plural rules (CLDR, cardinal, integers only) for the UI languages. A plural resource holds its forms
/// separated by <c>|</c> in this order (keys end with <c>_Plural</c>):
/// <list type="bullet">
/// <item>tr: 1 form — Turkish keeps the noun singular after a numeral ("3 dosya").</item>
/// <item>en, de: one | other.</item>
/// <item>fr: one | other — 0 and 1 are "one" ("0 fichier", "1 fichier", "2 fichiers").</item>
/// <item>ru: one | few | many — 1, 21, 101 · 2–4, 22–24 · 0, 5–20, 25–30, 11–14 …</item>
/// <item>pl: one | few | many — only 1 · 2–4, 22–24 (not 12–14) · 0, 5–21, 25–31 …</item>
/// </list>
/// WPF-free (linked into AirDrop.Tests).
/// </summary>
public static class Plural
{
    public const char Separator = '|';

    /// <summary>Number of forms a plural resource must have in <paramref name="language"/> (two-letter code).</summary>
    public static int FormCount(string language) => language switch
    {
        "tr" => 1,
        "ru" or "pl" => 3,
        _ => 2,
    };

    /// <summary>Index of the form to use for <paramref name="n"/> (0-based, see <see cref="FormCount"/>).</summary>
    public static int FormIndex(string language, long n)
    {
        n = Math.Abs(n);
        long m10 = n % 10, m100 = n % 100;
        bool few = m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14);
        switch (language)
        {
            case "tr":
                return 0;
            case "fr":
                return n <= 1 ? 0 : 1;
            case "ru":
                if (m10 == 1 && m100 != 11) return 0;
                return few ? 1 : 2;
            case "pl":
                if (n == 1) return 0;
                return few ? 1 : 2;
            default:   // en, de
                return n == 1 ? 0 : 1;
        }
    }

    /// <summary>Picks the form for <paramref name="n"/> out of a <c>|</c>-separated pattern (unformatted).</summary>
    public static string Select(string language, string pattern, long n)
    {
        var forms = pattern.Split(Separator);
        int i = Math.Min(FormIndex(language, n), forms.Length - 1);
        return forms[i];
    }
}
