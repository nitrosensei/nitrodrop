using System.Text;

namespace AirDrop.Core.Contacts;

/// <summary>One contact from a vCard file.</summary>
public sealed record VCardContact(string DisplayName, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails, byte[]? Photo);

/// <summary>
/// vCard 2.1 / 3.0 / 4.0 reader (RFC 2426, RFC 6350) for files exported by iCloud.com, Google Contacts,
/// Android and Outlook: line unfolding, property groups (item1.TEL), escaped values, quoted-printable (2.1),
/// embedded photos (ENCODING=b / BASE64, data: URIs). Remote photo URLs are ignored (no network access).
/// </summary>
public static class VCard
{
    public static IReadOnlyList<VCardContact> ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static IReadOnlyList<VCardContact> Parse(string text)
    {
        var result = new List<VCardContact>();
        Builder? cur = null;
        foreach (var line in Unfold(text))
        {
            if (!TrySplit(line, out var name, out var parameters, out var value)) continue;
            switch (name)
            {
                case "BEGIN" when value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase):
                    cur = new Builder();
                    break;
                case "END" when value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase):
                    if (cur?.Build() is { } c) result.Add(c);
                    cur = null;
                    break;
                default:
                    cur?.Add(name, parameters, value);
                    break;
            }
        }
        return result;
    }

    /// <summary>Joins folded lines (CRLF + space/tab) and quoted-printable soft breaks ("=" at line end).</summary>
    internal static IEnumerable<string> Unfold(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        bool qpContinuation = false;
        foreach (var raw in lines)
        {
            if (sb.Length > 0 && (qpContinuation || raw.StartsWith(' ') || raw.StartsWith('\t')))
            {
                sb.Append(qpContinuation ? raw : raw[1..]);
            }
            else
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                sb.Append(raw);
            }
            var s = sb.ToString();
            qpContinuation = s.EndsWith('=') && s.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            if (qpContinuation) sb.Length--; // drop the soft-break "="
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static bool TrySplit(string line, out string name, out List<(string key, string val)> parameters, out string value)
    {
        name = value = "";
        parameters = [];
        // The value starts at the first ':' outside double quotes.
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return false;
        value = line[(colon + 1)..];
        var head = SplitUnquoted(line[..colon], ';');
        var n = head[0];
        int dot = n.LastIndexOf('.');
        if (dot >= 0) n = n[(dot + 1)..]; // strip group ("item1.TEL")
        name = n.Trim().ToUpperInvariant();
        foreach (var p in head.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq < 0) parameters.Add(("TYPE", p.Trim().Trim('"'))); // vCard 2.1 bare parameter, e.g. ";JPEG;BASE64"
            else parameters.Add((p[..eq].Trim().ToUpperInvariant(), p[(eq + 1)..].Trim().Trim('"')));
        }
        return true;
    }

    private static List<string> SplitUnquoted(string s, char sep)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (var c in s)
        {
            if (c == '"') quoted = !quoted;
            if (c == sep && !quoted) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    internal static string Unescape(string v)
    {
        if (!v.Contains('\\')) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                char n = v[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(v[i]);
        }
        return sb.ToString();
    }

    private static List<string> SplitComponents(string v)
    {
        // Split on unescaped ';' then unescape each component.
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length) { sb.Append(v[i]).Append(v[++i]); }
            else if (v[i] == ';') { parts.Add(Unescape(sb.ToString())); sb.Clear(); }
            else sb.Append(v[i]);
        }
        parts.Add(Unescape(sb.ToString()));
        return parts;
    }

    private static string DecodeQuotedPrintable(string v, string? charset)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '=' && i + 2 < v.Length && Uri.IsHexDigit(v[i + 1]) && Uri.IsHexDigit(v[i + 2]))
            {
                bytes.Add(Convert.ToByte(v.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(v[i].ToString()));
        }
        Encoding enc;
        try { enc = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { enc = Encoding.UTF8; }
        return enc.GetString(bytes.ToArray());
    }

    private sealed class Builder
    {
        private string? _fn, _n, _org;
        private readonly List<string> _phones = [], _emails = [];
        private byte[]? _photo;

        public void Add(string name, List<(string key, string val)> ps, string rawValue)
        {
            string? Param(string k) => ps.FirstOrDefault(p => p.key == k).val;
            bool HasType(string t) => ps.Any(p => p.key is "TYPE" or "ENCODING" or "VALUE" &&
                                                  p.val.Split(',').Any(x => x.Trim().Equals(t, StringComparison.OrdinalIgnoreCase)));
            var value = rawValue;
            if ((Param("ENCODING") ?? "").Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase) || HasType("QUOTED-PRINTABLE"))
                value = DecodeQuotedPrintable(value, Param("CHARSET"));

            switch (name)
            {
                case "FN":
                    _fn = Unescape(value).Trim();
                    break;
                case "N":
                    var c = SplitComponents(value); // Family;Given;Additional;Prefix;Suffix
                    string Part(int i) => i < c.Count ? c[i].Trim() : "";
                    _n = string.Join(" ", new[] { Part(3), Part(1), Part(2), Part(0), Part(4) }.Where(x => x.Length > 0));
                    break;
                case "ORG":
                    _org = SplitComponents(value).FirstOrDefault()?.Trim();
                    break;
                case "TEL":
                    var tel = Unescape(value).Trim();
                    if (tel.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) tel = tel[4..];
                    if (tel.Length > 0 && !_phones.Contains(tel)) _phones.Add(tel);
                    break;
                case "EMAIL":
                    var mail = Unescape(value).Trim();
                    if (mail.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) mail = mail[7..];
                    if (mail.Length > 0 && !_emails.Contains(mail, StringComparer.OrdinalIgnoreCase)) _emails.Add(mail);
                    break;
                case "PHOTO":
                    _photo ??= DecodePhoto(value, HasType("B") || HasType("BASE64"));
                    break;
            }
        }

        private static byte[]? DecodePhoto(string value, bool base64)
        {
            var v = value.Trim();
            try
            {
                if (v.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = v.IndexOf(',');
                    if (comma < 0 || !v[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                    return Clean(Convert.FromBase64String(StripWs(v[(comma + 1)..])));
                }
                if (base64) return Clean(Convert.FromBase64String(StripWs(v)));
            }
            catch (FormatException) { }
            return null; // http(s) URIs etc. are ignored on purpose
        }

        private static string StripWs(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

        private static byte[]? Clean(byte[] b) => b.Length is > 0 and < 10 * 1024 * 1024 ? b : null;

        public VCardContact? Build()
        {
            var name = !string.IsNullOrWhiteSpace(_fn) ? _fn! : !string.IsNullOrWhiteSpace(_n) ? _n! :
                       !string.IsNullOrWhiteSpace(_org) ? _org! : _emails.FirstOrDefault() ?? _phones.FirstOrDefault();
            if (name == null) return null;
            return new VCardContact(name, _phones.ToList(), _emails.ToList(), _photo);
        }
    }
}
