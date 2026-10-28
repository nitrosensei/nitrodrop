using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirDrop.Core.Net;
using AirDrop.Core.Security;
using Nitro.Sync.Ipc;
using Nitro.Sync.Model;
using Nitro.Sync.Net;
using Nitro.Sync.Security;
using Nitro.Sync.Windows;

namespace Nitro.Sync;

public sealed class NitroSyncOptions
{
    public string DeviceName { get; set; } = Environment.MachineName;
    /// <summary>Hardware shared with paired peers (DeviceState.Hardware); default = this PC, null = don't share.</summary>
    public AirDrop.Core.Devices.HostDevice? Hardware { get; set; } = AirDrop.Core.Devices.HostDevice.Local;
    /// <summary>HTTPS port for peer sync (0 = random, advertised via mDNS).</summary>
    public int Port { get; set; } = SyncProtocol.DefaultPort;
    public int MdnsPort { get; set; } = AirDrop.Core.Mdns.MdnsService.DefaultPort;
    /// <summary>"any", "loopback", adapter name/index (see NetworkBinding.Resolve).</summary>
    public string? Interface { get; set; }
    /// <summary>TLS identity; default = the AirDrop one (%LOCALAPPDATA%\airdrop-win\tls-identity.pfx).</summary>
    public X509Certificate2? Certificate { get; set; }
    /// <summary>Default %LOCALAPPDATA%\airdrop-win\sync-peers.json.</summary>
    public string? StorePath { get; set; }
    public bool ProtectKeys { get; set; } = true;
    /// <summary>Null = WindowsAppsProvider with default options. Use a custom one to change the exclusion list.</summary>
    public IAppsProvider? AppsProvider { get; set; }
    public bool ShareApps { get; set; } = true;
    public TimeSpan AppsPollInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Native-messaging bridge pipe; null = NativeMessaging.PipeName(). Empty string disables it.</summary>
    public string? PipeName { get; set; }
    /// <summary>Plain-HTTP iOS Shortcut endpoint port; -1 disables it, 0 = random.</summary>
    public int IosPort { get; set; } = SyncProtocol.DefaultIosPort;
    /// <summary>For tests: allow non-LAN (e.g. loopback is LAN anyway) — production keeps the default.</summary>
    public Func<System.Net.IPAddress, bool>? IosSourceFilter { get; set; }
    public IUrlOpener? UrlOpener { get; set; }
    public IAppLauncher? AppLauncher { get; set; }
    public Action<string>? Log { get; set; }
}

public sealed record SyncPeerInfo(string DeviceId, string Name, bool IsPaired, bool IsOnline, DeviceState? State,
    DateTimeOffset? LastSeen, string? LastError);

public enum LinkSource { Peer, Ios, Extension }

public sealed record IncomingLink(string Url, string? Title, string FromDeviceId, string FromName, LinkSource Source);

public sealed record AppLaunchRequest(string FromDeviceId, string FromName, string AppId, string AppName);

/// <summary>A place the extension's "Telefona/PC'ye gönder" can deliver a link to.</summary>
public sealed record LinkTarget(string Id, string Name, string Kind);

public sealed record PairResult(bool Success, string? Code, string? Error);

/// <summary>Shown once when an iPhone is added: put <see cref="Url"/> (contains the token) into the Shortcut, or render it as a QR code.</summary>
public sealed record IosSetup(string DeviceId, string Name, string Token, string Url, IReadOnlyList<string> AlternativeUrls);

/// <summary>
/// Facade for the WPF app: continuity sync between paired NitroDrop PCs (tabs + running apps), the browser
/// extension bridge and the iOS Shortcut receiver. Thread-safe; events are raised on background threads.
/// </summary>
public sealed class NitroSyncService : IAsyncDisposable, ISyncHost
{
    private readonly NitroSyncOptions _o;
    private readonly PeerStore _store;
    private readonly X509Certificate2 _cert;
    private readonly string _certSha256;
    private readonly NetworkBinding _binding;
    private readonly IUrlOpener _opener;
    private readonly IAppLauncher _launcher;
    private readonly IAppsProvider? _apps;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PeerSlot> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IphoneActivity> _iphones = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<string, IReadOnlyList<BrowserTab>> _tabsByBrowser = new();
    private IReadOnlyList<LocalApp> _localApps = [];
    private DeviceState _state;
    private byte[] _stateJson = [];
    private string _etag = "";
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SyncServer? _server;
    private SyncDiscovery? _discovery;
    private IosReceiver? _ios;
    private SyncPipeServer? _pipe;
    private Task? _appsLoop;

    private sealed class PeerSlot
    {
        public required string DeviceId;
        public string Name = "";
        public DiscoveredPeer? Discovered;
        public DeviceState? State;
        public string? ETag;
        public DateTimeOffset? LastSeen;
        public string? LastError;
        public CancellationTokenSource? Poll;
        public SyncClient? Client;
        public string? ClientKey; // endpoint+pin the client was built for
    }

    // ---------------- events / callbacks ----------------

    /// <summary>A peer appeared/disappeared, was (un)paired, or its shared state changed.</summary>
    public event EventHandler<SyncPeerInfo>? PeerStateChanged;
    /// <summary>A link arrived (paired PC, iOS Shortcut). Raised before it is opened (when auto-open is on).</summary>
    public event EventHandler<IncomingLink>? LinkReceived;
    /// <summary>The iPhone reported an app/link ("iPhone'da Instagram açık → PC'de devam et").</summary>
    public event EventHandler<IphoneActivity>? IphoneActivityChanged;

    /// <summary>Must show <see cref="PairingPrompt.Code"/> and return the user's yes/no. Null = always refuse.</summary>
    public Func<PairingPrompt, CancellationToken, Task<bool>>? ConfirmPairing { get; set; }
    /// <summary>Required for /launch; null = launches always refused.</summary>
    public Func<AppLaunchRequest, CancellationToken, Task<bool>>? ConfirmLaunch { get; set; }
    /// <summary>Optional gate for links from paired PCs; null = open automatically.</summary>
    public Func<IncomingLink, CancellationToken, Task<bool>>? ConfirmOpen { get; set; }
    /// <summary>Open links/continuations from iOS automatically (default false: only raise the event; the UI offers "PC'de devam et").</summary>
    public bool AutoOpenIosLinks { get; set; }
    /// <summary>Extra link targets for the extension (e.g. AirDrop iPhones from AirDrop.Core). Ids must not collide with device ids ("airdrop:…").</summary>
    public Func<IReadOnlyList<LinkTarget>>? ExternalLinkTargets { get; set; }
    /// <summary>Delivers a link to an external target (e.g. AirDropNode link share).</summary>
    public Func<string, string, string?, CancellationToken, Task<bool>>? SendLinkToExternal { get; set; }

    public string DeviceId => _store.DeviceId;
    public string DeviceName => _o.DeviceName;
    public string CertSha256 => _certSha256;
    public int Port => _server?.Port ?? 0;
    public int IosPort => _ios?.Port ?? 0;
    public PeerStore Store => _store;

    public NitroSyncService(NitroSyncOptions? options = null)
    {
        _o = options ?? new NitroSyncOptions();
        _store = new PeerStore(_o.StorePath, _o.ProtectKeys);
        _cert = _o.Certificate ?? CertificateStore.LoadOrCreate();
        _certSha256 = PairingCrypto.CertFingerprint(_cert);
        _binding = NetworkBinding.Resolve(_o.Interface);
        _opener = _o.UrlOpener ?? new ShellUrlOpener();
        _launcher = _o.AppLauncher ?? new ProcessAppLauncher();
        _apps = _o.ShareApps ? _o.AppsProvider ?? new WindowsAppsProvider() : null;
        _state = new DeviceState { DeviceId = _store.DeviceId, DeviceName = _o.DeviceName };
        Rebuild(force: true);
        foreach (var p in _store.Peers) _peers[p.DeviceId] = new PeerSlot { DeviceId = p.DeviceId, Name = p.Name };
    }

    // ---------------- lifecycle ----------------

    public async Task StartAsync(CancellationToken ct = default)
    {
        _server = new SyncServer(this, _cert, _binding);
        try { _server.Start(_o.Port); }
        catch (System.Net.Sockets.SocketException) when (_o.Port != 0) { _server.Start(0); } // port taken: fall back to random

        _discovery = new SyncDiscovery(_binding, _o.MdnsPort, _store.DeviceId, _o.Log);
        _discovery.Changed += OnDiscovery;
        await _discovery.StartAsync(_o.DeviceName, (ushort)_server.Port, AirDropHostName());

        if (_o.IosPort >= 0)
        {
            _ios = new IosReceiver(_store, OnIosReportAsync, Log) { IsAllowedSource = _o.IosSourceFilter ?? NetworkScope.IsLocal };
            try { _ios.Start(_o.IosPort); }
            catch (System.Net.Sockets.SocketException e) { Log($"ios receiver: {e.Message}"); _ios = null; }
        }

        var pipeName = _o.PipeName ?? NativeMessaging.PipeName();
        if (pipeName.Length > 0)
        {
            _pipe = new SyncPipeServer(pipeName, HandlePipeAsync, Log);
            _pipe.SessionClosed += OnPipeSessionClosed;
            _pipe.Start();
        }

        if (_apps != null) _appsLoop = Task.Run(() => AppsLoop(_cts.Token));
    }

    private static string AirDropHostName() =>
        new string(Environment.MachineName.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()) + ".local.";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var s in _peers.Values) { s.Poll?.Cancel(); s.Client?.Dispose(); }
        if (_pipe != null) await _pipe.DisposeAsync();
        if (_ios != null) await _ios.DisposeAsync();
        if (_discovery != null) await _discovery.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        if (_appsLoop != null) { try { await _appsLoop; } catch (OperationCanceledException) { } }
        _cts.Dispose();
    }

    // ---------------- public API ----------------

    /// <summary>All known PCs: paired ones (online or not) and unpaired ones currently visible on the LAN.</summary>
    public IReadOnlyList<SyncPeerInfo> GetPeers() =>
        _peers.Values.Select(Info).OrderByDescending(p => p.IsOnline).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public DeviceState LocalState { get { lock (_stateGate) return _state; } }

    public IReadOnlyList<IphoneActivity> IphoneActivities => _iphones.Values.OrderByDescending(a => a.At).ToList();

    /// <summary>Pairs with a discovered PC. Both users see the same 6-digit code via <see cref="ConfirmPairing"/>.</summary>
    public async Task<PairResult> PairAsync(string deviceId, CancellationToken ct = default)
    {
        if (!_peers.TryGetValue(deviceId, out var slot) || slot.Discovered == null) return new(false, null, "Cihaz ağda bulunamadı");
        var confirm = ConfirmPairing;
        if (confirm == null) return new(false, null, "Eşleştirme onayı tanımlı değil");
        using var client = new SyncClient(slot.Discovered.EndPoint, _binding, pinnedCertSha256: null);
        using var ecdh = PairingCrypto.NewEcdh();
        var pubA = ecdh.ExportSubjectPublicKeyInfo();
        var nonceA = RandomNumberGenerator.GetBytes(16);
        string? code = null;
        byte[]? key = null;
        try
        {
            var start = await client.PostJsonAsync<PairStartResponse>(SyncProtocol.PairStart,
                new PairStartRequest(DeviceId, DeviceName, _certSha256, Convert.ToBase64String(pubA), Convert.ToBase64String(nonceA)),
                TimeSpan.FromSeconds(15), ct);
            var certB = client.ObservedCertSha256 ?? throw new SyncException("no certificate");
            if (!start.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) return new(false, null, "Cihaz kimliği uyuşmuyor");
            var pubB = Convert.FromBase64String(start.EcdhPublicKey);
            var nonceB = Convert.FromBase64String(start.Nonce);
            if (nonceB.Length != 16) return new(false, null, "Geçersiz yanıt");
            var transcript = PairingCrypto.Transcript(DeviceId, start.DeviceId, _certSha256, certB, pubA, pubB, nonceA, nonceB);
            code = PairingCrypto.Code(transcript);
            key = PairingCrypto.DeriveKey(ecdh, pubB, nonceA, nonceB, transcript);
            var peerName = StateSanitizer.Clamp(start.Name, StateLimits.MaxName);

            using var promptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            promptCts.CancelAfter(SyncServer.PairDecisionTimeout);
            if (!await confirm(new PairingPrompt(start.DeviceId, peerName, code, IsInitiator: true), promptCts.Token))
                return new(false, code, "Eşleştirme reddedildi");

            var fin = await client.PostJsonAsync<PairFinishResponse>(SyncProtocol.PairFinish,
                new PairFinishRequest(start.SessionId, Convert.ToBase64String(PairingCrypto.Confirm(key, "A", transcript))),
                SyncServer.PairDecisionTimeout + TimeSpan.FromSeconds(10), ct);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(fin.Confirm), PairingCrypto.Confirm(key, "B", transcript)))
                return new(false, code, "Anahtar doğrulaması başarısız");
            var peer = _store.AddOrReplace(start.DeviceId, peerName, certB, key);
            HandlePaired(peer);
            return new(true, code, null);
        }
        catch (SyncException e) when (e.Status == 403) { return new(false, code, "Karşı taraf reddetti"); }
        catch (Exception e) when (e is SyncException or HttpRequestException or FormatException or JsonException or CryptographicException)
        {
            return new(false, code, e.Message);
        }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Forgets a paired PC (and tells it, best effort).</summary>
    public async Task UnpairAsync(string deviceId, CancellationToken ct = default)
    {
        try { await SendSignedAsync(deviceId, HttpMethod.Post, SyncProtocol.Unpair, [], TimeSpan.FromSeconds(5), ct); }
        catch (Exception e) when (e is SyncException or HttpRequestException or InvalidOperationException) { }
        if (_store.Remove(deviceId) && _peers.TryGetValue(deviceId, out var s))
        {
            StopPolling(s);
            s.State = null;
            if (s.Discovered == null) _peers.TryRemove(deviceId, out _);
            Raise(s);
        }
    }

    /// <summary>Opens <paramref name="url"/> (http/https only) in the default browser of a paired PC.</summary>
    public async Task<bool> OpenOnPeerAsync(string deviceId, string url, string? title = null, CancellationToken ct = default)
    {
        if (!UrlPolicy.TryNormalize(url, out var safe)) throw new ArgumentException("Yalnızca http/https bağlantıları gönderilebilir", nameof(url));
        var body = JsonSerializer.SerializeToUtf8Bytes(new OpenRequest(safe, title), SyncProtocol.Json);
        var (status, _, _) = await SendSignedAsync(deviceId, HttpMethod.Post, SyncProtocol.Open, body, TimeSpan.FromSeconds(30), ct);
        return status == 200;
    }

    /// <summary>Asks a paired PC to start one of the apps it advertises (its user must confirm).</summary>
    public async Task<bool> LaunchOnPeerAsync(string deviceId, string appId, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new LaunchRequest(appId), SyncProtocol.Json);
        var (status, _, _) = await SendSignedAsync(deviceId, HttpMethod.Post, SyncProtocol.Launch, body, TimeSpan.FromMinutes(2), ct);
        return status == 200;
    }

    /// <summary>Replaces the tabs of one browser (normally fed by the extension through the pipe).</summary>
    public void UpdateLocalTabs(string browser, IEnumerable<BrowserTab> tabs)
    {
        var b = BrowserKind.Normalize(browser);
        var clean = StateSanitizer.Tabs(tabs, b);
        lock (_stateGate) _tabsByBrowser[b] = clean;
        Rebuild();
    }

    /// <summary>"PC'de devam et": opens the continuation target of an iPhone activity on this PC.</summary>
    public Task<bool> ContinueOnPcAsync(IphoneActivity activity, CancellationToken ct = default)
    {
        var t = activity.Target ?? ContinuationMap.Resolve(activity.App, activity.Url);
        if (t == null) return Task.FromResult(false);
        if (t.AppUri != null && _opener.TryOpenAppUri(t.AppUri)) return Task.FromResult(true);
        return Task.FromResult(t.WebUrl.Length > 0 && _opener.OpenWeb(t.WebUrl));
    }

    /// <summary>Registers an iPhone/iPad Shortcut and returns its one-time setup URL (token inside; show as QR/URL).</summary>
    public IosSetup AddIosDevice(string name)
    {
        var (token, hash) = IosReceiver.NewToken();
        var id = "ios-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        _store.AddIos(new IosDevice { Id = id, Name = StateSanitizer.Clamp(name, StateLimits.MaxName), TokenSha256 = hash });
        int port = _ios?.Port ?? (_o.IosPort > 0 ? _o.IosPort : SyncProtocol.DefaultIosPort);
        var urls = _binding.AdvertisedAddresses()
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a) && NetworkScope.IsLocal(a))
            .Select(a => $"http://{a}:{port}{IosReceiver.ActivityPath}?t={token}")
            .ToList();
        if (urls.Count == 0) urls.Add($"http://{Environment.MachineName}.local:{port}{IosReceiver.ActivityPath}?t={token}");
        return new IosSetup(id, name, token, urls[0], urls.Skip(1).ToList());
    }

    public bool RemoveIosDevice(string id)
    {
        _iphones.TryRemove(id, out _);
        return _store.RemoveIos(id);
    }

    public IReadOnlyList<IosDevice> IosDevices => _store.IosDevices;

    /// <summary>Targets for "Telefona/PC'ye gönder": paired online PCs + <see cref="ExternalLinkTargets"/>.</summary>
    public IReadOnlyList<LinkTarget> GetLinkTargets()
    {
        var list = _peers.Values.Where(s => _store.Find(s.DeviceId) != null && s.Discovered != null)
            .Select(s => new LinkTarget(s.DeviceId, s.Name, "pc")).ToList();
        try { if (ExternalLinkTargets?.Invoke() is { } ext) list.AddRange(ext); }
        catch (Exception e) { Log($"external targets: {e.Message}"); }
        return list;
    }

    /// <summary>Delivers a link to a target from <see cref="GetLinkTargets"/>.</summary>
    public async Task<bool> SendLinkAsync(string targetId, string url, string? title, CancellationToken ct = default)
    {
        if (!UrlPolicy.TryNormalize(url, out var safe)) return false;
        if (_store.Find(targetId) != null) return await OpenOnPeerAsync(targetId, safe, title, ct);
        return SendLinkToExternal != null && await SendLinkToExternal(targetId, safe, title, ct);
    }

    // ---------------- local state ----------------

    private void Rebuild(bool force = false)
    {
        lock (_stateGate)
        {
            var next = _state with
            {
                DeviceName = _o.DeviceName,
                Hardware = _o.Hardware,
                Tabs = _tabsByBrowser.Values.SelectMany(t => t).Take(StateLimits.MaxTabs).ToList(),
                Apps = _localApps.Select(a => a.Info).ToList(),
            };
            var etag = next.ComputeETag();
            if (!force && etag == _etag) return;
            _state = next with { UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            _stateJson = _state.ToJsonBytes();
            _etag = etag;
            var old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }
    }

    private async Task AppsLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var apps = _apps!.GetApps();
                lock (_stateGate) _localApps = apps.Take(StateLimits.MaxApps).ToList();
                Rebuild();
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log($"apps: {e.Message}"); }
            try { await Task.Delay(_o.AppsPollInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    // ---------------- ISyncHost ----------------

    PeerStore ISyncHost.Store => _store;

    (byte[] json, string etag) ISyncHost.SnapshotState() { lock (_stateGate) return (_stateJson, _etag); }

    async Task ISyncHost.WaitForStateChangeAsync(string etag, TimeSpan max, CancellationToken ct)
    {
        Task changed;
        lock (_stateGate)
        {
            if (_etag != etag) return;
            changed = _changed.Task;
        }
        try { await changed.WaitAsync(max, ct); }
        catch (TimeoutException) { }
    }

    Task<bool> ISyncHost.ConfirmPairingAsync(PairingPrompt prompt, CancellationToken ct) =>
        ConfirmPairing?.Invoke(prompt, ct) ?? Task.FromResult(false);

    void ISyncHost.OnPaired(PairedPeer peer) => HandlePaired(peer);

    private void HandlePaired(PairedPeer peer)
    {
        var s = _peers.GetOrAdd(peer.DeviceId, id => new PeerSlot { DeviceId = id });
        s.Name = peer.Name;
        s.LastError = null;
        Log($"paired with {peer.Name} ({peer.DeviceId})");
        EnsurePolling(s);
        Raise(s);
    }

    void ISyncHost.OnUnpairedByPeer(PairedPeer peer)
    {
        if (!_peers.TryGetValue(peer.DeviceId, out var s)) return;
        StopPolling(s);
        s.State = null;
        Raise(s);
    }

    async Task<bool> ISyncHost.HandleOpenAsync(PairedPeer from, string url, string? title, CancellationToken ct)
    {
        var link = new IncomingLink(url, title, from.DeviceId, from.Name, LinkSource.Peer);
        LinkReceived?.Invoke(this, link);
        if (ConfirmOpen != null && !await ConfirmOpen(link, ct)) return false;
        return _opener.OpenWeb(url);
    }

    async Task<bool> ISyncHost.HandleLaunchAsync(PairedPeer from, string appId, CancellationToken ct)
    {
        LocalApp? app;
        lock (_stateGate) app = _localApps.FirstOrDefault(a => a.Info.AppId == appId); // only what we advertise
        if (app == null || ConfirmLaunch == null) return false;
        if (!await ConfirmLaunch(new AppLaunchRequest(from.DeviceId, from.Name, appId, app.Info.Name), ct)) return false;
        return _launcher.Launch(app.FullPath);
    }

    void ISyncHost.Log(string message) => Log(message);

    private void Log(string m) => _o.Log?.Invoke("[sync] " + m);

    // ---------------- iOS ----------------

    private async Task OnIosReportAsync(IosReport r)
    {
        var activity = new IphoneActivity
        {
            DeviceId = r.Device.Id, DeviceName = r.Device.Name, App = r.App, Url = r.Url, Title = r.Title,
            Target = ContinuationMap.Resolve(r.App, r.Url),
        };
        _iphones[r.Device.Id] = activity;
        IphoneActivityChanged?.Invoke(this, activity);
        if (r.Url != null)
        {
            LinkReceived?.Invoke(this, new IncomingLink(r.Url, r.Title, r.Device.Id, r.Device.Name, LinkSource.Ios));
            if (AutoOpenIosLinks) _opener.OpenWeb(r.Url);
        }
        await PushDevicesToExtensionsAsync();
    }

    // ---------------- peers ----------------

    private void OnDiscovery(IReadOnlyList<DiscoveredPeer> found)
    {
        var ids = found.Select(f => f.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in found)
        {
            var s = _peers.GetOrAdd(d.DeviceId, id => new PeerSlot { DeviceId = id });
            bool changed = s.Discovered == null || !s.Discovered.EndPoint.Equals(d.EndPoint) || s.Name != d.Name;
            s.Discovered = d;
            if (_store.Find(d.DeviceId) is { } paired) s.Name = paired.Name; else s.Name = d.Name;
            if (changed)
            {
                if (_store.Find(d.DeviceId) != null) EnsurePolling(s, restart: true);
                Raise(s);
            }
        }
        foreach (var s in _peers.Values.Where(s => s.Discovered != null && !ids.Contains(s.DeviceId)).ToList())
        {
            s.Discovered = null;
            StopPolling(s);
            if (_store.Find(s.DeviceId) == null) _peers.TryRemove(s.DeviceId, out _);
            Raise(s);
        }
    }

    private SyncPeerInfo Info(PeerSlot s) =>
        new(s.DeviceId, s.Name, _store.Find(s.DeviceId) != null, s.Discovered != null, s.State, s.LastSeen, s.LastError);

    private void Raise(PeerSlot s)
    {
        try { PeerStateChanged?.Invoke(this, Info(s)); }
        catch (Exception e) { Log($"PeerStateChanged handler: {e.Message}"); }
        _ = PushDevicesToExtensionsAsync();
    }

    private void EnsurePolling(PeerSlot s, bool restart = false)
    {
        lock (s)
        {
            if (s.Discovered == null) return;
            if (s.Poll != null && !restart) return;
            s.Poll?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            s.Poll = cts;
            _ = Task.Run(() => PollLoop(s, cts.Token));
        }
    }

    private static void StopPolling(PeerSlot s)
    {
        lock (s)
        {
            s.Poll?.Cancel();
            s.Poll = null;
            s.ETag = null;
        }
    }

    private async Task PollLoop(PeerSlot s, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var path = SyncProtocol.State + (s.ETag != null ? "?wait=" + (int)SyncProtocol.MaxLongPoll.TotalSeconds : "");
                var (status, body, etag) = await SendSignedAsync(s.DeviceId, HttpMethod.Get, path, [], SyncProtocol.MaxLongPoll + TimeSpan.FromSeconds(10), ct, s.ETag);
                if (status == 200)
                {
                    var st = DeviceState.FromJson(body);
                    if (st == null) throw new SyncException("bad state");
                    st = StateSanitizer.State(st) with { DeviceId = s.DeviceId };
                    s.State = st;
                    s.ETag = etag;
                    s.LastSeen = DateTimeOffset.UtcNow;
                    s.LastError = null;
                    backoff = TimeSpan.FromSeconds(1);
                    Raise(s);
                }
                else if (status == 304)
                {
                    s.LastSeen = DateTimeOffset.UtcNow;
                    backoff = TimeSpan.FromSeconds(1);
                }
                else
                {
                    s.LastError = status == 401 ? "Karşı cihaz bu PC'yi tanımıyor (yeniden eşleştirin)" : $"HTTP {status}";
                    throw new SyncException(s.LastError, status);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is SyncException or HttpRequestException or IOException or InvalidOperationException)
            {
                s.LastError ??= e.Message;
                if (e is SyncException se && se.Message.Contains("pin mismatch")) s.LastError = se.Message;
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task<(int status, byte[] body, string? etag)> SendSignedAsync(string deviceId, HttpMethod method, string path,
        byte[] body, TimeSpan timeout, CancellationToken ct, string? ifNoneMatch = null)
    {
        var peer = _store.Find(deviceId) ?? throw new InvalidOperationException("Cihaz eşleştirilmemiş");
        if (!_peers.TryGetValue(deviceId, out var s) || s.Discovered == null) throw new InvalidOperationException("Cihaz çevrim dışı");
        SyncClient client;
        lock (s)
        {
            var clientKey = s.Discovered.EndPoint + "|" + peer.CertSha256;
            if (s.Client == null || s.ClientKey != clientKey)
            {
                s.Client?.Dispose();
                s.Client = new SyncClient(s.Discovered.EndPoint, _binding, peer.CertSha256);
                s.ClientKey = clientKey;
            }
            client = s.Client;
        }
        var key = _store.KeyOf(peer);
        try { return await client.SendSignedAsync(method, path, body, DeviceId, key, peer.DeviceId, ifNoneMatch, timeout, ct); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // ---------------- extension bridge ----------------

    private async Task<JsonObject?> HandlePipeAsync(PipeSession session, JsonObject msg, CancellationToken ct)
    {
        var type = (string?)msg["type"];
        switch (type)
        {
            case "hostHello":
                session.Origin = (string?)msg["origin"];
                return null;
            case "hello":
                session.Browser = BrowserKind.Normalize((string?)msg["browser"]);
                var hello = new JsonObject { ["type"] = "hello", ["deviceId"] = DeviceId, ["deviceName"] = DeviceName };
                _ = Task.Run(() => session.SendAsync(DevicesMessage(), ct), ct);
                return hello;
            case "tabs":
            {
                var browser = (string?)msg["browser"] ?? session.Browser;
                session.Browser = BrowserKind.Normalize(browser);
                List<BrowserTab>? tabs = null;
                try { tabs = msg["tabs"]?.Deserialize<List<BrowserTab>>(DeviceState.Json); }
                catch (JsonException) { }
                if (tabs == null) return Error("bad_tabs");
                UpdateLocalTabs(session.Browser, tabs);
                return msg["reqId"] != null ? new JsonObject { ["type"] = "ack" } : null;
            }
            case "getDevices":
                return DevicesMessage();
            case "getTargets":
            {
                var arr = new JsonArray();
                foreach (var t in GetLinkTargets())
                    arr.Add(new JsonObject { ["id"] = t.Id, ["name"] = t.Name, ["kind"] = t.Kind });
                return new JsonObject { ["type"] = "targets", ["targets"] = arr };
            }
            case "sendLink":
            {
                var target = (string?)msg["targetId"];
                var url = (string?)msg["url"];
                if (target == null || !UrlPolicy.TryNormalize(url, out var safe)) return Error("bad_url");
                bool ok;
                try { ok = await SendLinkAsync(target, safe, (string?)msg["title"], ct); }
                catch (Exception e) when (e is SyncException or HttpRequestException or InvalidOperationException or ArgumentException)
                {
                    return new JsonObject { ["type"] = "sendResult", ["ok"] = false, ["error"] = e.Message };
                }
                return new JsonObject { ["type"] = "sendResult", ["ok"] = ok };
            }
            case "continue":
            {
                var id = (string?)msg["deviceId"];
                if (id == null || !_iphones.TryGetValue(id, out var a)) return Error("unknown_device");
                return new JsonObject { ["type"] = "continueResult", ["ok"] = await ContinueOnPcAsync(a, ct) };
            }
            default:
                return Error("unknown_type");
        }
    }

    private static JsonObject Error(string code) => new() { ["type"] = "error", ["error"] = code };

    private void OnPipeSessionClosed(PipeSession s)
    {
        // That browser is gone (or the extension reloaded): its tabs are no longer open here.
        if (_pipe?.Sessions.Any(x => x.Browser == s.Browser) == true) return;
        lock (_stateGate) _tabsByBrowser.Remove(s.Browser);
        Rebuild();
    }

    /// <summary>Other devices for the popup: paired PCs with their tabs (newest first, ≤100 each) + iPhone activity.</summary>
    internal JsonObject DevicesMessage()
    {
        var devices = new JsonArray();
        foreach (var p in GetPeers().Where(p => p.IsPaired))
        {
            var tabs = new JsonArray();
            foreach (var t in (p.State?.Tabs ?? []).OrderByDescending(t => t.LastAccessed).Take(100))
                tabs.Add(new JsonObject
                {
                    ["title"] = t.Title, ["url"] = t.Url, ["favIconUrl"] = t.FaviconUrl, ["browser"] = t.Browser,
                    ["active"] = t.Active, ["lastAccessed"] = t.LastAccessed,
                });
            devices.Add(new JsonObject
            {
                ["id"] = p.DeviceId, ["name"] = p.Name, ["kind"] = "pc", ["online"] = p.IsOnline, ["tabs"] = tabs,
            });
        }
        foreach (var a in IphoneActivities)
            devices.Add(new JsonObject
            {
                ["id"] = a.DeviceId, ["name"] = a.DeviceName, ["kind"] = "phone", ["online"] = true,
                ["activity"] = new JsonObject
                {
                    ["app"] = a.Target?.DisplayName ?? a.App, ["url"] = a.Url, ["title"] = a.Title,
                    ["at"] = a.At.ToUnixTimeMilliseconds(), ["canContinue"] = a.Target != null,
                },
            });
        return new JsonObject { ["type"] = "devices", ["devices"] = devices };
    }

    private async Task PushDevicesToExtensionsAsync()
    {
        if (_pipe == null) return;
        var sessions = _pipe.Sessions;
        if (sessions.Count == 0) return;
        var msg = DevicesMessage();
        foreach (var s in sessions)
        {
            try { await s.SendAsync(msg.DeepClone()); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException) { }
        }
    }
}
