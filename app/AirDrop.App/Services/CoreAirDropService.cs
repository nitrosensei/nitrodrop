using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AirDrop.Core;
using AirDrop.Core.Contacts;
using AirDrop.Core.Net;
using AirDrop.Core.Transfer;
using CoreTransferStatus = AirDrop.Core.Transfer.TransferStatus;

namespace AirDrop.App.Services;

/// <summary>Knobs that are not part of the UI's <see cref="ServiceOptions"/>.</summary>
public sealed record CoreAirDropServiceSettings
{
    /// <summary>ReceiverModelName/SenderModelName we claim. UNVERIFIED: a Mac model makes iOS draw a laptop glyph.</summary>
    public string ModelName { get; init; } = AirDropIdentity.DefaultModelName;
    /// <summary>Interface to bind: "any" (LAN, default until the AWDL interface exists), "loopback", name or index.</summary>
    public string Interface { get; init; } = "any";
    public int Port { get; init; } = AirDropConstants.DefaultPort;
    public int MdnsPort { get; init; } = Core.Mdns.MdnsService.DefaultPort;
    public X509Certificate2? Certificate { get; init; }
    public TimeSpan PeerLostGrace { get; init; } = TimeSpan.FromSeconds(12);
    public Action<string>? Log { get; init; }
}

/// <summary>
/// <see cref="IAirDropService"/> on top of AirDrop.Core (<see cref="AirDropNode"/>). Until the AWDL driver
/// exists it runs over the normal network ("LAN mode"): two Windows PCs on the same network can AirDrop each
/// other; iPhones/Macs only see us once AWDL is up. Events are raised on thread-pool threads.
/// </summary>
public sealed class CoreAirDropService : IAirDropService
{
    private readonly CoreAirDropServiceSettings _settings;
    private AirDropNode? _node;
    private ReceiveMode _mode;

    public CoreAirDropService(CoreAirDropServiceSettings? settings = null) => _settings = settings ?? new();

    public bool IsHardwareReady => false;

    public string HardwareStatus =>
        "AWDL (Wi-Fi sürücüsü) henüz yok · yerel ağ modu: aynı ağdaki airdrop-win bilgisayarları birbirini görür";

    public event EventHandler<PeerInfo>? PeerFound;
    public event EventHandler<string>? PeerLost;
    public event EventHandler<TransferProgress>? ReceiveProgress;
    public event EventHandler<ReceivedBatch>? Received;

    public Func<IncomingRequest, Task<bool>>? AskHandler { get; set; }

    public ReceiveMode Mode => _mode;

    /// <summary>Port the HTTPS receiver actually listens on (0 before start).</summary>
    public int Port => _node?.Port ?? 0;

    public async Task StartAsync(ServiceOptions options, ReceiveMode mode, CancellationToken ct = default)
    {
        if (_node != null) throw new InvalidOperationException("Already started");
        _mode = mode;
        var node = new AirDropNode(new AirDropNodeOptions
        {
            Identity = new AirDropIdentity { ComputerName = options.DeviceName, ModelName = _settings.ModelName },
            ReceiveDirectory = options.ReceiveFolder,
            Binding = NetworkBinding.Resolve(_settings.Interface),
            Port = _settings.Port,
            MdnsPort = _settings.MdnsPort,
            Receiving = IsReceiving(mode),
            CompatibleMediaFormats = options.PreferCompatibleFormats,
            Certificate = _settings.Certificate,
            PeerLostGrace = _settings.PeerLostGrace,
            Log = _settings.Log,
        });
        node.PeerFound += p => PeerFound?.Invoke(this, ToPeerInfo(p));
        node.PeerLost += id => PeerLost?.Invoke(this, id);
        node.ReceiveProgress += s => ReceiveProgress?.Invoke(this, ToProgress(s));
        node.Received += OnReceived;
        node.AskHandler = OnAskAsync;
        _node = node;
        try { await node.StartAsync(ct); }
        catch
        {
            _node = null;
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>ContactsOnly needs an Apple-signed record we do not have, so it behaves like Off.</summary>
    private static bool IsReceiving(ReceiveMode mode) => mode == ReceiveMode.Everyone;

    public async Task SetModeAsync(ReceiveMode mode)
    {
        _mode = mode;
        if (_node != null) await _node.SetReceivingAsync(IsReceiving(mode));
    }

    public Task UpdateOptionsAsync(ServiceOptions options)
    {
        _node?.Update(options.DeviceName, options.ReceiveFolder, options.PreferCompatibleFormats);
        return Task.CompletedTask;
    }

    public async Task<SendOutcome> SendAsync(string peerId, IReadOnlyList<string> files,
        IProgress<TransferProgress> progress, CancellationToken ct = default)
    {
        var node = _node ?? throw new InvalidOperationException("Not started");
        try
        {
            var inner = new InlineProgress<CoreTransferStatus>(s => progress.Report(ToProgress(s)));
            var r = await node.SendFilesAsync(peerId, files, inner, ct);
            return r == NodeSendResult.Delivered ? SendOutcome.Delivered : SendOutcome.Declined;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SendOutcome.Cancelled; }
        catch (Exception e) when (IsTransferFailure(e))
        {
            _settings.Log?.Invoke($"send to {peerId} failed: {e.Message}");
            return SendOutcome.Failed;
        }
    }

    public async Task<SendOutcome> SendLinkAsync(string peerId, Uri link, CancellationToken ct = default)
    {
        var node = _node ?? throw new InvalidOperationException("Not started");
        try
        {
            var r = await node.SendLinkAsync(peerId, link, ct);
            return r == NodeSendResult.Delivered ? SendOutcome.Delivered : SendOutcome.Declined;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SendOutcome.Cancelled; }
        catch (Exception e) when (IsTransferFailure(e))
        {
            _settings.Log?.Invoke($"link to {peerId} failed: {e.Message}");
            return SendOutcome.Failed;
        }
    }

    private static bool IsTransferFailure(Exception e) =>
        e is AirDropRequestException or HttpRequestException or IOException or KeyNotFoundException
            or NotSupportedException or System.Net.Sockets.SocketException or OperationCanceledException
            or UnauthorizedAccessException or InvalidDataException;

    // ---------------- mapping ----------------

    private async Task<bool> OnAskAsync(NodeIncoming incoming, CancellationToken ct)
    {
        if (AskHandler is not { } handler) return false;
        var ask = incoming.Ask;
        var kind = KindOf(ask.SenderModelName, ask.IsAirDropWin || IsKnownAirDropWin(ask.SenderId));
        var req = new IncomingRequest(
            SenderName: ask.SenderComputerName ?? "Bilinmeyen cihaz",
            SenderKind: kind,
            SenderModelName: ask.SenderModelName,
            Files: ask.Files.Select(f => new IncomingFile(f.FileName, 0, f.FileType)).ToList(), // /Ask carries no sizes
            Links: ToUris(ask.Items),
            Thumbnail: ask.FileIcon,
            SenderContactHashes: incoming.SenderRecord?.AllHashes ?? []);
        return await handler(req).WaitAsync(ct);
    }

    private void OnReceived(NodeReceived r)
    {
        var name = r.Ask.SenderComputerName ?? "Bilinmeyen cihaz";
        var kind = KindOf(r.Ask.SenderModelName, r.Ask.IsAirDropWin || IsKnownAirDropWin(r.Ask.SenderId));
        var key = $"{name}|{r.Ask.SenderModelName ?? kind.ToString()}";
        Received?.Invoke(this, new ReceivedBatch(name, key, r.Paths, ToUris(r.Links), r.TotalBytes));
    }

    private bool IsKnownAirDropWin(string? senderId) =>
        senderId != null && _node?.Peers.Any(p => p.IsAirDropWin && string.Equals(p.Id, senderId, StringComparison.OrdinalIgnoreCase)) == true;

    private static PeerInfo ToPeerInfo(NodePeer p) =>
        new(p.Id, p.Name, KindOf(p.ModelName, p.IsAirDropWin), p.ModelName, p.Record?.AllHashes ?? []);

    private static TransferProgress ToProgress(CoreTransferStatus s) => new(s.BytesDone, s.BytesTotal, s.BytesPerSecond);

    private static IReadOnlyList<Uri> ToUris(IEnumerable<string> items) =>
        items.Select(i => Uri.TryCreate(i, UriKind.Absolute, out var u) ? u : null).OfType<Uri>().ToList();

    /// <summary>Apple model id → device family. Our own peers are recognised by the airdrop-win marker.</summary>
    public static DeviceKind KindOf(string? model, bool isAirDropWin = false)
    {
        if (isAirDropWin) return DeviceKind.Windows;
        if (string.IsNullOrWhiteSpace(model)) return DeviceKind.Unknown;
        if (model.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) return DeviceKind.iPhone;
        if (model.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)) return DeviceKind.iPad;
        if (model.StartsWith("Mac", StringComparison.OrdinalIgnoreCase) || model.StartsWith("iMac", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Mac; // Mac14,2 · MacBookAir10,1 · MacBookPro18,3 · Macmini9,1 · MacPro7,1 · iMac21,1
        if (model.StartsWith("airdrop-win", StringComparison.OrdinalIgnoreCase) || model.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
            return DeviceKind.Windows;
        return DeviceKind.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        var node = _node;
        _node = null;
        if (node != null) await node.DisposeAsync();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
