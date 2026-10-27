using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nitro.Sync.Security;

namespace Nitro.Sync.Net;

/// <summary>A report from an iOS Shortcut, after token check. App and/or Url may be null.</summary>
internal sealed record IosReport(IosDevice Device, string? App, string? Url, string? Title);

/// <summary>
/// Plain-HTTP LAN endpoint for iOS Shortcuts ("Get Contents of URL" cannot pin a self-signed certificate,
/// so this is HTTP + a per-device bearer token, LAN source addresses only):
///   POST /ios/v1/activity   Authorization: Bearer &lt;token&gt;  (or ?t=&lt;token&gt;)
///   body JSON {"app": "Instagram", "url": "https://…", "title": "…"} — either field optional;
///   a form body (url=…&amp;app=…) or a bare URL as text/plain is also accepted (share sheet).
/// Tokens are 32 random bytes; only SHA-256 hashes are stored. 5 bad tokens from one IP → 10 min lockout.
/// </summary>
internal sealed class IosReceiver : IAsyncDisposable
{
    public const string ActivityPath = "/ios/v1/activity";
    private const int MaxBody = 8 * 1024, MaxHeader = 8 * 1024;

    private readonly PeerStore _store;
    private readonly Func<IosReport, Task> _onReport;
    private readonly Action<string> _log;
    private readonly RateLimiter _perIp = new(burst: 10, perSecond: 0.5);
    private readonly Dictionary<string, (int fails, DateTime until)> _lockout = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(8);
    private Socket? _listener;
    private Task? _loop;

    public int Port { get; private set; }
    /// <summary>Tests use loopback; production refuses non-LAN sources (see <see cref="NetworkScope.IsLocal"/>).</summary>
    public Func<IPAddress, bool> IsAllowedSource { get; init; } = NetworkScope.IsLocal;

    public IosReceiver(PeerStore store, Func<IosReport, Task> onReport, Action<string> log)
    {
        _store = store;
        _onReport = onReport;
        _log = log;
    }

    public void Start(int port)
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true, ExclusiveAddressUse = true };
        s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        s.Listen(16);
        _listener = s;
        Port = ((IPEndPoint)s.LocalEndPoint!).Port;
        _loop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket c;
            try { c = await _listener!.AcceptAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (!await _slots.WaitAsync(0)) { c.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    t.CancelAfter(TimeSpan.FromSeconds(10));
                    await HandleAsync(c, t.Token);
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException) { }
                catch (Exception e) { _log($"ios: {e.Message}"); }
                finally { c.Dispose(); _slots.Release(); }
            });
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken ct)
    {
        var remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        await using var ns = new NetworkStream(socket, ownsSocket: false);
        if (!IsAllowedSource(remote)) { await Reply(ns, 403, "lan only"); return; }
        var ip = remote.ToString();
        if (IsLockedOut(ip) || !_perIp.TryAcquire(ip)) { await Reply(ns, 429, "slow down"); return; }

        // ---- parse request (single request per connection) ----
        var head = await ReadHeadAsync(ns, ct);
        if (head == null) return;
        var (lines, rest) = head.Value;
        var reqLine = lines[0].Split(' ');
        if (reqLine.Length != 3) { await Reply(ns, 400, "bad request"); return; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        var target = reqLine[1];
        var path = target.Split('?', 2)[0];
        if (reqLine[0] != "POST" || path != ActivityPath) { await Reply(ns, 404, "not found"); return; }
        if (headers.ContainsKey("Transfer-Encoding")) { await Reply(ns, 411, "length required"); return; }
        int len = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (len < 0 || len > MaxBody) { await Reply(ns, 413, "too large"); return; }
        var body = new byte[len];
        int have = Math.Min(rest.Length, len);
        Array.Copy(rest, body, have);
        while (have < len)
        {
            int r = await ns.ReadAsync(body.AsMemory(have), ct);
            if (r == 0) return;
            have += r;
        }

        // ---- token ----
        string? token = null;
        if (headers.TryGetValue("Authorization", out var auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth[7..].Trim();
        token ??= Query(target, "t");
        var device = token == null ? null : Match(token);
        if (device == null)
        {
            RegisterFailure(ip);
            await Reply(ns, 401, "token");
            return;
        }

        // ---- payload ----
        var (app, url, title) = ParseBody(body, headers.TryGetValue("Content-Type", out var ctype) ? ctype : "");
        app ??= Query(target, "app");
        if (url != null && !UrlPolicy.TryNormalize(url, out url)) { await Reply(ns, 400, "url"); return; }
        if (app == null && url == null) { await Reply(ns, 400, "empty"); return; }
        app = app == null ? null : Model.StateSanitizer.Clamp(app, 80);
        title = title == null ? null : Model.StateSanitizer.Clamp(title, Model.StateLimits.MaxTitle);
        await _onReport(new IosReport(device, app, url, title));
        await Reply(ns, 200, "ok");
    }

    internal static (string? app, string? url, string? title) ParseBody(byte[] body, string contentType)
    {
        if (body.Length == 0) return (null, null, null);
        var text = Encoding.UTF8.GetString(body).Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                string? S(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    ? NullIfEmpty(v.GetString()) : null;
                return (S("app"), S("url"), S("title"));
            }
            catch (JsonException) { return (null, null, null); }
        }
        if (contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) || (text.Contains('=') && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            var f = text.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            return (NullIfEmpty(f.GetValueOrDefault("app")), NullIfEmpty(f.GetValueOrDefault("url")), NullIfEmpty(f.GetValueOrDefault("title")));
        }
        return (null, NullIfEmpty(text), null); // bare URL (text/plain)
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Query(string target, string key)
    {
        int q = target.IndexOf('?');
        if (q < 0) return null;
        foreach (var kv in target[(q + 1)..].Split('&'))
        {
            var p = kv.Split('=', 2);
            if (p.Length == 2 && p[0] == key) return NullIfEmpty(Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        }
        return null;
    }

    private IosDevice? Match(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var d in _store.IosDevices)
        {
            byte[] stored;
            try { stored = Convert.FromHexString(d.TokenSha256); } catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(stored, hash)) return d;
        }
        return null;
    }

    public static (string token, string hash) NewToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    }

    private bool IsLockedOut(string ip)
    {
        lock (_lockout) return _lockout.TryGetValue(ip, out var l) && l.fails >= 5 && l.until > DateTime.UtcNow;
    }

    private void RegisterFailure(string ip)
    {
        lock (_lockout)
        {
            if (_lockout.Count > 1024) _lockout.Clear();
            var l = _lockout.TryGetValue(ip, out var x) && x.until > DateTime.UtcNow ? x : (0, DateTime.UtcNow);
            _lockout[ip] = (l.Item1 + 1, DateTime.UtcNow.AddMinutes(10));
        }
    }

    private static async Task<(List<string> lines, byte[] rest)?> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[MaxHeader];
        int len = 0;
        while (true)
        {
            int idx = IndexOf(buf.AsSpan(0, len), "\r\n\r\n"u8);
            if (idx >= 0)
            {
                var head = Encoding.Latin1.GetString(buf, 0, idx);
                return (head.Split("\r\n").ToList(), buf[(idx + 4)..len]);
            }
            if (len == buf.Length) throw new InvalidDataException("header too large");
            int r = await s.ReadAsync(buf.AsMemory(len), ct);
            if (r == 0) return null;
            len += r;
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> hay, ReadOnlySpan<byte> needle) => hay.IndexOf(needle);

    private static async Task Reply(Stream s, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Dispose();
        if (_loop != null) await _loop;
        _cts.Dispose();
    }
}
