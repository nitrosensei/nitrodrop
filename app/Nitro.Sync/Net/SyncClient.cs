using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using AirDrop.Core.Net;
using Nitro.Sync.Model;
using Nitro.Sync.Security;

namespace Nitro.Sync.Net;

public sealed class SyncException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// HTTPS client to one peer. With <c>pinnedCertSha256</c> set the TLS handshake fails unless the server
/// presents exactly that certificate (TOFU pin); without it (pairing only) any certificate is accepted and
/// its fingerprint captured in <see cref="ObservedCertSha256"/>.
/// </summary>
internal sealed class SyncClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string? _pin;

    public string? ObservedCertSha256 { get; private set; }

    public SyncClient(IPEndPoint endPoint, NetworkBinding binding, string? pinnedCertSha256)
    {
        _pin = pinnedCertSha256?.ToLowerInvariant();
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => new NetworkStream(await binding.ConnectAsync(endPoint, ct), ownsSocket: true),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert == null) return false;
                    var fp = PairingCrypto.CertFingerprint(cert);
                    ObservedCertSha256 = fp;
                    return _pin == null || fp == _pin;
                },
                EnabledSslProtocols = SslProtocols.None,
            },
            MaxConnectionsPerServer = 2,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var host = endPoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{new IPAddress(endPoint.Address.GetAddressBytes())}]" : endPoint.Address.ToString();
        _base = new Uri($"https://{host}:{endPoint.Port}");
    }

    public async Task<T> PostJsonAsync<T>(string path, object body, TimeSpan timeout, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, SyncProtocol.Json);
        var (status, resp, _) = await SendAsync(HttpMethod.Post, path, bytes, null, timeout, ct);
        if (status != 200) throw new SyncException($"{path} → HTTP {status}", status);
        return JsonSerializer.Deserialize<T>(resp, SyncProtocol.Json) ?? throw new SyncException("empty response");
    }

    /// <summary>Signed request from a paired device.</summary>
    public Task<(int status, byte[] body, string? etag)> SendSignedAsync(HttpMethod method, string pathAndQuery, byte[] body,
        string myId, byte[] key, string targetId, string? ifNoneMatch, TimeSpan timeout, CancellationToken ct)
    {
        var headers = RequestAuth.Headers(myId, key, method.Method, pathAndQuery, body, targetId);
        if (ifNoneMatch != null) headers["If-None-Match"] = ifNoneMatch;
        return SendAsync(method, pathAndQuery, body, headers, timeout, ct);
    }

    public async Task<(int status, byte[] body, string? etag)> SendAsync(HttpMethod method, string pathAndQuery, byte[]? body,
        Dictionary<string, string>? headers, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(method, new Uri(_base, pathAndQuery))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (body is { Length: > 0 } || method == HttpMethod.Post)
        {
            req.Content = new ByteArrayContent(body ?? []);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        if (headers != null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var len = resp.Content.Headers.ContentLength;
            if (len > StateLimits.MaxStateBytes) throw new SyncException("response too large");
            var bytes = await ReadLimitedAsync(await resp.Content.ReadAsStreamAsync(cts.Token), StateLimits.MaxStateBytes, cts.Token);
            return ((int)resp.StatusCode, bytes, resp.Headers.ETag?.ToString());
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException)
        {
            throw new SyncException("TLS certificate does not match the paired device (pin mismatch)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("timeout");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream s, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new SyncException("response too large");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
