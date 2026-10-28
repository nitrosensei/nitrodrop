using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AirDrop.App.Services;
using Nitro.Sync;

namespace AirDrop.App.ViewModels;

public enum MainTab { Nearby, Devices, Phone, History, Settings }

public sealed class MainViewModel : Observable
{
    public const double RadarSize = 480;
    private const double RingRadius = 166;
    private static readonly TimeSpan EveryoneWindow = TimeSpan.FromMinutes(10);

    private readonly IAirDropService _svc;
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly IContactResolver _contacts;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _countdown;

    private ReceiveMode _mode = ReceiveMode.Everyone;
    private DateTime _everyoneUntil;
    private MainTab _tab;
    private double _receiveProgress;
    private bool _isReceiving;
    private string _receiveTitle = "", _receiveDetail = "";
    private IReadOnlyList<string> _pendingFiles = Array.Empty<string>();

    /// <summary>Prefix of AirDrop peers offered to the browser extension ("Telefona gönder").</summary>
    public const string AirDropTargetPrefix = "airdrop:";

    private volatile IReadOnlyList<LinkTarget> _linkTargets = Array.Empty<LinkTarget>();

    public MainViewModel(IAirDropService svc, ISyncSource sync, AppSettings settings, HistoryStore history,
        IContactResolver contacts, Dispatcher ui)
    {
        _svc = svc;
        _settings = settings;
        _history = history;
        _contacts = contacts;
        _ui = ui;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => TickCountdown();

        _svc.PeerFound += (_, p) => _ui.Invoke(() => AddPeer(p));
        _svc.PeerLost += (_, id) => _ui.Invoke(() => RemovePeer(id));
        _svc.ReceiveProgress += (_, p) => _ui.Invoke(() => OnReceiveProgress(p));
        _svc.Received += (_, b) => _ui.Invoke(() => OnReceived(b));
        _svc.AskHandler = req => _ui.InvokeAsync(() => OnAsk(req)).Task.Unwrap();
        _svc.HardwareStatusChanged += (_, _) => _ui.InvokeAsync(() => { Raise(nameof(IsDemo)); Raise(nameof(HardwareStatus)); });

        Settings = new SettingsViewModel(settings, contacts, ApplySettingsAsync);
        Devices = new DevicesViewModel(sync, settings, ui) { AddHistory = AddHistory };
        // The extension's "Telefona gönder" can deliver through AirDrop (iPhones, iPads, Macs nearby).
        sync.ExternalLinkTargets = () => _linkTargets;
        sync.SendLinkToExternal = SendExternalLinkAsync;
        foreach (var e in history.Entries) History.Add(new HistoryItemViewModel(e));

        OpenReceiveFolder = new RelayCommand(_ => Shell(_settings.ReceiveFolder));
        ShowTab = new RelayCommand(p => Tab = Enum.Parse<MainTab>((string)p!));
        ClearHistory = new RelayCommand(_ => { _history.Clear(); History.Clear(); Raise(nameof(HasHistory)); },
            _ => History.Count > 0);
        CancelPending = new RelayCommand(_ => PendingFiles = Array.Empty<string>());
    }

    // ---- bindable state ----

    public ObservableCollection<PeerViewModel> Peers { get; } = new();
    public ObservableCollection<HistoryItemViewModel> History { get; } = new();
    public SettingsViewModel Settings { get; }
    /// <summary>Nitro.Sync: paired PCs, their tabs/apps, iPhone activity ("Cihazlar" + Ayarlar → Senkron).</summary>
    public DevicesViewModel Devices { get; }
    /// <summary>Wi-Fi driver state: "kurulum gerekli" banner + Settings → Wi-Fi Sürücüsü.</summary>
    public Setup.DriverSetupStatus DriverSetup { get; } = new(new Setup.SystemProbe());

    public bool HasPeers => Peers.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool IsDemo => !_svc.IsHardwareReady;
    public string HardwareStatus => _svc.HardwareStatus;
    public string ComputerName => _settings.DeviceName;

    public MainTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            Raise(nameof(IsNearby)); Raise(nameof(IsDevices)); Raise(nameof(IsPhone)); Raise(nameof(IsHistory)); Raise(nameof(IsSettings));
            if (value == MainTab.Settings) { Settings.RefreshTrusted(); Devices.RefreshExtensionStatus(); }
        }
    }
    public bool IsNearby { get => Tab == MainTab.Nearby; set { if (value) Tab = MainTab.Nearby; } }
    public bool IsDevices { get => Tab == MainTab.Devices; set { if (value) Tab = MainTab.Devices; } }
    public bool IsHistory { get => Tab == MainTab.History; set { if (value) Tab = MainTab.History; } }
    public bool IsSettings => Tab == MainTab.Settings;
    public bool IsPhone { get => Tab == MainTab.Phone; set { if (value) Tab = MainTab.Phone; } }

    /// <summary>"Telefon" tab (iPhone continuity); set by App from ContinuityHost.</summary>
    public PhoneViewModel? Phone { get; set; }

    /// <summary>Files handed over by Explorer ("NitroDrop ile gönder") waiting for a recipient.</summary>
    public IReadOnlyList<string> PendingFiles
    {
        get => _pendingFiles;
        set
        {
            _pendingFiles = value;
            Raise(); Raise(nameof(HasPending)); Raise(nameof(PendingText));
        }
    }
    public bool HasPending => PendingFiles.Count > 0;
    public string PendingText => PendingFiles.Count == 1
        ? $"“{Path.GetFileName(PendingFiles[0])}” göndermeye hazır — bir cihaz seçin"
        : $"{PendingFiles.Count} öge göndermeye hazır — bir cihaz seçin";

    public IReadOnlyList<ModeOption> Modes { get; } = new[]
    {
        new ModeOption(ReceiveMode.Off, "Alma Kapalı", true),
        new ModeOption(ReceiveMode.ContactsOnly, "Yalnızca Kişiler", false),
        new ModeOption(ReceiveMode.Everyone, "Herkes (10 dakika)", true),
    };

    public ModeOption SelectedMode
    {
        get => Modes.First(m => m.Mode == _mode);
        set
        {
            if (value is null || !value.Enabled) return;
            if (value.Mode == _mode && _mode != ReceiveMode.Everyone) return;
            _mode = value.Mode;
            if (_mode == ReceiveMode.Everyone) ArmEveryone();
            else _countdown.Stop();
            _ = _svc.SetModeAsync(_mode);
            Raise();
            Raise(nameof(VisibilityText));
        }
    }

    public string VisibilityText => _mode switch
    {
        ReceiveMode.Off => "Kimse sizi bulamaz",
        ReceiveMode.Everyone => $"Herkes sizi bulabilir · {Remaining():m\\:ss}",
        _ => "",
    };

    public bool IsReceiving { get => _isReceiving; private set => Set(ref _isReceiving, value); }
    public double ReceiveProgress { get => _receiveProgress; private set => Set(ref _receiveProgress, value); }
    public string ReceiveTitle { get => _receiveTitle; private set => Set(ref _receiveTitle, value); }
    public string ReceiveDetail { get => _receiveDetail; private set => Set(ref _receiveDetail, value); }

    public RelayCommand OpenReceiveFolder { get; }
    public RelayCommand ShowTab { get; }
    public RelayCommand ClearHistory { get; }
    public RelayCommand CancelPending { get; }

    /// <summary>View hooks.</summary>
    public Func<IncomingRequest, ContactMatch?, System.Windows.Media.ImageSource?, Task<bool>>? ShowAskSheet { get; set; }
    public Action<string, string, Action?>? Notify
    {
        get => _notify;
        set { _notify = value; Devices.Notify = value; }
    }
    private Action<string, string, Action?>? _notify;

    // ---- lifecycle ----

    public async Task StartAsync()
    {
        ArmEveryone();
        var sync = Devices.StartAsync();
        await _svc.StartAsync(_settings.ToServiceOptions(), _mode);
        await sync;
    }

    private async Task ApplySettingsAsync()
    {
        Raise(nameof(ComputerName));
        foreach (var p in Peers) RefreshIdentity(p);
        await _svc.UpdateOptionsAsync(_settings.ToServiceOptions());
        if (_svc is CoreAirDropService core) await core.SetRadioEnabledAsync(_settings.DirectIphoneConnection);
        await Devices.ApplyConfigAsync();   // no-op unless the device name changed
    }

    // ---- sending ----

    public async Task SendAsync(PeerViewModel peer, IReadOnlyList<string> files)
    {
        if (peer.IsBusy || files.Count == 0) return;
        peer.Progress = 0;
        peer.State = PeerState.Waiting;
        var progress = new Progress<TransferProgress>(peer.ReportProgress);
        SendOutcome result;
        try { result = await _svc.SendAsync(peer.Id, files, progress); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            result = SendOutcome.Failed;
        }
        FinishSend(peer, result, files, null);
    }

    public async Task SendLinkAsync(PeerViewModel peer, Uri link)
    {
        if (peer.IsBusy) return;
        peer.State = PeerState.Waiting;
        SendOutcome result;
        try { result = await _svc.SendLinkAsync(peer.Id, link); }
        catch (Exception) { result = SendOutcome.Failed; }
        FinishSend(peer, result, Array.Empty<string>(), link);
    }

    /// <summary>Extension → "Telefona gönder" → an AirDrop peer (called on a background thread).</summary>
    private Task<bool> SendExternalLinkAsync(string targetId, string url, string? title, CancellationToken ct)
    {
        if (!targetId.StartsWith(AirDropTargetPrefix, StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Task.FromResult(false);
        var peerId = targetId[AirDropTargetPrefix.Length..];
        return _ui.InvokeAsync(async () =>
        {
            var peer = Peers.FirstOrDefault(p => p.Id == peerId);
            if (peer is null || peer.IsBusy) return false;
            await SendLinkAsync(peer, uri);
            return peer.State == PeerState.Sent;
        }).Task.Unwrap();
    }

    private void UpdateLinkTargets() =>
        _linkTargets = Peers.Select(p => new LinkTarget(AirDropTargetPrefix + p.Id, p.Name,
            p.Kind is DeviceKind.iPhone or DeviceKind.iPad ? "phone" : "pc")).ToList();

    /// <summary>Sends whatever is on the Windows clipboard (text, link, image or copied files).</summary>
    public async Task SendClipboardAsync(PeerViewModel peer)
    {
        if (peer.IsBusy) return;
        var payload = ClipboardBridge.Read();
        if (payload is null)
        {
            Notify?.Invoke("NitroDrop", "Pano boş — önce bir şey kopyalayın.", null);
            return;
        }
        _settings.ClipboardTargetKey = peer.StableKey;
        _settings.Save();
        if (payload.Link is not null) await SendLinkAsync(peer, payload.Link);
        else await SendAsync(peer, payload.Files);
    }

    /// <summary>Ctrl+Alt+A: clipboard → last used device, else the only trusted / visible one.</summary>
    public async Task<bool> SendClipboardToDefaultAsync()
    {
        var target = Peers.FirstOrDefault(p => p.StableKey == _settings.ClipboardTargetKey)
                     ?? (Peers.Count(p => p.IsTrusted) == 1 ? Peers.First(p => p.IsTrusted) : null)
                     ?? (Peers.Count == 1 ? Peers[0] : null);
        if (target is null)
        {
            var payload = ClipboardBridge.Read();
            if (payload is { Files.Count: > 0 }) PendingFiles = payload.Files;
            return false;   // caller shows the window so the user can pick
        }
        Notify?.Invoke("NitroDrop", $"Pano {target.DisplayName} cihazına gönderiliyor…", null);
        await SendClipboardAsync(target);
        return true;
    }

    /// <summary>Sends files that came from Explorer to the chosen peer.</summary>
    public async Task<bool> TrySendPendingAsync(PeerViewModel peer)
    {
        if (!HasPending) return false;
        var files = PendingFiles;
        PendingFiles = Array.Empty<string>();
        await SendAsync(peer, files);
        return true;
    }

    private async void FinishSend(PeerViewModel peer, SendOutcome result, IReadOnlyList<string> files, Uri? link)
    {
        peer.State = result switch
        {
            SendOutcome.Delivered => PeerState.Sent,
            SendOutcome.Declined => PeerState.Declined,
            SendOutcome.Cancelled => PeerState.Idle,
            _ => PeerState.Failed,
        };
        if (result != SendOutcome.Cancelled)
        {
            AddHistory(new HistoryEntry
            {
                Direction = TransferDirection.Sent,
                DeviceName = peer.DisplayName,
                DeviceKey = peer.StableKey,
                DeviceKind = peer.Kind,
                ModelName = peer.Info.ModelName,
                Host = peer.Info.Host,
                Paths = files.ToList(),
                Links = link is null ? new() : new() { link.ToString() },
                TotalBytes = files.Sum(SafeSize),
                Succeeded = result == SendOutcome.Delivered,
                Note = result == SendOutcome.Declined ? "reddedildi" : result == SendOutcome.Failed ? "başarısız" : null,
            });
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!peer.IsBusy)
        {
            peer.State = PeerState.Idle;
            peer.Progress = 0;
        }
    }

    // ---- receiving ----

    private async Task<bool> OnAsk(IncomingRequest req)
    {
        var prefs = _settings.Devices.GetValueOrDefault(req.SenderKey);
        var contact = _contacts.Match(req.SenderContactHashes);
        string who = prefs?.DisplayName ?? contact?.Name ?? req.SenderName;
        bool linksOnly = req.Files.Count == 0 && req.Links.Count > 0;

        bool ok;
        if (prefs?.Trusted == true)
        {
            ok = true;   // trusted device: accept silently, like your own devices on Apple
            if (!linksOnly) Notify?.Invoke("NitroDrop", $"{who} cihazından alınıyor…", null);
        }
        else
        {
            if (ShowAskSheet is null) return false;
            var photo = ImageUtil.FromFile(prefs?.PhotoPath) ?? ImageUtil.FromBytes(contact?.Photo);
            ok = await ShowAskSheet(req, contact is null && prefs?.DisplayName is null ? null : new ContactMatch(who, null), photo);
        }

        if (ok && !linksOnly)
        {
            ReceiveTitle = $"{who} cihazından alınıyor";
            ReceiveDetail = "";
            ReceiveProgress = 0;
            IsReceiving = true;
        }
        return ok;
    }

    private void OnReceiveProgress(TransferProgress p)
    {
        IsReceiving = true;
        if (p.BytesTotal > 0)
        {
            ReceiveProgress = p.Fraction;
            ReceiveDetail = $"{Format.Size(p.BytesDone)} / {Format.Size(p.BytesTotal)} · {Format.Speed(p)}";
        }
        else
        {
            // Total unknown (iOS doesn't announce sizes): show amount + speed, keep the bar moving gently.
            ReceiveProgress = Math.Min(0.95, ReceiveProgress + 0.01);
            ReceiveDetail = $"{Format.Size(p.BytesDone)} alındı · {(p.BytesPerSecond / 1048576.0).ToString("0.0", new System.Globalization.CultureInfo("tr-TR"))} MB/sn";
        }
    }

    private async void OnReceived(ReceivedBatch b)
    {
        var prefs = _settings.Devices.GetValueOrDefault(b.SenderKey);
        string who = prefs?.DisplayName ?? b.SenderName;

        string? clip = _settings.CopyIncomingToClipboard ? ClipboardBridge.Write(b) : null;

        if (b.Links.Count > 0 && _settings.OpenLinksAutomatically && _svc is not DemoAirDropService)
            foreach (var l in b.Links) Shell(l.ToString());

        var sender = Peers.FirstOrDefault(p => p.StableKey == b.SenderKey);
        string? senderModel = sender?.Info.ModelName ?? b.SenderKey.Split('|').ElementAtOrDefault(1);
        AddHistory(new HistoryEntry
        {
            Direction = TransferDirection.Received,
            DeviceName = who,
            DeviceKey = b.SenderKey,
            DeviceKind = sender?.Kind ?? prefs?.Kind ?? CoreAirDropService.KindOf(senderModel),
            ModelName = senderModel,
            Host = sender?.Info.Host,
            Paths = b.Paths.ToList(),
            Links = b.Links.Select(l => l.ToString()).ToList(),
            TotalBytes = b.TotalBytes,
            Note = b.Paths.Count == 0 && b.Links.Count == 0 ? "demo — dosya yazılmadı" : null,
        });

        if (b.Paths.Count == 0 && b.Links.Count > 0)
        {
            var msg = clip is null ? $"{who} bir bağlantı paylaştı" : $"{who}: {clip}";
            Notify?.Invoke("NitroDrop", msg, () => Shell(b.Links[0].ToString()));
            return;
        }

        ReceiveProgress = 1;
        ReceiveTitle = clip is not null ? clip   // text / single image: the clipboard is the destination
            : b.Paths.Count > 0 ? $"{b.Paths.Count} öge alındı" : "Alındı (demo — dosya yazılmadı)";
        ReceiveDetail = b.Paths.Count > 0 ? Path.GetFileName(_settings.ReceiveFolder) + " klasörüne kaydedildi" : "";
        Notify?.Invoke("NitroDrop", $"{who}: {ReceiveTitle}", () =>
        {
            var first = b.Paths.FirstOrDefault();
            if (first is not null) Process.Start("explorer.exe", $"/select,\"{first}\"");
            else Shell(_settings.ReceiveFolder);
        });
        await Task.Delay(TimeSpan.FromSeconds(5));
        IsReceiving = false;
    }

    // ---- peers ----

    private void AddPeer(PeerInfo p)
    {
        var existing = Peers.FirstOrDefault(x => x.Id == p.Id);
        if (existing is not null)
        {
            existing.Update(p);
            RefreshIdentity(existing);
            return;
        }
        var vm = new PeerViewModel(p);
        RefreshIdentity(vm);
        var prefs = _settings.For(p.StableKey);
        prefs.LastSeenName = p.Name;
        prefs.Kind = p.Kind;
        Peers.Add(vm);
        Layout();
    }

    private void RemovePeer(string id)
    {
        var p = Peers.FirstOrDefault(x => x.Id == id);
        if (p is null || p.IsBusy) return;
        Peers.Remove(p);
        Layout();
    }

    private void RefreshIdentity(PeerViewModel p) =>
        p.ApplyIdentity(_settings.Devices.GetValueOrDefault(p.StableKey), _contacts.Match(p.Info.ContactHashes));

    public void SetPeerPhoto(PeerViewModel p, string? sourcePath)
    {
        var prefs = _settings.For(p.StableKey);
        prefs.PhotoPath = sourcePath is null ? null : AppSettings.ImportPhoto(sourcePath);
        _settings.Save();
        RefreshIdentity(p);
    }

    public void SetPeerAlias(PeerViewModel p, string? alias)
    {
        _settings.For(p.StableKey).DisplayName = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        _settings.Save();
        RefreshIdentity(p);
    }

    public void ToggleTrusted(PeerViewModel p)
    {
        var prefs = _settings.For(p.StableKey);
        prefs.Trusted = !prefs.Trusted;
        _settings.Save();
        RefreshIdentity(p);
    }

    /// <summary>Spread peers on a ring around the centre, like Finder's AirDrop view.</summary>
    private void Layout()
    {
        int n = Peers.Count;
        double c = RadarSize / 2;
        for (int i = 0; i < n; i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(n, 1);
            Peers[i].X = c + RingRadius * Math.Cos(a) - PeerViewModel.ItemWidth / 2;
            Peers[i].Y = c + RingRadius * Math.Sin(a) - PeerViewModel.Size / 2;
        }
        Raise(nameof(HasPeers));
        UpdateLinkTargets();
    }

    // ---- helpers ----

    private void AddHistory(HistoryEntry e)
    {
        _history.Add(e);
        History.Insert(0, new HistoryItemViewModel(e));
        Raise(nameof(HasHistory));
    }

#if DEBUG
    /// <summary>Snapshot only: shows (or with null, removes) sample rows without writing them to history.json.</summary>
    internal void PreviewHistory(IReadOnlyList<HistoryEntry>? entries)
    {
        if (entries is null) { History.Clear(); foreach (var e in _history.Entries) History.Add(new HistoryItemViewModel(e)); }
        else foreach (var e in entries) History.Insert(0, new HistoryItemViewModel(e));
        Raise(nameof(HasHistory));
    }
#endif

    private static long SafeSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
                return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception) { }
        return 0;
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void ArmEveryone()
    {
        _everyoneUntil = DateTime.UtcNow + EveryoneWindow;
        _countdown.Start();
        Raise(nameof(VisibilityText));
    }

    private TimeSpan Remaining()
    {
        var r = _everyoneUntil - DateTime.UtcNow;
        return r < TimeSpan.Zero ? TimeSpan.Zero : r;
    }

    private void TickCountdown()
    {
        if (Remaining() == TimeSpan.Zero)
        {
            SelectedMode = Modes.First(m => m.Mode == ReceiveMode.Off);   // same as iOS
            return;
        }
        Raise(nameof(VisibilityText));
    }
}

public sealed record ModeOption(ReceiveMode Mode, string Title, bool Enabled)
{
    public string Hint => Enabled ? "" : "Apple hesabı gerektirir";
}
