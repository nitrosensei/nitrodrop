using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirDrop.App.Services;
using Nitro.Sync;
using Nitro.Sync.Model;
using Nitro.Sync.Security;

namespace AirDrop.App.ViewModels;

/// <summary>"Cihazlar" tab + Ayarlar → Senkron: paired NitroDrop PCs, their tabs/apps, iPhone activity, pairing.</summary>
public sealed class DevicesViewModel : Observable
{
    private static readonly TimeSpan IphoneCardLifetime = TimeSpan.FromMinutes(5);

    private readonly ISyncSource _sync;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, SyncPcViewModel> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick;

    private IphoneActivity? _activity;
    private DateTime _activityShownAt;
    private bool _isAddingPc, _showExtensionHelp, _extensionConnected;
    private string _extensionStatus = "", _extensionDetail = "", _registerStatus = "", _newExcluded = "", _iphoneDetail = "";
    private IosSetup? _iosSetup;
    private ImageSource? _iosQr;

    public DevicesViewModel(ISyncSource sync, AppSettings settings, Dispatcher ui)
    {
        _sync = sync;
        _settings = settings;
        _ui = ui;

        _sync.PeerStateChanged += (_, p) => _ui.BeginInvoke(() => OnPeer(p));
        _sync.IphoneActivityChanged += (_, a) => _ui.BeginInvoke(() => ShowIphone(a));
        _sync.LinkReceived += (_, l) => _ui.BeginInvoke(() => OnLink(l));
        _sync.ConfirmPairing = (p, ct) =>
            _ui.InvokeAsync(() => ShowPairSheet?.Invoke(p, ct) ?? Task.FromResult(false)).Task.Unwrap();
        _sync.ConfirmLaunch = (r, ct) =>
            _ui.InvokeAsync(() => ShowLaunchSheet?.Invoke(r, ct) ?? Task.FromResult(false)).Task.Unwrap();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();

        foreach (var exe in _settings.SyncExcludedApps) ExcludedApps.Add(exe);

        ToggleAddPc = new RelayCommand(_ => IsAddingPc = !IsAddingPc);
        ContinueOnPc = new RelayCommand(async _ => await ContinueAsync(), _ => _activity?.Target is not null);
        DismissIphone = new RelayCommand(_ => HideIphone());
        AddExcluded = new RelayCommand(_ => AddExcludedApp(), _ => NormalizeExe(NewExcludedApp) is not null);
        RemoveExcluded = new RelayCommand(p => { if (p is string s) RemoveExcludedApp(s); });
        ToggleExtensionHelp = new RelayCommand(_ => ShowExtensionHelp = !ShowExtensionHelp);
        RegisterExtension = new RelayCommand(async _ => await RegisterExtensionAsync());
        OpenExtensionFolder = new RelayCommand(_ => Shell(ExtensionHost.ExtensionDir));
        CopyExtensionFolder = new RelayCommand(_ => TryCopy(ExtensionHost.ExtensionDir));
        AddIphone = new RelayCommand(_ => AddIphoneDevice(), _ => _sync.StartError is null);
        CloseIosSetup = new RelayCommand(_ => { IosSetup = null; });
        OpenShortcutHelp = new RelayCommand(_ => OpenShortcutDoc());
        CopyIosUrl = new RelayCommand(_ => { if (_iosSetup is not null) TryCopy(_iosSetup.Url); });
        RefreshIos();
        RefreshExtensionStatus();
    }

    // ---------------- view hooks ----------------

    public Func<PairingPrompt, CancellationToken, Task<bool>>? ShowPairSheet { get; set; }
    public Func<AppLaunchRequest, CancellationToken, Task<bool>>? ShowLaunchSheet { get; set; }
    public Action<string, string, Action?>? Notify { get; set; }
    public Action<HistoryEntry>? AddHistory { get; set; }
    /// <summary>Asks for the iPhone's name (TextPrompt); null = cancelled.</summary>
    public Func<string?>? AskIphoneName { get; set; }

    // ---------------- PCs ----------------

    public ObservableCollection<SyncPcViewModel> Paired { get; } = new();
    public ObservableCollection<SyncPcViewModel> Discovered { get; } = new();
    public bool HasPaired => Paired.Count > 0;
    public bool HasDiscovered => Discovered.Count > 0;
    public string? SyncError => _sync.StartError is { } e ? "Senkron başlatılamadı: " + e : null;
    public bool HasSyncError => _sync.StartError is not null;

    public bool IsAddingPc
    {
        get => _isAddingPc;
        set { if (Set(ref _isAddingPc, value)) Raise(nameof(AddPcButtonText)); }
    }
    public string AddPcButtonText => IsAddingPc ? "Bitti" : "Bilgisayar ekle";

    public RelayCommand ToggleAddPc { get; }

    public async Task StartAsync()
    {
        await _sync.StartAsync(SyncConfig.From(_settings));
        Resync();
    }

    /// <summary>Called after Ayarlar changed (device name, sharing switches, exclusions).</summary>
    public async Task ApplyConfigAsync()
    {
        await _sync.ReconfigureAsync(SyncConfig.From(_settings));
        Resync();
    }

    private void Resync()
    {
        var peers = _sync.GetPeers();
        var ids = peers.Select(p => p.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _byId.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            Paired.Remove(_byId[gone]);
            Discovered.Remove(_byId[gone]);
            _byId.Remove(gone);
        }
        foreach (var p in peers) OnPeer(p);
        Raise(nameof(SyncError)); Raise(nameof(HasSyncError));
        RefreshIos();
        RefreshExtensionStatus();
    }

    private void OnPeer(SyncPeerInfo p)
    {
        if (!_byId.TryGetValue(p.DeviceId, out var vm))
            _byId[p.DeviceId] = vm = new SyncPcViewModel(p.DeviceId, PairAsync, UnpairAsync);
        vm.Update(p);

        if (p.IsPaired)
        {
            Discovered.Remove(vm);
            Place(vm);
        }
        else
        {
            Paired.Remove(vm);
            if (p.IsOnline) { if (!Discovered.Contains(vm)) Discovered.Add(vm); }
            else { Discovered.Remove(vm); _byId.Remove(p.DeviceId); }
        }
        Raise(nameof(HasPaired)); Raise(nameof(HasDiscovered));
        RefreshExtensionStatus();
    }

    /// <summary>Keeps paired PCs sorted: online first, then by name.</summary>
    private void Place(SyncPcViewModel vm)
    {
        var order = Paired.Where(x => x != vm).Append(vm)
            .OrderByDescending(x => x.IsOnline).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        int target = order.IndexOf(vm);
        int current = Paired.IndexOf(vm);
        if (current < 0) Paired.Insert(target, vm);
        else if (current != target) Paired.Move(current, target);
    }

    private async Task PairAsync(SyncPcViewModel pc)
    {
        if (pc.IsPairing) return;
        pc.IsPairing = true;
        pc.PairError = null;
        PairResult r;
        try { r = await _sync.PairAsync(pc.Id); }
        catch (Exception e) { r = new PairResult(false, null, e.Message); }
        pc.IsPairing = false;
        if (r.Success)
        {
            IsAddingPc = false;
            Notify?.Invoke("NitroDrop", $"{pc.Name} ile eşleştirildi", null);
        }
        else if (r.Error != "Eşleştirme reddedildi")
        {
            pc.PairError = r.Error;
        }
    }

    private async Task UnpairAsync(SyncPcViewModel pc)
    {
        try { await _sync.UnpairAsync(pc.Id); }
        catch (Exception e) { Debug.WriteLine(e); }
        Resync();
    }

    // ---------------- iPhone activity ----------------

    public bool HasIphoneActivity => _activity is not null;
    public string IphoneHeadline
    {
        get
        {
            if (_activity is not { } a) return "";
            var name = a.Target?.DisplayName ?? a.App ?? "bir uygulama";
            if (a.Url is not null) return $"{a.DeviceName} bir bağlantı paylaştı";
            return $"iPhone'da {name} açık";
        }
    }
    public string IphoneDetail { get => _iphoneDetail; private set => Set(ref _iphoneDetail, value); }
    public string IphoneAppInitial => (_activity?.Target?.DisplayName ?? _activity?.App ?? "?")[..1].ToUpper(CultureInfo.CurrentCulture);
    public Brush IphoneAppTile => Palette.For(_activity?.Target?.AppKey ?? _activity?.App ?? "");
    public bool CanContinue => _activity?.Target is not null;
    public RelayCommand ContinueOnPc { get; }
    public RelayCommand DismissIphone { get; }

    private void ShowIphone(IphoneActivity a)
    {
        _activity = a;
        _activityShownAt = DateTime.UtcNow;
        RaiseIphone();   // quiet card only (an automation fires on every app open — no balloon spam)
    }

    private void HideIphone()
    {
        _activity = null;
        RaiseIphone();
    }

    private void RaiseIphone()
    {
        Raise(nameof(HasIphoneActivity)); Raise(nameof(IphoneHeadline)); Raise(nameof(CanContinue));
        Raise(nameof(IphoneAppInitial)); Raise(nameof(IphoneAppTile));
        IphoneDetail = _activity is { } a ? DescribeIphone(a) : "";
    }

    private static string DescribeIphone(IphoneActivity a)
    {
        string when = Ago(a.At);
        if (a.Url is not null) return $"{a.Title ?? Host(a.Url)} · {when}";
        return $"{a.DeviceName} · {when}";
    }

    private async Task ContinueAsync()
    {
        if (_activity is not { } a) return;
        bool ok;
        try { ok = await _sync.ContinueOnPcAsync(a); }
        catch (Exception e) { Debug.WriteLine(e); ok = false; }
        if (ok) HideIphone();
        else Notify?.Invoke("NitroDrop", "Bu etkinlik PC'de açılamadı.", null);
    }

    private void Tick()
    {
        if (_activity is { } a)
        {
            if (DateTime.UtcNow - _activityShownAt > IphoneCardLifetime) HideIphone();
            else IphoneDetail = DescribeIphone(a);
        }
        foreach (var pc in Paired) pc.RefreshStatus();
    }

    // ---------------- incoming links ----------------

    private void OnLink(IncomingLink l)
    {
        AddHistory?.Invoke(new HistoryEntry
        {
            Direction = TransferDirection.Received,
            DeviceName = l.FromName,
            DeviceKey = "sync:" + l.FromDeviceId,
            DeviceKind = l.Source == LinkSource.Ios ? DeviceKind.iPhone : DeviceKind.Windows,
            Host = l.Source == LinkSource.Peer ? Paired.FirstOrDefault(p => p.Id == l.FromDeviceId)?.Hardware : null,
            Links = new() { l.Url },
        });
        string what = l.Title is { Length: > 0 } t ? t : Host(l.Url);
        if (l.Source == LinkSource.Peer)
            Notify?.Invoke("NitroDrop", $"{l.FromName} bir bağlantı açtı: {what}", null);   // already opened by Nitro.Sync
        else
            Notify?.Invoke("NitroDrop", $"{l.FromName}: {what} — açmak için tıklayın", () => OpenUrl(l.Url));
    }

    // ---------------- settings: sharing ----------------

    public bool ShareTabs
    {
        get => _settings.SyncShareTabs;
        set { _settings.SyncShareTabs = value; Raise(); Commit(); }
    }

    public bool ShareApps
    {
        get => _settings.SyncShareApps;
        set { _settings.SyncShareApps = value; Raise(); Commit(); }
    }

    public ObservableCollection<string> ExcludedApps { get; } = new();
    public bool HasExcludedApps => ExcludedApps.Count > 0;

    public string NewExcludedApp { get => _newExcluded; set => Set(ref _newExcluded, value); }
    public RelayCommand AddExcluded { get; }
    public RelayCommand RemoveExcluded { get; }

    internal static string? NormalizeExe(string? s)
    {
        s = s?.Trim().Trim('"');
        if (string.IsNullOrEmpty(s)) return null;
        s = System.IO.Path.GetFileName(s);
        if (s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || s.Length > 100) return null;
        if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
        return s;
    }

    private void AddExcludedApp()
    {
        if (NormalizeExe(NewExcludedApp) is not { } exe) return;
        NewExcludedApp = "";
        if (ExcludedApps.Contains(exe, StringComparer.OrdinalIgnoreCase)) return;
        ExcludedApps.Add(exe);
        SaveExcluded();
    }

    private void RemoveExcludedApp(string exe)
    {
        if (!ExcludedApps.Remove(exe)) return;
        SaveExcluded();
    }

    private void SaveExcluded()
    {
        _settings.SyncExcludedApps = ExcludedApps.ToList();
        Raise(nameof(HasExcludedApps));
        Commit();
    }

    private void Commit()
    {
        _settings.Save();
        _ = ApplyConfigAsync();
        RefreshExtensionStatus();
    }

    // ---------------- settings: browser extension ----------------

    public bool ExtensionConnected { get => _extensionConnected; private set => Set(ref _extensionConnected, value); }
    public string ExtensionStatus { get => _extensionStatus; private set => Set(ref _extensionStatus, value); }
    public string ExtensionDetail { get => _extensionDetail; private set => Set(ref _extensionDetail, value); }
    public bool ShowExtensionHelp { get => _showExtensionHelp; set => Set(ref _showExtensionHelp, value); }
    public string RegisterStatus { get => _registerStatus; private set => Set(ref _registerStatus, value); }
    public string ExtensionFolder => ExtensionHost.ExtensionDir;
    public string ExtensionId => ExtensionHost.ExtensionId;
    public RelayCommand ToggleExtensionHelp { get; }
    public RelayCommand RegisterExtension { get; }
    public RelayCommand OpenExtensionFolder { get; }
    public RelayCommand CopyExtensionFolder { get; }

    public void RefreshExtensionStatus()
    {
        int tabs = _sync.LocalState?.Tabs.Count ?? 0;
        bool registered = _sync.IsDemo || ExtensionHost.IsRegistered();
        ExtensionConnected = ShareTabs && tabs > 0;
        if (!ShareTabs)
        {
            ExtensionStatus = "Sekme paylaşımı kapalı";
            ExtensionDetail = "Eklenti NitroDrop'a bağlanmaz; diğer bilgisayarlar sekmelerinizi görmez.";
        }
        else if (tabs > 0)
        {
            var browsers = _sync.LocalState!.Tabs.Select(t => t.Browser == BrowserKind.Edge ? "Edge" : "Chrome").Distinct();
            ExtensionStatus = "Bağlı";
            ExtensionDetail = $"{string.Join(" ve ", browsers)} · {tabs} sekme paylaşılıyor";
        }
        else if (registered)
        {
            ExtensionStatus = "Tarayıcıya bağlı, sekme gelmedi";
            ExtensionDetail = "Chrome ya da Edge'de NitroDrop Sekmeler eklentisinin yüklü ve açık olduğundan emin olun.";
        }
        else
        {
            ExtensionStatus = "Kurulu değil";
            ExtensionDetail = "Chrome ve Edge sekmelerinizi diğer bilgisayarlarınızda görmek için eklentiyi kurun.";
        }
    }

    private async Task RegisterExtensionAsync()
    {
        RegisterStatus = "Bağlanıyor…";
        var (ok, msg) = await ExtensionHost.RegisterAsync();
        RegisterStatus = ok ? "✓ " + msg + ". Tarayıcıda eklentiyi yeniden yükleyin." : "Bağlanamadı: " + msg;
        RefreshExtensionStatus();
    }

    // ---------------- settings: iPhone (Kestirme) ----------------

    public ObservableCollection<IosDeviceViewModel> IosDevices { get; } = new();
    public bool HasIosDevices => IosDevices.Count > 0;
    public RelayCommand AddIphone { get; }
    public RelayCommand CloseIosSetup { get; }
    public RelayCommand OpenShortcutHelp { get; }
    public RelayCommand CopyIosUrl { get; }

    public IosSetup? IosSetup
    {
        get => _iosSetup;
        set
        {
            _iosSetup = value;
            _iosQr = value is null ? null : QrImage.Create(value.Url);
            Raise(); Raise(nameof(HasIosSetup)); Raise(nameof(IosQr)); Raise(nameof(IosUrl)); Raise(nameof(IosSetupTitle));
        }
    }
    public bool HasIosSetup => _iosSetup is not null;
    public ImageSource? IosQr => _iosQr;
    public string IosUrl => _iosSetup?.Url ?? "";
    public string IosSetupTitle => _iosSetup is null ? "" : $"{_iosSetup.Name} için kurulum adresi";

    private void AddIphoneDevice()
    {
        var name = AskIphoneName?.Invoke();
        if (string.IsNullOrWhiteSpace(name)) return;
        ShowIosSetup(name.Trim());
    }

    /// <summary>Registers the iPhone and shows its one-time QR (also used by the Debug snapshot).</summary>
    public void ShowIosSetup(string name)
    {
        try { IosSetup = _sync.AddIosDevice(name); }
        catch (Exception e)
        {
            Notify?.Invoke("NitroDrop", "iPhone eklenemedi: " + e.Message, null);
            return;
        }
        RefreshIos();
    }

    private void RefreshIos()
    {
        IosDevices.Clear();
        foreach (var d in _sync.IosDevices)
            IosDevices.Add(new IosDeviceViewModel(d.Id, d.Name, d.CreatedAt, id =>
            {
                _sync.RemoveIosDevice(id);
                if (_iosSetup?.DeviceId == id) IosSetup = null;
                RefreshIos();
            }));
        Raise(nameof(HasIosDevices));
    }

    private static void OpenShortcutDoc()
    {
        var doc = System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "ios-shortcut.md");
        if (System.IO.File.Exists(doc)) Shell(doc);
    }

    // ---------------- helpers ----------------

    internal static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host) : url;

    internal static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d < TimeSpan.FromMinutes(1)) return "az önce";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} dk önce";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} saat önce";
        return t.ToLocalTime().ToString("d MMM HH:mm", new CultureInfo("tr-TR"));
    }

    /// <summary>Opens a URL from the network locally — http/https only.</summary>
    internal static void OpenUrl(string url)
    {
        if (UrlPolicy.TryNormalize(url, out var safe)) Shell(safe);
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Debug.WriteLine(e); }
    }

    internal static void TryCopy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception e) { Debug.WriteLine(e); }
    }
}

public sealed class IosDeviceViewModel(string id, string name, DateTimeOffset created, Action<string> remove)
{
    public string Name => name;
    public string Detail => "Kestirme · eklendi " + created.ToLocalTime().ToString("d MMM yyyy", new CultureInfo("tr-TR"));
    public RelayCommand Remove { get; } = new(_ => remove(id));
}

/// <summary>A paired (or discovered) NitroDrop PC.</summary>
public sealed class SyncPcViewModel : Observable
{
    private const int CollapsedTabs = 6;
    private readonly Func<SyncPcViewModel, Task> _pair, _unpair;
    private SyncPeerInfo? _info;
    private DeviceState? _state;
    private bool _isExpanded = true, _showAllTabs, _isPairing;
    private string? _pairError;
    private string _status = "";

    public SyncPcViewModel(string id, Func<SyncPcViewModel, Task> pair, Func<SyncPcViewModel, Task> unpair)
    {
        Id = id;
        _pair = pair;
        _unpair = unpair;
        Pair = new RelayCommand(async _ => await _pair(this), _ => !IsPairing);
        Unpair = new RelayCommand(async _ => await _unpair(this));
        ToggleExpanded = new RelayCommand(_ => IsExpanded = !IsExpanded);
        ToggleAllTabs = new RelayCommand(_ => { _showAllTabs = !_showAllTabs; RebuildTabs(); });
    }

    public string Id { get; }
    public string Name => _info?.Name ?? Id;
    public bool IsOnline => _info?.IsOnline ?? false;
    public bool IsPaired => _info?.IsPaired ?? false;
    public string StatusText { get => _status; private set => Set(ref _status, value); }

    /// <summary>Last hardware the peer shared (kept while it is offline).</summary>
    public AirDrop.Core.Devices.HostDevice? Hardware { get; private set; }
    /// <summary>"Acer Nitro V 15" / "Windows dizüstü"; null before the peer shared anything.</summary>
    public string? ModelText => Hardware is null ? null : DeviceModels.Describe(DeviceKind.Windows, null, Hardware);
    public string ArtKey => Hardware is null ? DeviceArtKeys.WindowsLaptop : DeviceModels.ArtForHost(Hardware);

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value)) Raise(nameof(ShowContent)); }
    }
    /// <summary>Tabs/apps area visible: expanded, online and sharing something.</summary>
    public bool ShowContent => IsExpanded && IsOnline && (HasTabs || HasApps);
    public bool CanExpand => IsOnline && (HasTabs || HasApps);

    public bool IsPairing
    {
        get => _isPairing;
        set { if (Set(ref _isPairing, value)) Raise(nameof(PairButtonText)); }
    }
    public string PairButtonText => IsPairing ? "Bekleniyor…" : "Eşleştir";
    public string? PairError { get => _pairError; set { if (Set(ref _pairError, value)) Raise(nameof(HasPairError)); } }
    public bool HasPairError => PairError is not null;

    public ObservableCollection<SyncTabViewModel> Tabs { get; } = new();
    public ObservableCollection<SyncAppViewModel> Apps { get; } = new();
    public bool HasTabs => (_state?.Tabs.Count ?? 0) > 0;
    public bool HasApps => Apps.Count > 0;
    public string TabsHeader => $"SEKMELER · {_state?.Tabs.Count ?? 0}";
    public string AppsHeader => $"UYGULAMALAR · {Apps.Count}";
    public bool HasMoreTabs => (_state?.Tabs.Count ?? 0) > CollapsedTabs;
    public string MoreTabsText => _showAllTabs ? "Daha az göster" : $"Tümünü göster ({_state?.Tabs.Count ?? 0})";

    public RelayCommand Pair { get; }
    public RelayCommand Unpair { get; }
    public RelayCommand ToggleExpanded { get; }
    public RelayCommand ToggleAllTabs { get; }

    public void Update(SyncPeerInfo p)
    {
        bool stateChanged = !ReferenceEquals(p.State, _state) &&
                            (p.State?.UpdatedAt != _state?.UpdatedAt || p.State?.ComputeETag() != _state?.ComputeETag());
        _info = p;
        if (p.State?.Hardware is { } hw && hw != Hardware)
        {
            Hardware = hw;
            Raise(nameof(Hardware)); Raise(nameof(ModelText)); Raise(nameof(ArtKey));
        }
        if (stateChanged)
        {
            _state = p.State;
            RebuildTabs();
            Apps.Clear();
            foreach (var a in _state?.Apps ?? Array.Empty<AppInfo>()) Apps.Add(new SyncAppViewModel(a));
        }
        Raise(nameof(Name)); Raise(nameof(IsOnline)); Raise(nameof(IsPaired));
        Raise(nameof(HasTabs)); Raise(nameof(HasApps)); Raise(nameof(TabsHeader)); Raise(nameof(AppsHeader));
        Raise(nameof(ShowContent)); Raise(nameof(CanExpand));
        RefreshStatus();
    }

    private void RebuildTabs()
    {
        Tabs.Clear();
        var all = (_state?.Tabs ?? Array.Empty<BrowserTab>()).OrderByDescending(t => t.Active).ThenByDescending(t => t.LastAccessed);
        foreach (var t in _showAllTabs ? all : all.Take(CollapsedTabs)) Tabs.Add(new SyncTabViewModel(t));
        Raise(nameof(HasMoreTabs)); Raise(nameof(MoreTabsText));
    }

    public void RefreshStatus()
    {
        if (_info is null) return;
        if (!IsPaired) { StatusText = "NitroDrop · aynı ağda"; return; }
        if (!IsOnline)
        {
            StatusText = _info.LastSeen is { } seen ? $"Çevrim dışı · son görülme {DevicesViewModel.Ago(seen)}" : "Çevrim dışı";
            return;
        }
        if (_state is null)
        {
            StatusText = _info.LastError is { } err ? $"Bağlanılamıyor · {err}" : "Çevrim içi · bağlanıyor…";
            return;
        }
        var parts = new List<string> { "Çevrim içi" };
        if (_state.Tabs.Count > 0) parts.Add($"{_state.Tabs.Count} sekme");
        if (_state.Apps.Count > 0) parts.Add($"{_state.Apps.Count} uygulama");
        if (parts.Count == 1) parts.Add("paylaşılan bir şey yok");
        if (Hardware?.DisplayName is { } model) parts.Insert(1, model);
        StatusText = string.Join(" · ", parts);
    }
}

public sealed class SyncTabViewModel : Observable
{
    private static readonly Dictionary<string, ImageSource?> FaviconCache = new();
    private readonly BrowserTab _t;
    private ImageSource? _favicon;

    public SyncTabViewModel(BrowserTab t)
    {
        _t = t;
        Domain = DevicesViewModel.Host(t.Url);
        Open = new RelayCommand(_ => DevicesViewModel.OpenUrl(_t.Url));
        CopyLink = new RelayCommand(_ => DevicesViewModel.TryCopy(_t.Url));
        LoadFavicon(t.FaviconUrl);
    }

    public string Title => string.IsNullOrWhiteSpace(_t.Title) ? Domain : _t.Title;
    public string Domain { get; }
    public string Url => _t.Url;
    public string Subtitle => Domain + (_t.Browser == BrowserKind.Edge ? " · Edge" : "");
    public string Initial => Domain.Length > 0 ? Domain[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
    public Brush Tile => Palette.For(Domain);
    public bool IsActive => _t.Active;
    public ImageSource? Favicon { get => _favicon; private set { if (Set(ref _favicon, value)) Raise(nameof(HasFavicon)); } }
    public bool HasFavicon => Favicon is not null;
    public RelayCommand Open { get; }
    public RelayCommand CopyLink { get; }

    /// <summary>https or data: favicons only; anything else keeps the letter tile.</summary>
    private void LoadFavicon(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 64 * 1024) return;
        if (FaviconCache.TryGetValue(url, out var cached)) { Favicon = cached; return; }
        try
        {
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && url.IndexOf(";base64,", StringComparison.Ordinal) is var i and > 0)
            {
                Favicon = FaviconCache[url] = ImageUtil.FromBytes(Convert.FromBase64String(url[(i + 8)..]), 32);
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = u;
            bi.DecodePixelWidth = 32;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.EndInit();
            if (bi.IsDownloading)
            {
                bi.DownloadCompleted += (_, _) => { FaviconCache[url] = bi; Favicon = bi; };
                bi.DownloadFailed += (_, _) => FaviconCache[url] = null;
            }
            else Favicon = FaviconCache[url] = bi;
        }
        catch (Exception) { FaviconCache[url] = null; }
    }
}

public sealed class SyncAppViewModel
{
    private static readonly Dictionary<string, string?> LocalCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _localPath;

    public SyncAppViewModel(AppInfo a)
    {
        Name = string.IsNullOrWhiteSpace(a.Name) ? a.Exe : a.Name;
        Detail = string.IsNullOrWhiteSpace(a.WindowTitle) || a.WindowTitle == Name ? a.Exe : a.WindowTitle!;
        Initial = Name.Length > 0 ? Name[..1].ToUpper(CultureInfo.CurrentCulture) : "?";
        Tile = Palette.For(a.Exe);
        if (a.IconPng is { Length: > 0 } png)
        {
            try { Icon = ImageUtil.FromBytes(Convert.FromBase64String(png), 32); }
            catch (FormatException) { }
        }
        if (!LocalCache.TryGetValue(a.Exe, out _localPath)) LocalCache[a.Exe] = _localPath = LocalApps.Find(a.Exe);
        OpenHere = new RelayCommand(_ => { if (_localPath is not null) LocalApps.Launch(_localPath); });
    }

    public string Name { get; }
    public string Detail { get; }
    public string Initial { get; }
    public Brush Tile { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    /// <summary>"Bu bilgisayarda aç" only when the same program exists here.</summary>
    public bool CanOpenHere => _localPath is not null;
    public RelayCommand OpenHere { get; }
}

/// <summary>
/// Stable muted tint per key for letter tiles (favicon / app icon fallback). Low-saturation graphite-family mid-tones,
/// all ≥ 6:1 with the white initial (OnTile), readable on both themes; no rainbow.
/// </summary>
internal static class Palette
{
    private static readonly Brush[] Colors =
        new[] { "#5B606A", "#6A5F57", "#56645E", "#5E5A6B", "#6B5A55", "#4F5D6B", "#62605A" }
            .Select(c => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return (Brush)b; })
            .ToArray();

    public static Brush For(string key)
    {
        uint h = 2166136261;
        foreach (char c in key.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return Colors[h % (uint)Colors.Length];
    }
}
