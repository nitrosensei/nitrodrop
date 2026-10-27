using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AirDrop.Core.Net;

namespace AirDrop.Core.Http;

public sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required Stream Body { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

public sealed class HttpResponse
{
    public int Status { get; init; } = 200;
    public byte[] Body { get; init; } = [];
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Close { get; init; }

    public static HttpResponse Empty(int status, bool close = false) => new() { Status = status, Close = close };
}

/// <summary>
/// Tiny HTTP/1.1-over-TLS server: keep-alive, Content-Length and chunked request bodies,
/// lazy "100 Continue". Enough for the three AirDrop endpoints, nothing more.
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxConnections = 16;
    private readonly X509Certificate2 _certificate;
    private readonly Func<HttpRequest, CancellationToken, Task<HttpResponse>> _handler;
    private readonly NetworkBinding _binding;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _connectionSlots = new(MaxConnections);
    private Socket? _listener;
    private Task? _acceptLoop;

    public Action<string>? Log { get; set; }
    public int Port { get; private set; }

    public MiniHttpServer(X509Certificate2 certificate, NetworkBinding binding,
        Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler)
    {
        _certificate = certificate;
        _binding = binding;
        _handler = handler;
    }

    public void Start(int port)
    {
        var ep = _binding.ListenEndPoint(port);
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (ep.AddressFamily == AddressFamily.InterNetworkV6) s.DualMode = true;
        s.ExclusiveAddressUse = true;
        s.Bind(ep);
        s.Listen(32);
        _listener = s;
        Port = ((IPEndPoint)s.LocalEndPoint!).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) { Log?.Invoke($"accept failed: {e.Message}"); continue; }

            if (!await _connectionSlots.WaitAsync(0))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleConnection(client); }
                catch (Exception e) when (e is IOException or AuthenticationException or SocketException or OperationCanceledException or InvalidDataException)
                {
                    Log?.Invoke($"connection error: {e.Message}");
                }
                catch (Exception e) { Log?.Invoke($"connection crashed: {e}"); }
                finally
                {
                    client.Dispose();
                    _connectionSlots.Release();
                }
            });
        }
    }

    private async Task HandleConnection(Socket socket)
    {
        socket.NoDelay = true;
        var remote = (IPEndPoint)socket.RemoteEndPoint!;
        if (remote.Address.IsIPv4MappedToIPv6) remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
        await using var net = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(net, leaveInnerStreamOpen: false);
        using (var hs = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
        {
            hs.CancelAfter(TimeSpan.FromSeconds(15));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None, // OS default (TLS 1.2/1.3)
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, hs.Token);
        }

        var input = new BufferedReader(tls);
        while (!_cts.IsCancellationRequested)
        {
            string? requestLine;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                try { requestLine = await input.ReadLineAsync(MaxHeaderBytes, idle.Token); }
                catch (OperationCanceledException) { return; }
            }
            if (requestLine == null) return;
            if (requestLine.Length == 0) continue;

            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                await WriteResponse(tls, HttpResponse.Empty(400, close: true));
                return;
            }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int headerBytes = 0;
            while (true)
            {
                var line = await input.ReadLineAsync(MaxHeaderBytes, _cts.Token) ?? throw new IOException("Connection closed in headers");
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > MaxHeaderBytes) throw new InvalidDataException("Headers too large");
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                headers[key] = headers.TryGetValue(key, out var prev) ? prev + ", " + value : value;
            }

            bool expectContinue = headers.TryGetValue("Expect", out var exp) && exp.Equals("100-continue", StringComparison.OrdinalIgnoreCase);
            bool chunked = headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            long contentLength = 0;
            if (!chunked && headers.TryGetValue("Content-Length", out var cl) && (!long.TryParse(cl, out contentLength) || contentLength < 0))
            {
                await WriteResponse(tls, HttpResponse.Empty(400, close: true));
                return;
            }

            Stream body = chunked ? new ChunkedReadStream(input) : new LengthReadStream(input, contentLength);
            var tracked = new ContinueStream(body, expectContinue ? tls : null);
            var request = new HttpRequest
            {
                Method = parts[0], Path = parts[1], Headers = headers, Body = tracked, RemoteEndPoint = remote,
            };

            HttpResponse response;
            try { response = await _handler(request, _cts.Token); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log?.Invoke($"handler error on {request.Path}: {e.Message}");
                response = HttpResponse.Empty(500, close: true);
            }

            bool close = response.Close
                         || (headers.TryGetValue("Connection", out var conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase))
                         || parts[2] == "HTTP/1.0";
            // Unread body: drain small ones to keep the connection usable, otherwise close.
            if (!close && !tracked.Completed)
            {
                if (expectContinue && !tracked.Started) close = true;
                else if (!await tracked.TryDrainAsync(64 * 1024, _cts.Token)) close = true;
            }
            await WriteResponse(tls, response, close);
            if (close)
            {
                // Lingering close: swallow a bit of the unread body so the client sees our
                // response instead of a connection reset.
                if (!tracked.Completed && (!expectContinue || tracked.Started))
                {
                    using var linger = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    linger.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await tracked.TryDrainAsync(16 * 1024 * 1024, linger.Token); }
                    catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException) { }
                }
                return;
            }
        }
    }

    private static async Task WriteResponse(Stream s, HttpResponse r, bool? close = null)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(r.Status).Append(' ').Append(Reason(r.Status)).Append("\r\n");
        foreach (var (k, v) in r.Headers)
            if (!k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && !k.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Content-Length: ").Append(r.Body.Length).Append("\r\n");
        if (close ?? r.Close) sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
        if (r.Body.Length > 0) await s.WriteAsync(r.Body);
        await s.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        100 => "Continue", 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
        406 => "Not Acceptable", 413 => "Payload Too Large", 415 => "Unsupported Media Type",
        500 => "Internal Server Error", _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Dispose();
        if (_acceptLoop != null) await _acceptLoop;
        _cts.Dispose();
    }

    // ---------------- streams ----------------

    /// <summary>Buffered reader that can hand out lines and raw bytes from the same stream.</summary>
    internal sealed class BufferedReader(Stream inner)
    {
        private readonly byte[] _buf = new byte[16384];
        private int _pos, _len;

        public async Task<string?> ReadLineAsync(int maxLength, CancellationToken ct)
        {
            var line = new List<byte>(128);
            while (true)
            {
                if (_pos == _len && !await FillAsync(ct)) return line.Count == 0 ? null : throw new IOException("Unexpected EOF in line");
                byte b = _buf[_pos++];
                if (b == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                    return Encoding.Latin1.GetString(line.ToArray());
                }
                line.Add(b);
                if (line.Count > maxLength) throw new InvalidDataException("Line too long");
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct)
        {
            if (_pos == _len)
            {
                if (dest.Length >= _buf.Length) return await inner.ReadAsync(dest, ct);
                if (!await FillAsync(ct)) return 0;
            }
            int n = Math.Min(dest.Length, _len - _pos);
            _buf.AsMemory(_pos, n).CopyTo(dest);
            _pos += n;
            return n;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _pos = 0;
            _len = await inner.ReadAsync(_buf, ct);
            return _len > 0;
        }
    }

    internal abstract class BodyStream : Stream
    {
        public abstract bool Completed { get; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    internal sealed class LengthReadStream(BufferedReader input, long length) : BodyStream
    {
        private long _remaining = length;
        public override bool Completed => _remaining == 0;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining == 0) return 0;
            int n = await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
            if (n == 0) throw new IOException("Connection closed before body end");
            _remaining -= n;
            return n;
        }
    }

    internal sealed class ChunkedReadStream(BufferedReader input) : BodyStream
    {
        private long _chunkRemaining;
        private bool _done;
        public override bool Completed => _done;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_done) return 0;
            if (_chunkRemaining == 0)
            {
                var line = await input.ReadLineAsync(1024, ct) ?? throw new IOException("EOF in chunk header");
                int semi = line.IndexOf(';');
                var hex = (semi >= 0 ? line[..semi] : line).Trim();
                if (!long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out _chunkRemaining) || _chunkRemaining < 0)
                    throw new InvalidDataException("Bad chunk size");
                if (_chunkRemaining == 0)
                {
                    // Trailers until blank line.
                    while (!string.IsNullOrEmpty(await input.ReadLineAsync(8192, ct))) { }
                    _done = true;
                    return 0;
                }
            }
            int n = await input.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _chunkRemaining)], ct);
            if (n == 0) throw new IOException("EOF in chunk data");
            _chunkRemaining -= n;
            if (_chunkRemaining == 0 && await input.ReadLineAsync(16, ct) is not "")
                throw new InvalidDataException("Missing CRLF after chunk");
            return n;
        }
    }

    /// <summary>Sends "100 Continue" on first read when the client asked for it.</summary>
    internal sealed class ContinueStream(Stream body, Stream? continueTarget) : BodyStream
    {
        private Stream? _continueTarget = continueTarget;
        public bool Started { get; private set; }
        public override bool Completed => body is BodyStream b && b.Completed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started = true;
            if (_continueTarget != null)
            {
                await _continueTarget.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct);
                await _continueTarget.FlushAsync(ct);
                _continueTarget = null;
            }
            return await body.ReadAsync(buffer, ct);
        }

        public async Task<bool> TryDrainAsync(long max, CancellationToken ct)
        {
            var buf = new byte[8192];
            long total = 0;
            int n;
            while ((n = await ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > max) return false;
            }
            return true;
        }
    }
}
