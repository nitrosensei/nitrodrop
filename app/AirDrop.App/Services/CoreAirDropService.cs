using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AirDrop.App.Localization;
using AirDrop.Core;
using AirDrop.Core.Contacts;
using AirDrop.Core.Net;
using AirDrop.Core.Transfer;
using Nitro.Awdl.Ble;
using Nitro.Awdl.Ipc;
using CoreTransferStatus = AirDrop.Core.Transfer.TransferStatus;

namespace AirDrop.App.Services;

/// <summary>Knobs that are not part of the UI's <see cref="ServiceOptions"/>.</summary>
public sealed record CoreAirDropServiceSettings
{
    /// <summary>ReceiverModelName/SenderModelName we claim. UNVERIFIED: a Mac model makes iOS draw a laptop glyph.</summary>
    public string ModelName { get; init; } = AirDropIdentity.DefaultModelName;
    /// <summary>Real hardware sent to NitroDrop peers (NitroDropDevice marker); default = this PC, null = not sent.</summary>
    public AirDrop.Core.Devices.HostDevice? Host { get; init; } = AirDrop.Core.Devices.HostDevice.Local;
    /// <summary>Interface of the LAN node: "any" (default), "loopback", name or index. The AWDL node binds separately.</summary>
    public string Interface { get; init; } = "any";
    public int Port { get; init; } = AirDropConstants.DefaultPort;
    public int MdnsPort { get; init; } = Core.Mdns.MdnsService.DefaultPort;
    public X509Certificate2? Certificate { get; init; }
    public TimeSpan PeerLostGrace { get; init; } = TimeSpan.FromSeconds(12);
    public Action<string>? Log { get; init; }
    /// <summary>iPhone link over our Wi-Fi driver (elevated radio host + Wintun). Null = LAN only.</summary>
    public RadioIntegration? Radio { get; init; }
}

/// <summary>How <see cref="CoreAirDropService"/> reaches the radio host (all seams so the decisions are testable).</summary>
public sealed record RadioIntegration
{
    /// <summary>Starts NitroDrop.Radio.exe elevated and connects its pipe.</summary>
    public required IRadioHostLauncher Launcher { get; init; }
    /// <summary>Our driver is bound to the Wi-Fi card (DriverSetupStatus / SystemProbe: service airdropwifi).</summary>
    public required Func<bool> DriverInstalled { get; init; }
    /// <summary>Ayarlar → Wi-Fi Sürücüsü → "iPhone'lara doğrudan bağlan".</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The BLE wake-up advert (null = none, e.g. no Bluetooth).</summary>
    public Func<IAirDropBleAdvertiser?>? CreateBle { get; init; }
    /// <summary>Interface name → binding (tests substitute loopback for the Wintun adapter).</summary>
    public Func<string, NetworkBinding> ResolveBinding { get; init; } = NetworkBinding.Resolve;
    /// <summary>From the start command until the host reports Ready or Failed.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long the Wintun interface may take to show up in Windows' interface list after Ready.</summary>
    public TimeSpan InterfaceTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>wintun.dll is downloaded on first use, after the user agrees (null = assume present, e.g. tests).</summary>
    public WintunProvisioning? Wintun { get; init; }
}

/// <summary>
/// The on-demand Wintun download (Nitro.Awdl.Ip.WintunInstall): wintun.dll is not part of NitroDrop's packages (its
/// licence is not OSI), so the first radio start asks "Wi-Fi bileşeni indirilecek (Wintun, WireGuard LLC)" and only
/// then downloads and verifies it.
/// </summary>
public sealed record WintunProvisioning
{
    /// <summary>A verified copy is already on disk.</summary>
    public required Func<bool> IsInstalled { get; init; }
    /// <summary>Shows the consent prompt; true = download.</summary>
    public required Func<CancellationToken, Task<bool>> Confirm { get; init; }
    /// <summary>Downloads and verifies (throws on any failed check).</summary>
    public required Func<CancellationToken, Task> Install { get; init; }
}

/// <summary>Where the iPhone link stands (drives <see cref="CoreAirDropService.HardwareStatus"/>).</summary>
public enum RadioPhase
{
    /// <summary>No radio integration configured (tests, CLI): LAN only.</summary>
    None,
    Disabled,
    NoDriver,
    /// <summary>Waiting for the user to allow the Wintun download.</summary>
    WintunConsent,
    /// <summary>The user did not allow the Wintun download: LAN only.</summary>
    WintunDeclined,
    WintunDownloading,
    /// <summary>Download or verification failed: LAN only.</summary>
    WintunFailed,
    /// <summary>UAC prompt is up / connecting to the host.</summary>
    Launching,
    Cancelled,
    LaunchFailed,
    Starting,
    Ready,
    Failed,
    /// <summary>The host reported Ready but the interface could not be bound.</summary>
    BindFailed,
    HostExited,
}

/// <summary>
/// <see cref="IAirDropService"/> on top of AirDrop.Core. Always runs a LAN node (PC↔PC on the normal network). When the
/// Wi-Fi driver is installed it also starts the elevated radio host (NitroDrop.Radio.exe, one UAC prompt), waits for
/// the "NitroDrop AWDL" Wintun interface and runs a second node bound to it, so iPhones/iPads/Macs see us over AWDL.
/// Both nodes share one identity. Events are raised on thread-pool threads.
/// </summary>
public sealed class CoreAirDropService : IAirDropService
{
    private readonly CoreAirDropServiceSettings _settings;
    private readonly ConcurrentDictionary<string, AirDropNode> _peerOwner = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _radioGate = new(1, 1);
    private readonly object _bleLock = new();
    private AirDropNode? _lan, _awdl;
    private AirDropIdentity? _identity;
    private ServiceOptions? _options;
    private ReceiveMode _mode;

    private bool _radioEnabled;
    private volatile RadioPhase _phase;
    private RadioStatus? _radio;
    private string? _error;
    private IRadioHostConnection? _conn;
    private CancellationTokenSource? _radioCts;
    private Task _radioTask = Task.CompletedTask;
    private Task _statusChain = Task.CompletedTask;
    private TaskCompletionSource? _outcome;
    private IAirDropBleAdvertiser? _ble;
    private bool _bleOn;
    private int _activeSends;

    public CoreAirDropService(CoreAirDropServiceSettings? settings = null)
    {
        _settings = settings ?? new();
        _radioEnabled = _settings.Radio?.Enabled ?? false;
    }

    // ---------------- hardware status ----------------

    public RadioPhase Phase => _phase;
    public bool IsHardwareReady => _phase == RadioPhase.Ready;
    /// <summary>Last status from the radio host.</summary>
    public RadioStatus? Radio => _radio;
    /// <summary>Completes when the current radio bring-up has reached a decision (ready, failed, LAN fallback).</summary>
    public Task RadioStartup => _radioTask;
    /// <summary>The bound AWDL interface's binding, while Ready.</summary>
    public NetworkBinding? AwdlBinding { get; private set; }
    public bool BleAdvertising { get { lock (_bleLock) return _bleOn; } }

    /// <summary>Status without our Wi-Fi driver (LAN only), in the UI language.</summary>
    public static string LanOnlyText => MainStrings.Status_LanOnly;

    public string HardwareStatus => _phase switch
    {
        RadioPhase.Disabled => MainStrings.Status_Disabled,
        RadioPhase.WintunConsent => MainStrings.Status_WintunConsent,
        RadioPhase.WintunDeclined => MainStrings.Status_WintunDeclined,
        RadioPhase.WintunDownloading => MainStrings.Status_WintunDownloading,
        RadioPhase.WintunFailed => MainStrings.Status_WintunFailed(_error),
        RadioPhase.Launching => MainStrings.Status_Launching,
        RadioPhase.Cancelled => MainStrings.Status_Cancelled,
        RadioPhase.LaunchFailed => MainStrings.Status_LaunchFailed(_error),
        RadioPhase.Starting => MainStrings.Status_Starting,
        RadioPhase.Ready => MainStrings.Status_Ready_Plural(_radio?.Peers.Count ?? 0, _radio?.Channel ?? 6),
        RadioPhase.Failed => MainStrings.Status_Failed(DescribeFailure(_radio?.FailureKind ?? RadioFailureKind.Other)),
        RadioPhase.BindFailed => MainStrings.Status_BindFailed,
        RadioPhase.HostExited => MainStrings.Status_HostExited,
        _ => LanOnlyText,
    };

    /// <summary>Short, in the UI language (it sits in the status chip); the technical detail goes to the log.</summary>
    public static string DescribeFailure(RadioFailureKind kind) => kind switch
    {
        RadioFailureKind.DriverMissing => MainStrings.Failure_DriverMissing,
        RadioFailureKind.AccessDenied => MainStrings.Failure_AccessDenied,
        RadioFailureKind.WintunMissing => MainStrings.Failure_WintunMissing,
        RadioFailureKind.RfKill => MainStrings.Failure_RfKill,
        RadioFailureKind.Radio => MainStrings.Failure_Radio,
        RadioFailureKind.Network => MainStrings.Failure_Network,
        _ => MainStrings.Failure_Other,
    };

    public event EventHandler? HardwareStatusChanged;
    public event EventHandler<PeerInfo>? PeerFound;
    public event EventHandler<string>? PeerLost;
    public event EventHandler<TransferProgress>? ReceiveProgress;
    public event EventHandler<ReceivedBatch>? Received;

    public Func<IncomingRequest, Task<bool>>? AskHandler { get; set; }

    public ReceiveMode Mode => _mode;

    /// <summary>Port the LAN HTTPS receiver actually listens on (0 before start).</summary>
    public int Port => _lan?.Port ?? 0;

    // ---------------- lifecycle ----------------

    public async Task StartAsync(ServiceOptions options, ReceiveMode mode, CancellationToken ct = default)
    {
        if (_lan != null) throw new InvalidOperationException("Already started");
        _mode = mode;
        _options = options;
        _identity = new AirDropIdentity { ComputerName = options.DeviceName, ModelName = _settings.ModelName, Host = _settings.Host };
        // With the radio in play, the LAN node leaves the AWDL adapter to the AWDL node (one advert per network).
        var lanBinding = _settings.Radio != null && IsAny(_settings.Interface)
            ? NetworkBinding.AnyExcept(RadioPipe.InterfaceName)
            : NetworkBinding.Resolve(_settings.Interface);
        var node = NewNode(lanBinding, _settings.Port);
        _lan = node;
        try { await node.StartAsync(ct); }
        catch
        {
            _lan = null;
            await node.DisposeAsync();
            throw;
        }
        if (_settings.Radio != null) StartRadio();
    }

    private static bool IsAny(string? s) => string.IsNullOrWhiteSpace(s) || s.Equals("any", StringComparison.OrdinalIgnoreCase);

    private AirDropNode NewNode(NetworkBinding binding, int port)
    {
        var o = _options!;
        var node = new AirDropNode(new AirDropNodeOptions
        {
            Identity = _identity!,
            ReceiveDirectory = o.ReceiveFolder,
            Binding = binding,
            Port = port,
            MdnsPort = _settings.MdnsPort,
            Receiving = IsReceiving(_mode),
            CompatibleMediaFormats = o.PreferCompatibleFormats,
            Certificate = _settings.Certificate,
            PeerLostGrace = _settings.PeerLostGrace,
            Log = _settings.Log,
        });
        node.PeerFound += p =>
        {
            _peerOwner[p.Id] = node;
            PeerFound?.Invoke(this, ToPeerInfo(p));
        };
        node.PeerLost += id =>
        {
            if (_peerOwner.TryRemove(new KeyValuePair<string, AirDropNode>(id, node))) PeerLost?.Invoke(this, id);
        };
        node.ReceiveProgress += s => ReceiveProgress?.Invoke(this, ToProgress(s));
        node.Received += OnReceived;
        node.AskHandler = OnAskAsync;
        return node;
    }

    /// <summary>
    /// Ayarlar toggle: starts or stops the iPhone link without restarting the LAN side. Only a change acts (so other
    /// settings being saved never re-prompt UAC); after a declined prompt, switching it off and on again retries.
    /// </summary>
    public async Task SetRadioEnabledAsync(bool enabled)
    {
        if (_settings.Radio == null || enabled == _radioEnabled) return;
        _radioEnabled = enabled;
        if (!enabled)
        {
            await StopRadioAsync();
            SetPhase(RadioPhase.Disabled);
        }
        else if (_lan != null && _phase is not (RadioPhase.Launching or RadioPhase.Starting or RadioPhase.Ready))
        {
            await StopRadioAsync();
            StartRadio();
        }
    }

    private void StartRadio()
    {
        var cts = _radioCts = new CancellationTokenSource();
        _radioTask = Task.Run(() => RunRadioAsync(cts.Token));
    }

    // ---------------- radio host ----------------

    /// <summary>Consent prompt + download when wintun.dll is missing. False = stay LAN only (phase already set).</summary>
    private async Task<bool> EnsureWintunAsync(WintunProvisioning w, CancellationToken ct)
    {
        try { if (w.IsInstalled()) return true; }
        catch (Exception e) { Log($"wintun check failed: {e.Message}"); }

        SetPhase(RadioPhase.WintunConsent);
        bool agreed;
        try { agreed = await w.Confirm(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception e) { Log($"wintun consent prompt failed: {e.Message}"); agreed = false; }
        if (ct.IsCancellationRequested) return false;
        if (!agreed)
        {
            Log("wintun: download not allowed, staying in LAN mode");
            SetPhase(RadioPhase.WintunDeclined);
            return false;
        }

        SetPhase(RadioPhase.WintunDownloading);
        try
        {
            await w.Install(ct);
            Log("wintun: downloaded and verified");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception e)
        {
            Log($"wintun download failed: {e}");
            _error = e.Message;
            SetPhase(RadioPhase.WintunFailed);
            return false;
        }
    }

    private async Task RunRadioAsync(CancellationToken ct)
    {
        var r = _settings.Radio!;
        if (!_radioEnabled) { SetPhase(RadioPhase.Disabled); return; }
        bool installed;
        try { installed = r.DriverInstalled(); }
        catch (Exception e) { Log($"driver check failed: {e.Message}"); installed = false; }
        if (!installed) { SetPhase(RadioPhase.NoDriver); return; }
        if (r.Wintun is { } w && !await EnsureWintunAsync(w, ct)) return;

        SetPhase(RadioPhase.Launching);
        IRadioHostConnection conn;
        try { conn = await r.Launcher.LaunchAsync(ct); }
        catch (RadioLaunchCancelledException)
        {
            Log("radio host: UAC prompt declined, staying in LAN mode");
            SetPhase(RadioPhase.Cancelled);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e)
        {
            Log($"radio host could not start: {e}");
            _error = e.Message;
            SetPhase(RadioPhase.LaunchFailed);
            return;
        }
        if (ct.IsCancellationRequested) { await conn.DisposeAsync(); return; }

        var outcome = _outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _conn = conn;
        conn.Log += line => Log("[radio] " + line);
        conn.StatusChanged += s => Enqueue(() => HandleStatusAsync(conn, s));
        conn.Disconnected += () => Enqueue(() => HandleDisconnectAsync(conn));
        SetPhase(RadioPhase.Starting);
        try { await conn.SendAsync(RadioMessage.Start(_options?.DeviceName), ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log($"radio host: start command failed: {e.Message}");
        }
        // A status sent before we subscribed (the host's first one comes right after "hello").
        if (conn.Status.State is RadioState.Ready or RadioState.Failed) Enqueue(() => HandleStatusAsync(conn, conn.Status));
        if (!conn.IsConnected) Enqueue(() => HandleDisconnectAsync(conn));

        var timeout = Task.Delay(r.ReadyTimeout, ct);
        try
        {
            if (await Task.WhenAny(outcome.Task, timeout) == timeout && !ct.IsCancellationRequested)
            {
                Log("radio host: no Ready/Failed in time");
                await _radioGate.WaitAsync(CancellationToken.None);
                try
                {
                    if (_conn == conn && _phase == RadioPhase.Starting)
                    {
                        _radio = RadioStatus.Failed(RadioFailureKind.Other, "timeout");
                        SetPhase(RadioPhase.Failed);
                    }
                }
                finally { _radioGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Status handling runs one at a time, in arrival order.</summary>
    private void Enqueue(Func<Task> work)
    {
        lock (_bleLock)
            _statusChain = _statusChain.ContinueWith(_ => work(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async Task HandleStatusAsync(IRadioHostConnection conn, RadioStatus s)
    {
        await _radioGate.WaitAsync();
        try
        {
            if (_conn != conn) return;
            _radio = s;
            switch (s.State)
            {
                case RadioState.Ready when _awdl == null:
                    try
                    {
                        await StartAwdlNodeAsync(s.Interface ?? RadioPipe.InterfaceName);
                        SetPhase(RadioPhase.Ready);
                    }
                    catch (Exception e)
                    {
                        Log($"AWDL interface could not be bound: {e}");
                        _error = e.Message;
                        SetPhase(RadioPhase.BindFailed);
                    }
                    _outcome?.TrySetResult();
                    break;
                case RadioState.Ready:
                    SetPhase(RadioPhase.Ready);   // peer count changed
                    break;
                case RadioState.Failed:
                    Log($"radio host failed: {s.FailureKind}: {s.Failure}");
                    await StopAwdlNodeAsync();
                    SetPhase(RadioPhase.Failed);
                    _outcome?.TrySetResult();
                    break;
                case RadioState.Stopped when _phase == RadioPhase.Ready:
                    await StopAwdlNodeAsync();
                    SetPhase(RadioPhase.HostExited);
                    break;
            }
        }
        finally { _radioGate.Release(); }
        UpdateBle();
    }

    private async Task HandleDisconnectAsync(IRadioHostConnection conn)
    {
        await _radioGate.WaitAsync();
        try
        {
            if (_conn != conn) return;   // we closed it ourselves (StopRadioAsync disposes it)
            _conn = null;
            Log("radio host disconnected");
            await StopAwdlNodeAsync();
            SetPhase(RadioPhase.HostExited);
            _outcome?.TrySetResult();
        }
        finally { _radioGate.Release(); }
        await conn.DisposeAsync();
        UpdateBle();
    }

    /// <summary>Binds a second node to the Wintun interface; it can take a moment to appear in Windows' list.</summary>
    private async Task StartAwdlNodeAsync(string interfaceName)
    {
        var r = _settings.Radio!;
        var until = DateTime.UtcNow + r.InterfaceTimeout;
        NetworkBinding binding;
        while (true)
        {
            try { binding = r.ResolveBinding(interfaceName); break; }
            catch (ArgumentException) when (DateTime.UtcNow < until) { await Task.Delay(250); }
        }
        var node = NewNode(binding, 0);   // the LAN node holds 8770 on [::]; any free port, advertised over mDNS
        await node.StartAsync();
        _awdl = node;
        AwdlBinding = binding;
        Log($"AWDL node on {binding.DisplayName}, port {node.Port}");
    }

    private async Task StopAwdlNodeAsync()
    {
        var node = _awdl;
        _awdl = null;
        AwdlBinding = null;
        if (node == null) return;
        foreach (var (id, owner) in _peerOwner.ToArray())
            if (owner == node && _peerOwner.TryRemove(new KeyValuePair<string, AirDropNode>(id, node)))
                PeerLost?.Invoke(this, id);
        await node.DisposeAsync();
    }

    /// <summary>Closes the pipe (the host stops the radio and exits) and drops the AWDL node.</summary>
    private async Task StopRadioAsync()
    {
        _radioCts?.Cancel();
        try { await _radioTask; } catch (Exception e) { Log($"radio task: {e.Message}"); }
        IRadioHostConnection? conn;
        await _radioGate.WaitAsync();
        try
        {
            conn = _conn;
            _conn = null;
            await StopAwdlNodeAsync();
        }
        finally { _radioGate.Release(); }
        if (conn != null)
        {
            try { await conn.SendAsync(RadioMessage.Quit()).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) { }
            await conn.DisposeAsync();
        }
        _radioCts?.Dispose();
        _radioCts = null;
        UpdateBle();
    }

    private void SetPhase(RadioPhase phase)
    {
        _phase = phase;
        HardwareStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- BLE wake-up advert ----------------

    /// <summary>
    /// The advert makes iPhones in "Everyone" mode wake their AWDL interface (docs/awdl-port.md, BLE): on while we are
    /// discoverable to everyone and while a send is in flight, only with the AWDL link up.
    /// </summary>
    private void UpdateBle()
    {
        bool want = _phase == RadioPhase.Ready && (_mode == ReceiveMode.Everyone || Volatile.Read(ref _activeSends) > 0);
        lock (_bleLock)
        {
            if (want == _bleOn) return;
            try
            {
                if (want)
                {
                    _ble ??= _settings.Radio?.CreateBle?.Invoke();
                    if (_ble == null) return;
                    _ble.Start();
                    _bleOn = true;
                }
                else
                {
                    _bleOn = false;
                    _ble?.Stop();
                }
            }
            catch (Exception e)   // Bluetooth off / no adapter: AWDL still works with phones already awake
            {
                Log($"BLE advert {(want ? "start" : "stop")} failed: {e.Message}");
            }
        }
    }

    // ---------------- receive mode / options ----------------

    /// <summary>ContactsOnly needs an Apple-signed record we do not have, so it behaves like Off.</summary>
    private static bool IsReceiving(ReceiveMode mode) => mode == ReceiveMode.Everyone;

    public async Task SetModeAsync(ReceiveMode mode)
    {
        _mode = mode;
        if (_lan != null) await _lan.SetReceivingAsync(IsReceiving(mode));
        if (_awdl is { } awdl) await awdl.SetReceivingAsync(IsReceiving(mode));
        UpdateBle();
    }

    public Task UpdateOptionsAsync(ServiceOptions options)
    {
        _options = options;
        _lan?.Update(options.DeviceName, options.ReceiveFolder, options.PreferCompatibleFormats);
        _awdl?.Update(options.DeviceName, options.ReceiveFolder, options.PreferCompatibleFormats);
        return Task.CompletedTask;
    }

    // ---------------- sending ----------------

    private AirDropNode NodeFor(string peerId) =>
        _peerOwner.TryGetValue(peerId, out var n) ? n : _lan ?? throw new InvalidOperationException("Not started");

    public async Task<SendOutcome> SendAsync(string peerId, IReadOnlyList<string> files,
        IProgress<TransferProgress> progress, CancellationToken ct = default)
    {
        var node = NodeFor(peerId);
        Interlocked.Increment(ref _activeSends);
        UpdateBle();
        try
        {
            var inner = new InlineProgress<CoreTransferStatus>(s => progress.Report(ToProgress(s)));
            var r = await node.SendFilesAsync(peerId, files, inner, ct);
            return r == NodeSendResult.Delivered ? SendOutcome.Delivered : SendOutcome.Declined;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SendOutcome.Cancelled; }
        catch (Exception e) when (IsTransferFailure(e))
        {
            Log($"send to {peerId} failed: {e.Message}");
            return SendOutcome.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _activeSends);
            UpdateBle();
        }
    }

    public async Task<SendOutcome> SendLinkAsync(string peerId, Uri link, CancellationToken ct = default)
    {
        var node = NodeFor(peerId);
        Interlocked.Increment(ref _activeSends);
        UpdateBle();
        try
        {
            var r = await node.SendLinkAsync(peerId, link, ct);
            return r == NodeSendResult.Delivered ? SendOutcome.Delivered : SendOutcome.Declined;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SendOutcome.Cancelled; }
        catch (Exception e) when (IsTransferFailure(e))
        {
            Log($"link to {peerId} failed: {e.Message}");
            return SendOutcome.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _activeSends);
            UpdateBle();
        }
    }

    private static bool IsTransferFailure(Exception e) =>
        e is AirDropRequestException or HttpRequestException or IOException or KeyNotFoundException
            or NotSupportedException or System.Net.Sockets.SocketException or OperationCanceledException
            or UnauthorizedAccessException or InvalidDataException;

    private void Log(string line) => _settings.Log?.Invoke(line);

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
            SenderContactHashes: incoming.SenderRecord?.AllHashes ?? [],
            SenderHost: ask.Host);
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
        senderId != null && new[] { _lan, _awdl }.OfType<AirDropNode>().SelectMany(n => n.Peers)
            .Any(p => p.IsAirDropWin && string.Equals(p.Id, senderId, StringComparison.OrdinalIgnoreCase));

    private static PeerInfo ToPeerInfo(NodePeer p) =>
        new(p.Id, p.Name, KindOf(p.ModelName, p.IsAirDropWin), p.ModelName, p.Record?.AllHashes ?? [], p.Host);

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
        if (_settings.Radio != null) await StopRadioAsync();
        lock (_bleLock)
        {
            _ble?.Dispose();
            _ble = null;
            _bleOn = false;
        }
        var node = _lan;
        _lan = null;
        if (node != null) await node.DisposeAsync();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
