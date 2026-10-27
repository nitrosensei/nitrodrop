using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AirDrop.Core.Http;
using AirDrop.Core.Net;
using Nitro.Sync.Model;
using Nitro.Sync.Security;

namespace Nitro.Sync.Net;

/// <summary>What the HTTPS endpoints need from the service.</summary>
internal interface ISyncHost
{
    string DeviceId { get; }
    string DeviceName { get; }
    string CertSha256 { get; }
    PeerStore Store { get; }
    (byte[] json, string etag) SnapshotState();
    Task WaitForStateChangeAsync(string etag, TimeSpan max, CancellationToken ct);
    Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken ct);
    void OnPaired(PairedPeer peer);
    void OnUnpairedByPeer(PairedPeer peer);
    Task<bool> HandleOpenAsync(PairedPeer from, string url, string? title, CancellationToken ct);
    Task<bool> HandleLaunchAsync(PairedPeer from, string appId, CancellationToken ct);
    void Log(string message);
}

/// <summary>
/// HTTPS JSON endpoints for peer sync on AirDrop.Core's <see cref="MiniHttpServer"/> (same TLS identity as
/// AirDrop). Everything except /info and the pairing handshake requires a signed request from a paired device.
/// </summary>
internal sealed class SyncServer : IAsyncDisposable
{
    private readonly ISyncHost _host;
    private readonly MiniHttpServer _http;
    private readonly RateLimiter _perIp = new(burst: 30, perSecond: 10);
    private readonly RateLimiter _pairPerIp = new(burst: 3, perSecond: 1.0 / 20);
    private readonly ReplayCache _replay = new();
    private readonly ConcurrentDictionary<string, PendingPair> _pending = new();
    private const int MaxPending = 4;
    public static readonly TimeSpan PairDecisionTimeout = TimeSpan.FromSeconds(90);

    private sealed class PendingPair
    {
        public required string PeerId, PeerName, PeerCert;
        public required byte[] Transcript, Key;
        public required Task<bool> Decision;
        public required CancellationTokenSource Cts;
        public DateTime Expires = DateTime.UtcNow + PairDecisionTimeout + TimeSpan.FromSeconds(10);
    }

    public int Port => _http.Port;

    public SyncServer(ISyncHost host, X509Certificate2 cert, NetworkBinding binding)
    {
        _host = host;
        _http = new MiniHttpServer(cert, binding, HandleAsync) { Log = m => host.Log("http: " + m) };
    }

    public void Start(int port) => _http.Start(port);

    private async Task<HttpResponse> HandleAsync(HttpRequest req, CancellationToken ct)
    {
        var ip = req.RemoteEndPoint.Address.ToString();
        if (!_perIp.TryAcquire(ip)) return Status(429);
        var pathOnly = req.Path.Split('?', 2)[0];
        try
        {
            switch (req.Method, pathOnly)
            {
                case ("GET", SyncProtocol.Info):
                    return JsonResponse(new InfoResponse(_host.DeviceId, _host.DeviceName, SyncProtocol.Version));
                case ("POST", SyncProtocol.PairStart):
                    if (!_pairPerIp.TryAcquire(ip)) return Status(429);
                    return await PairStartAsync(req, ct);
                case ("POST", SyncProtocol.PairFinish):
                    return await PairFinishAsync(req, ct);
                case ("GET", SyncProtocol.State):
                case ("POST", SyncProtocol.Open):
                case ("POST", SyncProtocol.Launch):
                case ("POST", SyncProtocol.Unpair):
                    return await AuthenticatedAsync(req, pathOnly, ct);
                default:
                    return Status(404);
            }
        }
        catch (InvalidDataException) { return Status(413, close: true); }
        catch (JsonException) { return Status(400); }
    }

    // ---------------- pairing ----------------

    private async Task<HttpResponse> PairStartAsync(HttpRequest req, CancellationToken ct)
    {
        foreach (var kv in _pending)
            if (kv.Value.Expires < DateTime.UtcNow && _pending.TryRemove(kv.Key, out var old)) old.Cts.Cancel();
        if (_pending.Count >= MaxPending) return Status(429);

        var body = await ReadBodyAsync(req, SyncProtocol.MaxSmallBody, ct);
        var r = JsonSerializer.Deserialize<PairStartRequest>(body, SyncProtocol.Json);
        if (r == null || !IsId(r.DeviceId) || r.DeviceId.Equals(_host.DeviceId, StringComparison.OrdinalIgnoreCase)
            || r.CertSha256 is not { Length: 64 } || !IsHex(r.CertSha256)) return Status(400);
        byte[] pubA, nonceA;
        try { pubA = Convert.FromBase64String(r.EcdhPublicKey); nonceA = Convert.FromBase64String(r.Nonce); }
        catch (FormatException) { return Status(400); }
        if (nonceA.Length != 16 || pubA.Length > 256) return Status(400);

        using var ecdh = PairingCrypto.NewEcdh();
        var pubB = ecdh.ExportSubjectPublicKeyInfo();
        var nonceB = RandomNumberGenerator.GetBytes(16);
        var transcript = PairingCrypto.Transcript(r.DeviceId, _host.DeviceId, r.CertSha256, _host.CertSha256, pubA, pubB, nonceA, nonceB);
        byte[] key;
        try { key = PairingCrypto.DeriveKey(ecdh, pubA, nonceA, nonceB, transcript); }
        catch (CryptographicException) { return Status(400); }

        var name = StateSanitizer.Clamp(r.Name, StateLimits.MaxName);
        var cts = new CancellationTokenSource(PairDecisionTimeout);
        var prompt = new PairingPrompt(r.DeviceId, name, PairingCrypto.Code(transcript), IsInitiator: false);
        var decision = SafeConfirm(prompt, cts.Token);
        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _pending[sessionId] = new PendingPair
        {
            PeerId = r.DeviceId, PeerName = name, PeerCert = r.CertSha256.ToLowerInvariant(),
            Transcript = transcript, Key = key, Decision = decision, Cts = cts,
        };
        return JsonResponse(new PairStartResponse(sessionId, _host.DeviceId, _host.DeviceName,
            Convert.ToBase64String(pubB), Convert.ToBase64String(nonceB)));
    }

    private async Task<bool> SafeConfirm(PairingPrompt prompt, CancellationToken ct)
    {
        try { return await _host.ConfirmPairingAsync(prompt, ct); }
        catch (Exception e) { _host.Log($"pair confirm failed: {e.Message}"); return false; }
    }

    private async Task<HttpResponse> PairFinishAsync(HttpRequest req, CancellationToken ct)
    {
        var body = await ReadBodyAsync(req, SyncProtocol.MaxSmallBody, ct);
        var r = JsonSerializer.Deserialize<PairFinishRequest>(body, SyncProtocol.Json);
        if (r == null || !_pending.TryRemove(r.SessionId ?? "", out var p)) return Status(404);
        try
        {
            byte[] mac;
            try { mac = Convert.FromBase64String(r.Confirm); } catch (FormatException) { return Status(400); }
            if (!CryptographicOperations.FixedTimeEquals(mac, PairingCrypto.Confirm(p.Key, "A", p.Transcript)))
                return Status(403);
            bool accepted;
            try { accepted = await p.Decision.WaitAsync(PairDecisionTimeout, ct); }
            catch (TimeoutException) { accepted = false; }
            if (!accepted) return Status(403);
            var peer = _host.Store.AddOrReplace(p.PeerId, p.PeerName, p.PeerCert, p.Key);
            _host.OnPaired(peer);
            return JsonResponse(new PairFinishResponse(Convert.ToBase64String(PairingCrypto.Confirm(p.Key, "B", p.Transcript))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(p.Key);
            p.Cts.Cancel(); // closes the local prompt if it is still open
            p.Cts.Dispose();
        }
    }

    // ---------------- authenticated endpoints ----------------

    private async Task<HttpResponse> AuthenticatedAsync(HttpRequest req, string path, CancellationToken ct)
    {
        var deviceId = req.Header(RequestAuth.HDevice);
        var peer = deviceId == null ? null : _host.Store.Find(deviceId);
        if (peer == null)
        {
            await DrainAsync(req, ct);
            return Status(401);
        }
        int limit = path == SyncProtocol.State ? 0 : SyncProtocol.MaxSmallBody;
        var body = await ReadBodyAsync(req, limit, ct);
        byte[] key = _host.Store.KeyOf(peer);
        try
        {
            if (!RequestAuth.Verify(key, req.Method, req.Path, req.Header(RequestAuth.HTime), req.Header(RequestAuth.HNonce),
                    req.Header(RequestAuth.HSig), body, _host.DeviceId, _replay))
                return Status(401);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        switch (path)
        {
            case SyncProtocol.State:
                return await StateAsync(req, ct);
            case SyncProtocol.Open:
            {
                var r = JsonSerializer.Deserialize<OpenRequest>(body, SyncProtocol.Json);
                if (r == null || !UrlPolicy.TryNormalize(r.Url, out var url)) return Status(400);
                var title = r.Title == null ? null : StateSanitizer.Clamp(r.Title, StateLimits.MaxTitle);
                return Status(await _host.HandleOpenAsync(peer, url, title, ct) ? 200 : 403);
            }
            case SyncProtocol.Launch:
            {
                var r = JsonSerializer.Deserialize<LaunchRequest>(body, SyncProtocol.Json);
                if (r == null || string.IsNullOrEmpty(r.AppId) || r.AppId.Length > 64) return Status(400);
                return Status(await _host.HandleLaunchAsync(peer, r.AppId, ct) ? 200 : 403);
            }
            case SyncProtocol.Unpair:
                if (_host.Store.Remove(peer.DeviceId)) _host.OnUnpairedByPeer(peer);
                return Status(200);
        }
        return Status(404);
    }

    private async Task<HttpResponse> StateAsync(HttpRequest req, CancellationToken ct)
    {
        var (json, etag) = _host.SnapshotState();
        var ifNoneMatch = req.Header("If-None-Match");
        var wait = ParseWait(req.Path);
        if (ifNoneMatch == etag && wait > TimeSpan.Zero)
        {
            await _host.WaitForStateChangeAsync(etag, wait, ct);
            (json, etag) = _host.SnapshotState();
        }
        if (ifNoneMatch == etag)
        {
            var nm = Status(304);
            nm.Headers["ETag"] = etag;
            return nm;
        }
        var resp = new HttpResponse { Status = 200, Body = json };
        resp.Headers["Content-Type"] = "application/json";
        resp.Headers["ETag"] = etag;
        return resp;
    }

    internal static TimeSpan ParseWait(string path)
    {
        int q = path.IndexOf('?');
        if (q < 0) return TimeSpan.Zero;
        foreach (var kv in path[(q + 1)..].Split('&'))
            if (kv.StartsWith("wait=", StringComparison.Ordinal) && int.TryParse(kv.AsSpan(5), out int s) && s > 0)
                return TimeSpan.FromSeconds(Math.Min(s, SyncProtocol.MaxLongPoll.TotalSeconds));
        return TimeSpan.Zero;
    }

    // ---------------- helpers ----------------

    internal static async Task<byte[]> ReadBodyAsync(HttpRequest req, int max, CancellationToken ct)
    {
        if (req.Header("Content-Length") is { } cl && long.TryParse(cl, out var len) && len > max)
            throw new InvalidDataException("body too large");
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await req.Body.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("body too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static async Task DrainAsync(HttpRequest req, CancellationToken ct)
    {
        try { await ReadBodyAsync(req, SyncProtocol.MaxSmallBody, ct); } catch (InvalidDataException) { }
    }

    private static bool IsId(string? s) => s is { Length: > 0 and <= 64 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    private static bool IsHex(string s) => s.All(char.IsAsciiHexDigit);

    internal static HttpResponse Status(int status, bool close = false) => HttpResponse.Empty(status, close);

    private static HttpResponse JsonResponse<T>(T value)
    {
        var r = new HttpResponse { Status = 200, Body = JsonSerializer.SerializeToUtf8Bytes(value, SyncProtocol.Json) };
        r.Headers["Content-Type"] = "application/json";
        return r;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _pending.Values) p.Cts.Cancel();
        await _http.DisposeAsync();
    }
}
