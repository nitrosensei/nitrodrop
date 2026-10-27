using System.Security.Cryptography;
using System.Text;

namespace AirDrop.Core.Contacts;

/// <summary>Country calling code + national trunk prefix used to expand local phone numbers.</summary>
public sealed record PhoneRegion(string CountryCode, string TrunkPrefix = "0")
{
    public static PhoneRegion Turkey { get; } = new("90", "0");
    public static PhoneRegion Germany { get; } = new("49", "0");
    public static PhoneRegion UnitedKingdom { get; } = new("44", "0");
    public static PhoneRegion UnitedStates { get; } = new("1", "1");
}

/// <summary>
/// Hash candidates for an identifier, to compare with ValidatedEmailHashes / ValidatedPhoneHashes.
///
/// Known (primary sources): SHA-256 over the UTF-8 identifier, lowercase hex (PrivateDrop paper §2.4 and
/// PrivateDrop-Base ContactsChecker/Hash.swift). PrivateDrop hashes e-mail addresses as stored and phone
/// numbers after stripping a leading "00"/"+".
/// UNVERIFIED: Apple's exact canonical forms. Best belief: e-mail trimmed + lowercased; phone in E.164
/// digits without "+" (e.g. "905551234567"). Because we only compare against the user's own contacts,
/// we emit every plausible form (with/without "+", as typed, lowercased or not) — more candidates cost
/// nothing and never reveal non-contacts. We never try to reverse hashes.
/// </summary>
public static class ContactHashing
{
    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static IEnumerable<string> EmailForms(string email)
    {
        var t = email.Trim();
        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        if (t.Length == 0) yield break;
        var lower = t.ToLowerInvariant();
        yield return lower; // primary (UNVERIFIED)
        if (lower != t) yield return t;
    }

    /// <summary>Phone forms: "905551234567" (primary, UNVERIFIED), "+905551234567", and the digits as typed.</summary>
    public static IEnumerable<string> PhoneForms(string phone, PhoneRegion region)
    {
        var t = phone.Trim();
        if (t.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        int ext = t.IndexOfAny([';', ',', 'x', 'X']);
        if (ext > 0) t = t[..ext];
        bool plus = t.TrimStart().StartsWith('+');
        var digits = new string(t.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length < 4) yield break;

        // Candidate E.164 numbers (digits only, with country code). Ambiguous inputs yield several.
        var internationals = new List<string>();
        if (plus) internationals.Add(digits);
        else if (digits.StartsWith("00")) internationals.Add(digits[2..]);
        else
        {
            var tp = region.TrunkPrefix;
            if (tp.Length > 0 && digits.StartsWith(tp, StringComparison.Ordinal))
                internationals.Add(region.CountryCode + digits[tp.Length..]); // "0555 123 45 67" → 905551234567
            if (digits.StartsWith(region.CountryCode, StringComparison.Ordinal) && digits.Length >= region.CountryCode.Length + 9)
                internationals.Add(digits);                                     // "905551234567" typed without "+"
            if (internationals.Count == 0) internationals.Add(region.CountryCode + digits); // "555 123 45 67"
        }

        var forms = new List<string>();
        foreach (var i in internationals) forms.Add(i);           // primary form (UNVERIFIED)
        foreach (var i in internationals) forms.Add("+" + i);
        forms.Add(digits);                                         // as typed, digits only
        foreach (var f in forms.Distinct()) yield return f;
    }

    public static IEnumerable<string> EmailHashes(string email) => EmailForms(email).Select(Sha256Hex);
    public static IEnumerable<string> PhoneHashes(string phone, PhoneRegion region) => PhoneForms(phone, region).Select(Sha256Hex);
}
