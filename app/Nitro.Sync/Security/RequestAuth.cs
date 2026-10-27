using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nitro.Sync.Security;

/// <summary>
/// Per-request authentication between paired PCs. The HTTPS server cannot require client certificates
/// (AirDrop.Core's MiniHttpServer), so the client proves it holds the pairing key:
///   X-Nitro-Device: id, X-Nitro-Time: unix seconds, X-Nitro-Nonce: 16 random bytes (hex),
///   X-Nitro-Sig: base64 HMAC-SHA256(key, "METHOD\nPATH\nTIME\nNONCE\nsha256hex(body)\nTARGETID")
/// ±5 min clock skew, nonces remembered for 10 min (replay protection). The server side pins the client's
/// identity to the device id; the client pins the server's certificate.
/// </summary>
public static class RequestAuth
{
    public const string HDevice = "X-Nitro-Device", HTime = "X-Nitro-Time", HNonce = "X-Nitro-Nonce", HSig = "X-Nitro-Sig";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Canonical(string method, string path, long time, string nonce, ReadOnlySpan<byte> body, string targetId) =>
        string.Join('\n', method.ToUpperInvariant(), path, time.ToString(CultureInfo.InvariantCulture), nonce,
            Convert.ToHexStringLower(SHA256.HashData(body)), targetId.ToLowerInvariant());

    public static string Sign(byte[] key, string canonical) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));

    public static Dictionary<string, string> Headers(string deviceId, byte[] key, string method, string path, ReadOnlySpan<byte> body, string targetId, DateTimeOffset? now = null)
    {
        long t = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        return new Dictionary<string, string>
        {
            [HDevice] = deviceId, [HTime] = t.ToString(CultureInfo.InvariantCulture), [HNonce] = nonce,
            [HSig] = Sign(key, Canonical(method, path, t, nonce, body, targetId)),
        };
    }

    public static bool Verify(byte[] key, string method, string path, string? time, string? nonce, string? sig,
        ReadOnlySpan<byte> body, string targetId, ReplayCache replay, DateTimeOffset? now = null)
    {
        if (time == null || nonce == null || sig == null) return false;
        if (nonce.Length != 32 || !long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out long t)) return false;
        var ts = DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(t, 0, 253402300799));
        if ((ts - (now ?? DateTimeOffset.UtcNow)).Duration() > MaxSkew) return false;
        byte[] given;
        try { given = Convert.FromBase64String(sig); } catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(method, path, t, nonce, body, targetId)));
        if (!CryptographicOperations.FixedTimeEquals(given, expected)) return false;
        return replay.TryAdd(nonce);
    }
}

/// <summary>Remembers nonces for a while; bounded.</summary>
public sealed class ReplayCache(TimeSpan? ttl = null, int max = 20000)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();

    public bool TryAdd(string nonce)
    {
        var now = DateTime.UtcNow;
        if (_seen.Count > max)
            foreach (var kv in _seen)
                if (now - kv.Value > _ttl) _seen.TryRemove(kv.Key, out _);
        if (_seen.Count > max) return false; // under attack: fail closed
        return _seen.TryAdd(nonce, now);
    }
}

/// <summary>Token bucket per key (usually the remote IP).</summary>
public sealed class RateLimiter(double burst, double perSecond)
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private sealed class Bucket { public double Tokens; public long Last; }

    public bool TryAcquire(string key, double cost = 1)
    {
        var now = Environment.TickCount64;
        if (_buckets.Count > 4096) _buckets.Clear();
        var b = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = burst, Last = now });
        lock (b)
        {
            b.Tokens = Math.Min(burst, b.Tokens + (now - b.Last) / 1000.0 * perSecond);
            b.Last = now;
            if (b.Tokens < cost) return false;
            b.Tokens -= cost;
            return true;
        }
    }
}

public static class NetworkScope
{
    /// <summary>Loopback, RFC 1918, link-local, CGNAT-free private ranges, IPv6 ULA/link-local. Used for "LAN only".</summary>
    public static bool IsLocal(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254);
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (a.IsIPv6LinkLocal) return true;
            var b = a.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7
        }
        return false;
    }
}
