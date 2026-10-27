using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AirDrop.Core.Archive;
using AirDrop.Core.Net;
using AirDrop.Core.Plist;

namespace AirDrop.Core;

public sealed class AirDropRequestException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>AirDrop sender: /Discover, /Ask, /Upload against one receiver over a single keep-alive connection.</summary>
public sealed class AirDropClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly AirDropIdentity _identity;

    public TimeSpan DiscoverTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>The receiver holds /Ask open while its user decides.</summary>
    public TimeSpan AskTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public Action<string>? Log { get; set; }

    public AirDropClient(IPEndPoint receiver, AirDropIdentity? identity = null, NetworkBinding? binding = null, string? hostName = null)
    {
        _identity = identity ?? new AirDropIdentity();
        binding ??= NetworkBinding.Any;
        var handler = new SocketsHttpHandler
        {
            // Always connect to the resolved endpoint on our interface; the URI host is cosmetic.
            ConnectCallback = async (_, ct) => new NetworkStream(await binding.ConnectAsync(receiver, ct), ownsSocket: true),
            SslOptions =
            {
                // Everyone mode: receivers use self-signed certs, like OpenDrop (ssl.CERT_NONE).
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
            MaxConnectionsPerServer = 1,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var host = hostName?.TrimEnd('.') ?? (receiver.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{new IPAddress(receiver.Address.GetAddressBytes())}]" : receiver.Address.ToString());
        _base = new Uri($"https://{host}:{receiver.Port}/");
    }

    private HttpRequestMessage NewRequest(string path, HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, path))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content,
        };
        req.Headers.Connection.Add("keep-alive");
        req.Headers.Accept.ParseAdd("*/*");
        req.Headers.TryAddWithoutValidation("User-Agent", AirDropConstants.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", AirDropConstants.AcceptLanguage);
        req.Headers.TryAddWithoutValidation("Accept-Encoding", AirDropConstants.AcceptEncoding);
        return req;
    }

    private async Task<(int status, byte[] body)> PostPlistAsync(string path, Dictionary<string, object> body, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var content = new ByteArrayContent(BinaryPlist.Write(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(AirDropConstants.PlistContentType);
        using var req = NewRequest(path, content);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        Log?.Invoke($"{path} -> {(int)resp.StatusCode}");
        return ((int)resp.StatusCode, bytes);
    }

    public async Task<DiscoverResult> DiscoverAsync(byte[]? senderRecordData = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if ((senderRecordData ?? SenderRecordData) is { } record) body[AirDropConstants.SenderRecordData] = record;
        var (status, bytes) = await PostPlistAsync("/Discover", body, DiscoverTimeout, ct);
        if (status != 200) throw new AirDropRequestException($"/Discover failed: HTTP {status}", status);
        var d = bytes.Length == 0 ? [] : BinaryPlist.ReadDictionary(bytes);
        string? caps = d.TryGetValue(AirDropConstants.ReceiverMediaCapabilities, out var mc) && mc is byte[] mb
            ? Encoding.UTF8.GetString(mb) : null;
        return new DiscoverResult(
            d.GetValueOrDefault(AirDropConstants.ReceiverComputerName) as string,
            d.GetValueOrDefault(AirDropConstants.ReceiverModelName) as string,
            caps, d);
    }

    /// <summary>
    /// Value of ConvertMediaFormats in our Asks. PrivateDrop documents it as "sender wants media formats to be
    /// converted"; we cannot transcode HEIC/HEVC on Windows without extra codecs, so we never ask for it and
    /// always send originals. UNVERIFIED: exact receiver behaviour when true.
    /// </summary>
    public bool ConvertMediaFormats { get; init; }

    /// <summary>Our validation record (SenderRecordData) for /Discover and /Ask; null = none.</summary>
    public byte[]? SenderRecordData { get; init; }

    /// <summary>
    /// /Ask for a link share: Items = [url], no Files, no Upload afterwards.
    /// UNVERIFIED: modelled on what macOS Safari sends (Items array of URL strings); OpenDrop cannot send links.
    /// </summary>
    public Dictionary<string, object> BuildLinkAsk(IReadOnlyList<Uri> links, byte[]? fileIcon = null)
    {
        var ask = new Dictionary<string, object>
        {
            [AirDropConstants.SenderComputerName] = _identity.ComputerName,
            [AirDropConstants.BundleID] = AirDropConstants.SenderBundleId,
            [AirDropConstants.SenderModelName] = _identity.ModelName,
            [AirDropConstants.SenderID] = _identity.ServiceId,
            [AirDropConstants.ConvertMediaFormats] = false,
            [AirDropConstants.Items] = links.Select(l => (object)l.AbsoluteUri).ToList(),
            [AirDropConstants.AirDropWinVersion] = AirDropConstants.AirDropWinVersionValue,
        };
        if (SenderRecordData != null) ask[AirDropConstants.SenderRecordData] = SenderRecordData;
        if (fileIcon != null) ask[AirDropConstants.FileIcon] = fileIcon;
        return ask;
    }

    /// <summary>Sends a link-only Ask. Returns true if the receiver accepted (the link is then delivered).</summary>
    public async Task<bool> AskLinksAsync(IReadOnlyList<Uri> links, CancellationToken ct = default)
    {
        var (status, _) = await PostPlistAsync("/Ask", BuildLinkAsk(links), AskTimeout, ct);
        return status == 200;
    }

    /// <summary>Builds the /Ask body exactly like OpenDrop's send_ask.</summary>
    public Dictionary<string, object> BuildAsk(IReadOnlyList<string> paths, byte[]? fileIcon = null)
    {
        var files = paths.Select(p =>
        {
            var name = EntryName(p);
            return (object)new Dictionary<string, object>
            {
                [AirDropConstants.FileName] = name,
                [AirDropConstants.FileType] = UtiTypes.FromPath(p),
                [AirDropConstants.FileBomPath] = "./" + name,
                [AirDropConstants.FileIsDirectory] = Directory.Exists(p),
                [AirDropConstants.ConvertMediaFormats] = ConvertMediaFormats ? 1L : 0L,
            };
        }).ToList();
        var ask = new Dictionary<string, object>
        {
            [AirDropConstants.SenderComputerName] = _identity.ComputerName,
            [AirDropConstants.BundleID] = AirDropConstants.SenderBundleId,
            [AirDropConstants.SenderModelName] = _identity.ModelName,
            [AirDropConstants.SenderID] = _identity.ServiceId,
            [AirDropConstants.ConvertMediaFormats] = ConvertMediaFormats,
            [AirDropConstants.Files] = files,
            [AirDropConstants.AirDropWinVersion] = AirDropConstants.AirDropWinVersionValue,
        };
        if (SenderRecordData != null) ask[AirDropConstants.SenderRecordData] = SenderRecordData;
        // TODO: generate a JPEG2000 thumbnail like OpenDrop does for images (needs an encoder).
        if (fileIcon != null) ask[AirDropConstants.FileIcon] = fileIcon;
        return ask;
    }

    /// <summary>Returns true if the receiver accepted.</summary>
    public async Task<bool> AskAsync(IReadOnlyList<string> paths, byte[]? fileIcon = null, CancellationToken ct = default)
    {
        var (status, _) = await PostPlistAsync("/Ask", BuildAsk(paths, fileIcon), AskTimeout, ct);
        return status == 200;
    }

    /// <summary>Streams files as gzip'd odc cpio with chunked transfer encoding.</summary>
    public async Task UploadAsync(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        using var req = NewRequest("/Upload", new CpioGzipContent(paths, progress));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        Log?.Invoke($"/Upload -> {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode)
            throw new AirDropRequestException($"/Upload failed: HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
    }

    /// <summary>Discover → Ask → Upload. Returns false if the receiver declined.</summary>
    public async Task<bool> SendAsync(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var p in paths)
            if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("Not found", p);
        var discover = await DiscoverAsync(ct: ct);
        if (!discover.IsDiscoverable) throw new AirDropRequestException("Receiver is not discoverable", 200);
        Log?.Invoke($"Receiver: {discover.ReceiverComputerName} ({discover.ReceiverModelName})");
        if (!await AskAsync(paths, ct: ct)) return false;
        await UploadAsync(paths, progress, ct);
        return true;
    }

    internal static string EntryName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));

    public void Dispose() => _http.Dispose();

    /// <summary>HttpContent that writes gzip(cpio(files)) on the fly; unknown length → chunked.</summary>
    private sealed class CpioGzipContent : HttpContent
    {
        private readonly IReadOnlyList<string> _paths;
        private readonly IProgress<TransferProgress>? _progress;

        public CpioGzipContent(IReadOnlyList<string> paths, IProgress<TransferProgress>? progress)
        {
            _paths = paths;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue(AirDropConstants.CpioContentType);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var entries = new List<(string full, string name, bool dir)>();
            foreach (var p in _paths)
            {
                var full = Path.GetFullPath(p);
                var top = EntryName(full);
                if (Directory.Exists(full))
                {
                    entries.Add((full, top, true));
                    foreach (var e in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    {
                        var info = new FileInfo(e);
                        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // never follow links
                        var rel = Path.GetRelativePath(full, e).Replace(Path.DirectorySeparatorChar, '/');
                        entries.Add((e, top + "/" + rel, Directory.Exists(e)));
                    }
                }
                else entries.Add((full, top, false));
            }
            long total = entries.Where(e => !e.dir).Sum(e => new FileInfo(e.full).Length);
            long sent = 0;

            await using var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true);
            await using var cpio = new CpioWriter(gzip, leaveOpen: true);
            foreach (var (full, name, dir) in entries)
            {
                var entryName = "./" + name; // libarchive/OpenDrop store "./<name>"
                if (dir)
                {
                    await cpio.AddDirectoryAsync(entryName, File.GetLastWriteTimeUtc(full), ct);
                    continue;
                }
                await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await cpio.AddFileAsync(entryName, fs, fs.Length, File.GetLastWriteTimeUtc(full), n =>
                {
                    sent += n;
                    _progress?.Report(new TransferProgress(sent, total, name));
                }, ct);
            }
            await cpio.FinishAsync(ct);
        }
    }
}
