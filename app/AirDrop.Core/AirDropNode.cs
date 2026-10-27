using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using AirDrop.Core.Contacts;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;
using AirDrop.Core.Transfer;

namespace AirDrop.Core;

public sealed class AirDropNodeOptions
{
    public required AirDropIdentity Identity { get; init; }
    public required string ReceiveDirectory { get; init; }
    public NetworkBinding Binding { get; init; } = NetworkBinding.Any;
    /// <summary>Preferred HTTPS port; falls back to a free port if taken. 0 = always pick a free one.</summary>
    public int Port { get; init; } = AirDropConstants.DefaultPort;
    public int MdnsPort { get; init; } = MdnsService.DefaultPort;
    /// <summary>Receiving on (advertised + discoverable + Asks go to the handler) at start.</summary>
    public bool Receiving { get; init; } = true;
    /// <summary>Advertise "compatible formats" (no HEIF/HEVC) so iOS converts to JPEG/H.264.</summary>
    public bool CompatibleMediaFormats { get; init; } = true;
    /// <summary>OpenDrop's 0x88 plus SupportsUrl (0x01) so senders may share links with us.</summary>
    public AirDropReceiverFlags Flags { get; init; } = AirDropConstants.DefaultFlags | AirDropReceiverFlags.SupportsUrl;
    public X509Certificate2? Certificate { get; init; }
    /// <summary>A peer missing from mDNS for this long is reported lost (absorbs SRV refresh gaps).</summary>
    public TimeSpan PeerLostGrace { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>How often to retry /Discover on peers that answered "not discoverable" or failed.</summary>
    public TimeSpan RediscoverInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DiscoverTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public Action<string>? Log { get; init; }
}

/// <summary>A discovered and /Discover-ed peer.</summary>
public sealed record NodePeer(string Id, AirDropPeer Endpoint, DiscoverResult Discover, ValidationRecord? Record)
{
    public string Name => Discover.ReceiverComputerName ?? Id;
    public string? ModelName => Discover.ReceiverModelName;
    public bool IsAirDropWin => Discover.IsAirDropWin;
}

public sealed record NodeIncoming(AskRequest Ask, ValidationRecord? SenderRecord);

public sealed record NodeReceived(AskRequest Ask, IReadOnlyList<string> Paths, IReadOnlyList<string> Links, long TotalBytes);

public enum NodeSendResult { Delivered, Declined }

/// <summary>
/// One AirDrop endpoint: receiver (HTTPS server + mDNS advertisement) and sender (mDNS browser + /Discover
/// per peer + Ask/Upload), with peer found/lost tracking and smoothed progress. UI-agnostic; events fire on
/// thread-pool threads.
/// </summary>
public sealed class AirDropNode : IAsyncDisposable
{
    private readonly AirDropNodeOptions _o;
    private AirDropServer? _server;
    private AirDropBrowser? _browser;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly ConcurrentDictionary<string, Tracked> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ThroughputMeter _receiveMeter = new();
    private long _lastReceiveBytes;
    private volatile bool _receiving;
    private AirDropIdentity _identity;
    private bool _compatible;

    private sealed class Tracked
    {
        public required AirDropPeer Endpoint;
        public NodePeer? Peer;            // non-null once /Discover succeeded and it is discoverable (reported)
        public DateTime LastSeen;
        public DateTime NextDiscover;
        public bool Discovering;
    }

    public event Action<NodePeer>? PeerFound;
    public event Action<string>? PeerLost;
    public event Action<TransferStatus>? ReceiveProgress;
    public event Action<NodeReceived>? Received;

    /// <summary>Decides incoming Asks while receiving is on. Null = decline.</summary>
    public Func<NodeIncoming, CancellationToken, Task<bool>>? AskHandler { get; set; }

    public AirDropNode(AirDropNodeOptions options)
    {
        _o = options;
        _identity = options.Identity;
        _receiving = options.Receiving;
        _compatible = options.CompatibleMediaFormats;
    }

    public AirDropIdentity Identity => _identity;
    public bool Receiving => _receiving;
    public int Port => _server?.Port ?? 0;
    public IReadOnlyList<NodePeer> Peers => _peers.Values.Select(t => t.Peer).OfType<NodePeer>().ToList();

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_server != null) throw new InvalidOperationException("Already started");
        _server = NewServer(_o.Port);
        try { await _server.StartAsync(ct); }
        catch (SocketException) when (_o.Port != 0)
        {
            _o.Log?.Invoke($"Port {_o.Port} busy, picking a free port");
            await _server.DisposeAsync();
            _server = NewServer(0);
            await _server.StartAsync(ct);
        }
        _server.FilesReceived += OnFilesReceived;
        _server.ReceiveProgress += OnReceiveProgress;

        _browser = new AirDropBrowser(_server.Mdns!);
        _browser.Exclude(_identity.ServiceId);
        _server.Mdns!.CacheChanged += Wake;
        var token = _cts.Token;
        _loops.Add(Task.Run(() => _browser.BrowseAsync(_ => Wake(), token)));
        _loops.Add(Task.Run(() => TrackLoop(token)));
    }

    private AirDropServer NewServer(int port) => new(new AirDropServerOptions
    {
        ReceiveDirectory = _o.ReceiveDirectory,
        Identity = _identity,
        Binding = _o.Binding,
        Port = port,
        Flags = _o.Flags,
        Discoverable = _receiving,
        Advertise = _receiving,
        MediaCapabilitiesJson = MediaCapabilities.For(_compatible),
        AskHandler = HandleAskAsync,
        Certificate = _o.Certificate,
        EnableMdns = true,
        MdnsPort = _o.MdnsPort,
    }) { Log = _o.Log };

    /// <summary>Receiving on: advertise + discoverable + accept Asks. Off: mDNS goodbye, empty /Discover, Asks rejected.</summary>
    public async Task SetReceivingAsync(bool on)
    {
        _receiving = on;
        if (_server == null) return;
        _server.Discoverable = on;
        await _server.SetAdvertisingAsync(on, _cts.Token);
    }

    /// <summary>Changes name / receive folder / media capabilities without restarting.</summary>
    public void Update(string? computerName = null, string? receiveDirectory = null, bool? compatibleMediaFormats = null)
    {
        if (computerName != null && computerName != _identity.ComputerName)
        {
            _identity = new AirDropIdentity
            {
                ComputerName = computerName,
                ModelName = _identity.ModelName,
                ServiceId = _identity.ServiceId,
                HostName = _identity.HostName,
            };
        }
        if (compatibleMediaFormats is bool c) _compatible = c;
        if (_server == null) return;
        _server.Identity = _identity;
        if (receiveDirectory != null) _server.ReceiveDirectory = receiveDirectory;
        _server.MediaCapabilitiesJson = MediaCapabilities.For(_compatible);
    }

    private async Task<bool> HandleAskAsync(AskRequest ask, CancellationToken ct)
    {
        if (!_receiving || AskHandler is not { } handler) return false;
        var record = ValidationRecord.TryParse(ask.SenderRecordData);
        return await handler(new NodeIncoming(ask, record), ct);
    }

    private void OnReceiveProgress(TransferProgress p)
    {
        long done = p.BytesTransferred;
        if (done < Interlocked.Exchange(ref _lastReceiveBytes, done)) _receiveMeter.Reset(); // new upload
        if (_receiveMeter.Update(done, p.TotalBytes > 0 ? p.TotalBytes : 0) is { } s) ReceiveProgress?.Invoke(s);
    }

    private void OnFilesReceived(ReceivedFiles r)
    {
        if (r.Paths.Count > 0 || r.TotalBytes > 0)
            ReceiveProgress?.Invoke(new TransferStatus(r.TotalBytes, r.TotalBytes, _receiveMeter.BytesPerSecond));
        _receiveMeter.Reset();
        Interlocked.Exchange(ref _lastReceiveBytes, 0);
        Received?.Invoke(new NodeReceived(r.Ask, r.Paths, r.Ask.Items, r.TotalBytes));
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    // ---------------- peer tracking ----------------

    private async Task TrackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Track(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { _o.Log?.Invoke($"peer tracking: {e.Message}"); }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Track(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var current = _browser!.CurrentPeers();
        foreach (var p in current)
        {
            var t = _peers.GetOrAdd(p.InstanceName, _ => new Tracked { Endpoint = p, NextDiscover = now });
            lock (t)
            {
                t.LastSeen = now;
                t.Endpoint = p;
                if (t.Peer == null && !t.Discovering && now >= t.NextDiscover)
                {
                    t.Discovering = true;
                    _ = Task.Run(() => DiscoverPeerAsync(t, ct), ct);
                }
            }
        }
        // A goodbye removes the PTR immediately → lost now; a merely unresolved peer (SRV expired, re-query
        // pending) gets the grace period.
        var announced = new HashSet<string>(_server!.Mdns!.GetInstanceNames(AirDropConstants.ServiceType), StringComparer.OrdinalIgnoreCase);
        foreach (var (id, t) in _peers)
        {
            bool lost;
            lock (t) lost = !t.Discovering && (now - t.LastSeen > _o.PeerLostGrace || (t.LastSeen < now && !announced.Contains(id)));
            if (lost && _peers.TryRemove(id, out var removed) && removed.Peer != null)
            {
                _o.Log?.Invoke($"peer lost: {id}");
                PeerLost?.Invoke(id);
            }
        }
    }

    private async Task DiscoverPeerAsync(Tracked t, CancellationToken ct)
    {
        AirDropPeer ep;
        lock (t) ep = t.Endpoint;
        NodePeer? found = null;
        try
        {
            using var client = new AirDropClient(ep.EndPoint, _identity, _o.Binding, ep.HostName) { DiscoverTimeout = _o.DiscoverTimeout };
            var d = await client.DiscoverAsync(ct: ct);
            if (d.IsDiscoverable) found = new NodePeer(ep.InstanceName, ep, d, ValidationRecord.TryParse(d.ReceiverRecordData));
            else _o.Log?.Invoke($"peer {ep.InstanceName} not discoverable (receiving off / contacts only)");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _o.Log?.Invoke($"/Discover {ep.InstanceName} failed: {e.Message}");
        }
        catch (OperationCanceledException) { return; }

        bool report;
        lock (t)
        {
            t.Discovering = false;
            t.Peer = found;
            t.NextDiscover = DateTime.UtcNow + _o.RediscoverInterval;
            report = found != null && _peers.TryGetValue(ep.InstanceName, out var cur) && ReferenceEquals(cur, t);
        }
        if (report)
        {
            _o.Log?.Invoke($"peer found: {found!.Id} '{found.Name}' ({found.ModelName})");
            PeerFound?.Invoke(found!);
        }
    }

    // ---------------- sending ----------------

    private AirDropClient ClientFor(string peerId, out NodePeer peer)
    {
        if (!_peers.TryGetValue(peerId, out var t) || t.Peer is not { } p)
            throw new KeyNotFoundException($"Unknown peer '{peerId}'");
        peer = p;
        AirDropPeer ep;
        lock (t) ep = t.Endpoint;
        return new AirDropClient(ep.EndPoint, _identity, _o.Binding, ep.HostName);
    }

    /// <summary>Discover → Ask → Upload. Declined = the receiver said no. Throws on network/protocol failure.</summary>
    public async Task<NodeSendResult> SendFilesAsync(string peerId, IReadOnlyList<string> paths,
        IProgress<TransferStatus>? progress = null, CancellationToken ct = default)
    {
        foreach (var p in paths)
            if (!File.Exists(p) && !Directory.Exists(p)) throw new FileNotFoundException("Not found", p);
        using var client = ClientFor(peerId, out _);
        var d = await client.DiscoverAsync(ct: ct);
        if (!d.IsDiscoverable) throw new AirDropRequestException("Receiver is not discoverable", 200);
        // Sender-side media conversion: we cannot transcode HEIC/HEVC, so originals are sent even when the
        // receiver lacks HEIF support (MediaCapabilities.SupportsHeif(d.MediaCapabilitiesJson) == false).
        if (!await client.AskAsync(paths, ct: ct)) return NodeSendResult.Declined;

        long total = TotalSize(paths);
        var meter = new ThroughputMeter();
        progress?.Report(new TransferStatus(0, total, 0));
        long last = 0;
        var inner = new InlineProgress<TransferProgress>(tp =>
        {
            last = tp.BytesTransferred;
            if (meter.Update(tp.BytesTransferred, total) is { } s) progress?.Report(s);
        });
        await client.UploadAsync(paths, inner, ct);
        progress?.Report(new TransferStatus(Math.Max(last, total), total, meter.BytesPerSecond));
        return NodeSendResult.Delivered;
    }

    /// <summary>Link share: Ask with Items=[url], no Upload.</summary>
    public async Task<NodeSendResult> SendLinkAsync(string peerId, Uri link, CancellationToken ct = default)
    {
        using var client = ClientFor(peerId, out var peer);
        if (peer.Endpoint.Flags != AirDropReceiverFlags.None && !peer.Endpoint.Flags.HasFlag(AirDropReceiverFlags.SupportsUrl))
            throw new NotSupportedException("Receiver does not advertise URL support");
        var d = await client.DiscoverAsync(ct: ct);
        if (!d.IsDiscoverable) throw new AirDropRequestException("Receiver is not discoverable", 200);
        return await client.AskLinksAsync([link], ct) ? NodeSendResult.Delivered : NodeSendResult.Declined;
    }

    private static long TotalSize(IReadOnlyList<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p)) total += new FileInfo(p).Length;
            else if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) total += fi.Length;
                }
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_loops); } catch { /* shutting down */ }
        if (_server != null) await _server.DisposeAsync(); // sends mDNS goodbye
        _cts.Dispose();
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; would post to the captured context).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
