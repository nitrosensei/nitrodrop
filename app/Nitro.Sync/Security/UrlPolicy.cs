namespace Nitro.Sync.Security;

/// <summary>
/// The only URLs NitroDrop will ever open on behalf of a remote device: absolute http/https, no control
/// characters, bounded length. Everything else (javascript:, file:, data:, vbscript:, ms-*: protocol
/// handlers, UNC paths…) is refused — opening them could run code.
/// </summary>
public static class UrlPolicy
{
    public const int MaxUrlLength = 4096;

    public static bool TryNormalize(string? input, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxUrlLength) return false;
        var s = input.Trim();
        foreach (var c in s)
            if (char.IsControl(c) || c == ' ' || c == '"' || c == '<' || c == '>' || c == '\\' || c == '^' || c == '`' || c == '|')
                return false;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.IsUnc || uri.IsFile || string.IsNullOrEmpty(uri.Host)) return false;
        // Raw text must also start with the scheme (guards against odd Uri parsing of leading junk).
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        url = uri.AbsoluteUri;
        return url.Length <= MaxUrlLength;
    }

    public static bool IsAllowed(string? input) => TryNormalize(input, out _);
}
