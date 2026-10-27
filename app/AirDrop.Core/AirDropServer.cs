using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AirDrop.Core.Archive;
using AirDrop.Core.Http;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;
using AirDrop.Core.Plist;
using AirDrop.Core.Security;

namespace AirDrop.Core;

public sealed class AirDropServerOptions
{
    public required string ReceiveDirectory { get; init; }
    public AirDropIdentity Identity { get; init; } = new();
    public NetworkBinding Binding { get; init; } = NetworkBinding.Any;
    /// <summary>0 = pick a free port.</summary>
    public int Port { get; init; } = AirDropConstants.DefaultPort;
    public AirDropReceiverFlags Flags { get; init; } = AirDropConstants.DefaultFlags;
    /// <summary>Whether /Discover reveals our name ("Everyone" mode). False hides us like "Receiving Off".</summary>
    public bool Discoverable { get; init; } = true;
    /// <summary>Publish the DNS-SD record at start (needs <see cref="EnableMdns"/>). Can be toggled later.</summary>
    public bool Advertise { get; init; } = true;
    /// <summary>JSON for ReceiverMediaCapabilities, see <see cref="AirDrop.Core.MediaCapabilities"/>.</summary>
    public string MediaCapabilitiesJson { get; init; } = MediaCapabilities.CompatibleJson;
    /// <summary>Our Apple-signed validation record (ReceiverRecordData); null = none (we have no Apple ID record).</summary>
    public byte[]? RecordData { get; init; }
    /// <summary>Decides on an incoming /Ask. Null = decline everything.</summary>
    public Func<AskRequest, CancellationToken, Task<bool>>? AskHandler { get; init; }
    /// <summary>How long an accepted Ask authorizes an /Upload from the same IP.</summary>
    public TimeSpan UploadWindow { get; init; } = TimeSpan.FromMinutes(5);
    public long? MaxUploadBytes { get; init; }
    public X509Certificate2? Certificate { get; init; }
    /// <summary>Publish via mDNS. Disable for direct host:port tests.</summary>
    public bool EnableMdns { get; init; } = true;
    public int MdnsPort { get; init; } = MdnsService.DefaultPort;
}

public sealed record ReceivedFiles(AskRequest Ask, IReadOnlyList<string> Paths, long TotalBytes);

/// <summary>AirDrop receiver: HTTPS /Discover, /Ask, /Upload plus DNS-SD announcement.</summary>
public sealed class AirDropServer : IAsyncDisposable
{
    private readonly AirDropServerOptions _o;
    private readonly ConcurrentDictionary<IPAddress, (AskRequest ask, DateTime expires)> _approved = new();
    private MiniHttpServer? _http;
    private MdnsService? _mdns;
    private ServiceRegistration? _registration;

    public event Action<ReceivedFiles>? FilesReceived;
    public event Action<TransferProgress>? ReceiveProgress;
    public Action<string>? Log { get; set; }
    public int Port => _http?.Port ?? 0;
    public MdnsService? Mdns => _mdns;

    // Runtime-changeable settings (initialised from the options).
    public AirDropIdentity Identity { get; set; }
    public string ReceiveDirectory { get; set; }
    public bool Discoverable { get; set; }
    public string MediaCapabilitiesJson { get; set; }
    public byte[]? RecordData { get; set; }
    public Func<AskRequest, CancellationToken, Task<bool>>? AskHandler { get; set; }
    public bool IsAdvertising => _registration != null;

    public AirDropServer(AirDropServerOptions options)
    {
        _o = options;
        Identity = options.Identity;
        ReceiveDirectory = options.ReceiveDirectory;
        Discoverable = options.Discoverable;
        MediaCapabilitiesJson = options.MediaCapabilitiesJson;
        RecordData = options.RecordData;
        AskHandler = options.AskHandler;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var cert = _o.Certificate ?? CertificateStore.LoadOrCreate(commonName: Identity.ComputerName);
        Directory.CreateDirectory(ReceiveDirectory);
        _http = new MiniHttpServer(cert, _o.Binding, HandleAsync) { Log = Log };
        _http.Start(_o.Port);
        Log?.Invoke($"HTTPS listening on port {_http.Port} ({_o.Binding})");

        if (_o.EnableMdns)
        {
            _mdns = new MdnsService(_o.Binding, _o.MdnsPort) { Log = Log };
            _mdns.Start();
            if (_o.Advertise) await SetAdvertisingAsync(true, ct);
        }
    }

    /// <summary>Publishes or withdraws (mDNS goodbye) our _airdrop._tcp record.</summary>
    public async Task SetAdvertisingAsync(bool advertise, CancellationToken ct = default)
    {
        if (_mdns == null || _http == null) return;
        if (advertise && _registration == null)
        {
            var reg = new ServiceRegistration
            {
                InstanceName = Identity.ServiceId,
                ServiceType = AirDropConstants.ServiceType,
                HostName = Identity.HostName,
                Port = (ushort)_http.Port,
                Txt = [$"flags={(int)_o.Flags}"],
            };
            _registration = reg;
            await _mdns.RegisterAsync(reg, ct);
            Log?.Invoke($"mDNS: published {reg.FullName} -> {reg.HostName}:{reg.Port}");
        }
        else if (!advertise && _registration is { } old)
        {
            _registration = null;
            await _mdns.UnregisterAsync(old);
            Log?.Invoke($"mDNS: withdrew {old.FullName}");
        }
    }

    private async Task<HttpResponse> HandleAsync(HttpRequest req, CancellationToken ct)
    {
        Log?.Invoke($"{req.RemoteEndPoint} {req.Method} {req.Path}");
        if (req.Method is "GET" or "HEAD") return HttpResponse.Empty(200);
        if (req.Method != "POST") return HttpResponse.Empty(400);
        return req.Path switch
        {
            "/Discover" => await HandleDiscover(req, ct),
            "/Ask" => await HandleAsk(req, ct),
            "/Upload" => await HandleUpload(req, ct),
            _ => HttpResponse.Empty(400),
        };
    }

    private static async Task<Dictionary<string, object>> ReadPlistBody(HttpRequest req, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await req.Body.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 4 * 1024 * 1024) throw new InvalidDataException("Plist body too large");
        }
        if (ms.Length == 0) return [];
        return BinaryPlist.ReadDictionary(ms.ToArray());
    }

    private static HttpResponse PlistResponse(Dictionary<string, object> d) => new() { Body = BinaryPlist.Write(d) };

    private async Task<HttpResponse> HandleDiscover(HttpRequest req, CancellationToken ct)
    {
        await ReadPlistBody(req, ct); // may carry SenderRecordData (contacts-only mode, unsupported)
        if (!Discoverable) return PlistResponse([]);
        var d = new Dictionary<string, object>
        {
            [AirDropConstants.ReceiverMediaCapabilities] = Encoding.UTF8.GetBytes(MediaCapabilitiesJson),
            [AirDropConstants.ReceiverComputerName] = Identity.ComputerName,
            [AirDropConstants.ReceiverModelName] = Identity.ModelName,
            [AirDropConstants.AirDropWinVersion] = AirDropConstants.AirDropWinVersionValue,
        };
        if (RecordData != null) d[AirDropConstants.ReceiverRecordData] = RecordData;
        return PlistResponse(d);
    }

    private async Task<HttpResponse> HandleAsk(HttpRequest req, CancellationToken ct)
    {
        var dict = await ReadPlistBody(req, ct);
        var ask = AskRequest.FromPlist(dict, req.RemoteEndPoint);
        Log?.Invoke($"Ask from {ask.SenderComputerName} ({ask.SenderModelName}): {ask.Files.Count} file(s) {string.Join(", ", ask.Files.Select(f => f.FileName))}" +
                    (ask.Items.Count > 0 ? $", {ask.Items.Count} item(s)" : ""));

        bool accepted = false;
        if (AskHandler is { } handler && (ask.Files.Count > 0 || ask.Items.Count > 0))
        {
            try { accepted = await handler(ask, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Log?.Invoke($"Ask handler failed: {e.Message}"); }
        }
        if (!accepted)
        {
            // UNVERIFIED: the exact status Apple returns on decline. OpenDrop never declines; PrivateDrop
            // (seemoo-lab, ServerRoutes.handleAsk) answers a declined Ask with 406 Not Acceptable, so do we.
            return HttpResponse.Empty(406);
        }
        if (ask.IsLinkOnly)
        {
            // Links travel inside the Ask (Items); no /Upload follows.
            // UNVERIFIED against a real iPhone/Mac sender.
            FilesReceived?.Invoke(new ReceivedFiles(ask, [], 0));
        }
        else _approved[req.RemoteEndPoint.Address] = (ask, DateTime.UtcNow + _o.UploadWindow);
        return PlistResponse(new Dictionary<string, object>
        {
            [AirDropConstants.ReceiverModelName] = Identity.ModelName,
            [AirDropConstants.ReceiverComputerName] = Identity.ComputerName,
            [AirDropConstants.AirDropWinVersion] = AirDropConstants.AirDropWinVersionValue,
        });
    }

    private async Task<HttpResponse> HandleUpload(HttpRequest req, CancellationToken ct)
    {
        if (!_approved.TryRemove(req.RemoteEndPoint.Address, out var approval) || approval.expires < DateTime.UtcNow)
        {
            Log?.Invoke($"Upload from {req.RemoteEndPoint} without an accepted Ask");
            return HttpResponse.Empty(403, close: true);
        }
        var ctype = req.Header("Content-Type") ?? "";
        if (!ctype.StartsWith(AirDropConstants.CpioContentType, StringComparison.OrdinalIgnoreCase))
        {
            // Mirrors OpenDrop (406 + Content-Type: application/x-cpio).
            var r = HttpResponse.Empty(406, close: true);
            r.Headers["Content-Type"] = AirDropConstants.CpioContentType;
            return r;
        }

        var receiveDir = ReceiveDirectory;
        Directory.CreateDirectory(receiveDir);
        var extractor = new SafeExtractor(receiveDir) { MaxTotalBytes = _o.MaxUploadBytes };
        long expected = approval.ask.Files.Count; // only for logging
        try
        {
            var body = await PeekStream.CreateAsync(req.Body, 2, ct);
            bool gzip = body.Prefix.Length >= 2 && body.Prefix[0] == 0x1F && body.Prefix[1] == 0x8B;
            // UNVERIFIED: whether iOS ever sends an uncompressed archive; we accept both.
            Stream archive = gzip ? new GZipStream(body, CompressionMode.Decompress) : body;
            long total = 0;
            await extractor.ExtractAsync(archive, n =>
            {
                total += n;
                ReceiveProgress?.Invoke(new TransferProgress(total, -1, null));
            }, ct);
        }
        catch (Exception e) when (e is UnsafeArchiveException or InvalidDataException or IOException or DecoderFallbackException)
        {
            Log?.Invoke($"Upload rejected: {e.Message}");
            Cleanup(extractor.ExtractedPaths);
            return HttpResponse.Empty(e is UnsafeArchiveException ? 400 : 500, close: true);
        }

        Log?.Invoke($"Received {extractor.ExtractedPaths.Count}/{expected} item(s), {extractor.TotalBytes} bytes into {receiveDir}");
        FilesReceived?.Invoke(new ReceivedFiles(approval.ask, extractor.ExtractedPaths.ToList(), extractor.TotalBytes));
        return HttpResponse.Empty(200, close: true);
    }

    private static void Cleanup(IEnumerable<string> paths)
    {
        foreach (var p in paths.ToList())
        {
            try
            {
                if (File.Exists(p)) File.Delete(p);
                else if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mdns != null) await _mdns.DisposeAsync();
        if (_http != null) await _http.DisposeAsync();
    }
}

/// <summary>Stream that exposes its first few bytes without consuming them.</summary>
internal sealed class PeekStream : Stream
{
    private readonly Stream _inner;
    private int _prefixPos;
    public byte[] Prefix { get; }

    private PeekStream(Stream inner, byte[] prefix) { _inner = inner; Prefix = prefix; }

    public static async Task<PeekStream> CreateAsync(Stream inner, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int n = await inner.ReadAtLeastAsync(buf, count, throwOnEndOfStream: false, ct);
        return new PeekStream(inner, buf[..n]);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_prefixPos < Prefix.Length)
        {
            int n = Math.Min(buffer.Length, Prefix.Length - _prefixPos);
            Prefix.AsMemory(_prefixPos, n).CopyTo(buffer);
            _prefixPos += n;
            return n;
        }
        return await _inner.ReadAsync(buffer, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
