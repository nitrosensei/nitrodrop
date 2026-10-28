using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nitro.Sync;
using Nitro.Sync.Model;
using Nitro.Sync.Security;
using Nitro.Sync.Windows;
using AirDrop.App.Localization;

namespace AirDrop.App.Services;

/// <summary>What the user chose in Ayarlar → Senkron; applied by recreating the sync service.</summary>
public sealed record SyncConfig(string DeviceName, bool ShareTabs, bool ShareApps, IReadOnlyList<string> ExcludedApps)
{
    public static SyncConfig From(AppSettings s) =>
        new(s.DeviceName, s.SyncShareTabs, s.SyncShareApps, s.SyncExcludedApps.ToList());

    public bool SameAs(SyncConfig o) =>
        DeviceName == o.DeviceName && ShareTabs == o.ShareTabs && ShareApps == o.ShareApps &&
        ExcludedApps.SequenceEqual(o.ExcludedApps, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// UI boundary for Nitro.Sync (continuity between NitroDrop PCs, the browser extension and the iOS Shortcut).
/// The real one wraps <see cref="NitroSyncService"/>; <see cref="DemoSyncSource"/> feeds sample data for
/// <c>--demo</c> / <c>--snapshot</c>. Events arrive on background threads.
/// </summary>
public interface ISyncSource : IAsyncDisposable
{
    bool IsDemo { get; }
    /// <summary>Null when running; otherwise why sync could not start (shown in Ayarlar).</summary>
    string? StartError { get; }

    event EventHandler<SyncPeerInfo>? PeerStateChanged;
    event EventHandler<IncomingLink>? LinkReceived;
    event EventHandler<IphoneActivity>? IphoneActivityChanged;

    Func<PairingPrompt, CancellationToken, Task<bool>>? ConfirmPairing { get; set; }
    Func<AppLaunchRequest, CancellationToken, Task<bool>>? ConfirmLaunch { get; set; }
    Func<IReadOnlyList<LinkTarget>>? ExternalLinkTargets { get; set; }
    Func<string, string, string?, CancellationToken, Task<bool>>? SendLinkToExternal { get; set; }

    /// <summary>What this PC currently shares (tabs from the extension, running apps).</summary>
    DeviceState? LocalState { get; }
    IReadOnlyList<IosDevice> IosDevices { get; }

    Task StartAsync(SyncConfig config);
    /// <summary>Applies changed settings (restarts the service when needed).</summary>
    Task ReconfigureAsync(SyncConfig config);

    IReadOnlyList<SyncPeerInfo> GetPeers();
    Task<PairResult> PairAsync(string deviceId);
    Task UnpairAsync(string deviceId);
    Task<bool> ContinueOnPcAsync(IphoneActivity activity);
    IosSetup AddIosDevice(string name);
    bool RemoveIosDevice(string id);
}

/// <summary>The real thing: a <see cref="NitroSyncService"/> that is rebuilt when the sync settings change.</summary>
public sealed class NitroSyncSource : ISyncSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NitroSyncService? _svc;
    private SyncConfig? _config;

    public bool IsDemo => false;
    public string? StartError { get; private set; }

    public event EventHandler<SyncPeerInfo>? PeerStateChanged;
    public event EventHandler<IncomingLink>? LinkReceived;
    public event EventHandler<IphoneActivity>? IphoneActivityChanged;

    public Func<PairingPrompt, CancellationToken, Task<bool>>? ConfirmPairing { get; set; }
    public Func<AppLaunchRequest, CancellationToken, Task<bool>>? ConfirmLaunch { get; set; }
    public Func<IReadOnlyList<LinkTarget>>? ExternalLinkTargets { get; set; }
    public Func<string, string, string?, CancellationToken, Task<bool>>? SendLinkToExternal { get; set; }

    public DeviceState? LocalState => _svc?.LocalState;
    public IReadOnlyList<IosDevice> IosDevices => _svc?.IosDevices ?? Array.Empty<IosDevice>();

    public async Task StartAsync(SyncConfig config)
    {
        await _gate.WaitAsync();
        try { await StartCoreAsync(config); }
        finally { _gate.Release(); }
    }

    public async Task ReconfigureAsync(SyncConfig config)
    {
        await _gate.WaitAsync();
        try
        {
            if (_config is not null && _config.SameAs(config) && _svc is not null) return;
            await StopCoreAsync();
            await StartCoreAsync(config);
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(SyncConfig config)
    {
        _config = config;
        StartError = null;
        try
        {
            var apps = new AppsProviderOptions();
            foreach (var exe in config.ExcludedApps) apps.ExcludedExecutables.Add(exe);
            var svc = new NitroSyncService(new NitroSyncOptions
            {
                DeviceName = config.DeviceName,
                ShareApps = config.ShareApps,
                AppsProvider = config.ShareApps ? new WindowsAppsProvider(apps) : null,
                // No extension bridge = no tabs are collected from the browser (the facade has no separate switch).
                PipeName = config.ShareTabs ? null : "",
                Log = m => Debug.WriteLine(m),
            });
            // Callbacks go through this wrapper so they survive a restart.
            svc.ConfirmPairing = (p, ct) => ConfirmPairing?.Invoke(p, ct) ?? Task.FromResult(false);
            svc.ConfirmLaunch = (r, ct) => ConfirmLaunch?.Invoke(r, ct) ?? Task.FromResult(false);
            svc.ConfirmOpen = null;               // links from paired PCs open directly
            svc.AutoOpenIosLinks = false;         // iPhone links: notification + "PC'de devam et"
            svc.ExternalLinkTargets = () => ExternalLinkTargets?.Invoke() ?? Array.Empty<LinkTarget>();
            svc.SendLinkToExternal = (id, url, title, ct) =>
                SendLinkToExternal?.Invoke(id, url, title, ct) ?? Task.FromResult(false);
            svc.PeerStateChanged += (_, p) => PeerStateChanged?.Invoke(this, p);
            svc.LinkReceived += (_, l) => LinkReceived?.Invoke(this, l);
            svc.IphoneActivityChanged += (_, a) => IphoneActivityChanged?.Invoke(this, a);
            _svc = svc;
            await svc.StartAsync();
            foreach (var p in svc.GetPeers()) PeerStateChanged?.Invoke(this, p);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            StartError = e.Message;
            await StopCoreAsync();   // half-started (e.g. a port in use): tear down; the next settings change retries
        }
    }

    private async Task StopCoreAsync()
    {
        var old = _svc;
        _svc = null;
        if (old is null) return;
        try { await old.DisposeAsync(); }
        catch (Exception e) { Debug.WriteLine(e); }
    }

    public IReadOnlyList<SyncPeerInfo> GetPeers() => _svc?.GetPeers() ?? Array.Empty<SyncPeerInfo>();

    public Task<PairResult> PairAsync(string deviceId) =>
        _svc?.PairAsync(deviceId) ?? Task.FromResult(new PairResult(false, null, DevicesStrings.Pair_ErrNoSync));

    public Task UnpairAsync(string deviceId) => _svc?.UnpairAsync(deviceId) ?? Task.CompletedTask;

    public Task<bool> ContinueOnPcAsync(IphoneActivity activity) =>
        _svc?.ContinueOnPcAsync(activity) ?? Task.FromResult(false);

    public IosSetup AddIosDevice(string name) =>
        (_svc ?? throw new InvalidOperationException(DevicesStrings.Pair_ErrNoSync)).AddIosDevice(name);

    public bool RemoveIosDevice(string id) => _svc?.RemoveIosDevice(id) ?? false;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }
}
