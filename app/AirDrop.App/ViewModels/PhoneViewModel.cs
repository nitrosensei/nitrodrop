using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AirDrop.App.Services;
using Nitro.Continuity.Ancs;
using Nitro.Continuity.Runtime;
using Nitro.Sync;
using Nitro.Sync.Model;

namespace AirDrop.App.ViewModels;

// No WPF types in this file (ICommand is System.ObjectModel): it is linked into AirDrop.Tests.

public abstract class PhoneObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>ICommand without WPF's CommandManager (so it compiles on plain net10.0).</summary>
public sealed class DelegateCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) { if (CanExecute(p)) run(p); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum PhoneSection { Messages, Notifications, Calls }

public enum PairStep { Guide, Searching, Pick, Confirm, EnterPin, Busy, Success, Error }

public static class PhoneFormat
{
    public static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string When(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        if (d < TimeSpan.FromMinutes(1)) return "şimdi";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} dk önce";
        if (t.Date == now.Date) return t.ToString("HH:mm", Tr);
        if (t.Date == now.Date.AddDays(-1)) return "Dün";
        if (d < TimeSpan.FromDays(7)) return t.ToString("dddd", Tr);
        return t.ToString("d MMM", Tr);
    }

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", Tr) : d.ToString(@"mm\:ss", Tr);

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        if (name.Any(char.IsAsciiDigit) && !name.Any(char.IsLetter)) return "";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[^1][0]}" : parts[0][..1];
        return s.ToUpper(Tr);
    }
}

/// <summary>App badge (colour + Segoe Fluent glyph) per iOS bundle id — we have no real app icons over ANCS.</summary>
public static class PhoneAppVisuals
{
    public static (string Color, string Glyph) For(string? bundleId) => bundleId switch
    {
        "net.whatsapp.WhatsApp" or "net.whatsapp.WhatsAppSMB" => ("#25D366", ""),
        "com.apple.MobileSMS" => ("#34C759", ""),
        "com.apple.mobilephone" => ("#34C759", ""),
        "com.burbn.instagram" => ("#E1306C", ""),
        "com.apple.mobilecal" => ("#FF3B30", ""),
        "com.apple.mobilemail" or "com.google.Gmail" or "com.microsoft.Office.Outlook" => ("#0A84FF", ""),
        "ph.telegra.Telegraph" => ("#2AABEE", ""),
        "com.spotify.client" => ("#1DB954", ""),
        "com.google.ios.youtube" => ("#FF0000", ""),
        "com.atebits.Tweetie2" => ("#1C1C1E", ""),
        "com.facebook.Messenger" => ("#0084FF", ""),
        "com.apple.reminders" => ("#FF9500", ""),
        "com.apple.Maps" or "com.google.Maps" => ("#30B0C7", ""),
        _ => ("#8E8E93", ""),
    };
}

public sealed class NotificationItem : PhoneObservable
{
    public required uint Uid { get; init; }
    public string? AppId { get; set; }
    public string AppName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public AncsCategoryId Category { get; set; }
    public string? PositiveLabel { get; set; }
    public string? NegativeLabel { get; set; }
    public bool IsPreExisting { get; set; }
    public ContinuationTarget? Continuation { get; set; }
    public string AppColor => PhoneAppVisuals.For(AppId).Color;
    public string AppGlyph => PhoneAppVisuals.For(AppId).Glyph;
    public bool HasPositive => !string.IsNullOrEmpty(PositiveLabel);
    public bool HasNegative => !string.IsNullOrEmpty(NegativeLabel);
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool IsMessagesApp => AppId == "com.apple.MobileSMS";
    public bool CanContinue => IsMessagesApp || Continuation is not null;
    public string ContinueLabel => IsMessagesApp ? "Mesajlarda aç" : Continuation is { } c ? $"{c.DisplayName} ile aç" : "";
    private string _timeText = "";
    public string TimeText { get => _timeText; set => Set(ref _timeText, value); }

    public void RaiseAll() => Raise(string.Empty);
}

public sealed class MessageBubble : PhoneObservable
{
    private PhoneMessage _m;
    public MessageBubble(PhoneMessage m) => _m = m;
    public PhoneMessage Message { get => _m; set { _m = value; Raise(string.Empty); } }
    public string Handle => _m.Handle;
    public string Body => _m.Body;
    public bool Outgoing => _m.Outgoing;
    public DateTimeOffset Time => _m.Time;
    public string TimeText => _m.Time.ToString("HH:mm", PhoneFormat.Tr);
    public string? StatusText => _m.Status switch
    {
        MessageStatus.Sending => "Gönderiliyor…",
        MessageStatus.Failed => "Gönderilemedi",
        _ => null,
    };
    public bool Failed => _m.Status == MessageStatus.Failed;
}

public sealed class ConversationItem : PhoneObservable
{
    public required string Key { get; init; }
    public string Address { get; set; } = "";
    private string _name = "";
    public string DisplayName { get => _name; set { if (Set(ref _name, value)) Raise(nameof(Initials)); } }
    public string Initials => PhoneFormat.Initials(DisplayName);
    public byte[]? Photo { get; set; }
    public ObservableCollection<MessageBubble> Messages { get; } = new();
    private int _unread;
    public int Unread { get => _unread; set { if (Set(ref _unread, value)) Raise(nameof(HasUnread)); } }
    public bool HasUnread => Unread > 0;
    private string _timeText = "";
    public string LastTimeText { get => _timeText; set => Set(ref _timeText, value); }
    public string LastText => Messages.Count == 0 ? "" : (Messages[^1].Outgoing ? "Siz: " : "") + Messages[^1].Body;
    public DateTimeOffset LastTime => Messages.Count == 0 ? DateTimeOffset.MinValue : Messages[^1].Time;
    /// <summary>Alphanumeric senders (banks, 2FA) cannot be replied to.</summary>
    public bool CanReply => Address.Any(char.IsAsciiDigit) && !Address.Any(char.IsLetter);
    public void Touch() { Raise(nameof(LastText)); Raise(nameof(LastTime)); }
}

public sealed class RecentCallItem
{
    public required RecentCall Call { get; init; }
    public required string DisplayName { get; init; }
    public string? Number { get; init; }
    public string TimeText { get; set; } = "";
    public bool IsMissed => Call.Outcome == CallOutcome.Missed;
    public string Initials => PhoneFormat.Initials(DisplayName);
    public string Glyph => Call.Outcome switch
    {
        CallOutcome.Outgoing => "",
        CallOutcome.Missed => "",
        CallOutcome.Declined => "",
        _ => "",
    };
    public string OutcomeText => Call.Outcome switch
    {
        CallOutcome.Missed => "Cevapsız arama",
        CallOutcome.Declined => "Reddedildi",
        CallOutcome.Outgoing => Call.Duration > TimeSpan.Zero ? $"Giden · {PhoneFormat.Duration(Call.Duration)}" : "Giden",
        _ => Call.Duration > TimeSpan.Zero ? $"Gelen · {PhoneFormat.Duration(Call.Duration)}" : "Gelen",
    };
}

public sealed record IosToggle(string Name, string Explains, string Glyph);
public sealed record GuideStep(int Number, string Title, string Body);

/// <summary>
/// The "Telefon" tab: connection status + pairing flow, notifications (with "continue on PC"), MAP conversations with
/// reply, recent calls, now playing; raises <see cref="IncomingCall"/> / <see cref="ToastRequested"/> for the windows.
/// All service events are marshalled with the <c>post</c> delegate (the WPF dispatcher in the app, inline in tests).
/// </summary>
public sealed class PhoneViewModel : PhoneObservable, IPairingPrompt, IAsyncDisposable
{
    private readonly IContinuityService _svc;
    private readonly IPhoneContacts _contacts;
    private readonly Action<Action> _post;
    private readonly IUrlOpener? _opener;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, CallViewModel> _calls = new();
    private TaskCompletionSource<bool>? _confirm;
    private TaskCompletionSource<string?>? _pin;
    private CancellationTokenSource? _pairCts;

    public PhoneViewModel(IContinuityService svc, IPhoneContacts contacts, Action<Action> post, IUrlOpener? opener = null,
        PhoneSettings? settings = null, Func<DateTimeOffset>? clock = null)
    {
        _svc = svc;
        _contacts = contacts;
        _post = post;
        _opener = opener;
        _now = clock ?? (() => DateTimeOffset.Now);
        Settings = settings ?? new PhoneSettings();

        _svc.StatusChanged += s => _post(() => ApplyStatus(s));
        _svc.CallCapabilityChanged += c => _post(() => CallCapability = c);
        _svc.NotificationAdded += n => _post(() => OnNotification(n, added: true));
        _svc.NotificationModified += n => _post(() => OnNotification(n, added: false));
        _svc.NotificationRemoved += n => _post(() => OnNotificationRemoved(n));
        _svc.CallUpdated += c => _post(() => OnCall(c));
        _svc.CallLogged += c => _post(() => AddRecent(c));
        _svc.NowPlayingChanged += np => _post(() => NowPlaying = np);
        _svc.Messages.Added += m => _post(() => OnMessage(m.Handle, m));
        _svc.Messages.Changed += (old, m) => _post(() => OnMessage(old, m));
        _svc.Messages.Removed += m => _post(() => OnMessageRemoved(m));

        ShowSection = new DelegateCommand(p => { if (Enum.TryParse<PhoneSection>(p as string, out var s)) Section = s; });
        StartPairing = new DelegateCommand(_ => _ = StartPairingAsync());
        PairDevice = new DelegateCommand(p => { if (p is PhoneDevice d) _ = PairAsync(d); });
        ConfirmCode = new DelegateCommand(_ => _confirm?.TrySetResult(true));
        RejectCode = new DelegateCommand(_ => _confirm?.TrySetResult(false));
        SubmitPin = new DelegateCommand(_ => _pin?.TrySetResult(PinInput));
        CancelPairing = new DelegateCommand(_ => CancelPair());
        EstablishLe = new DelegateCommand(_ => _ = EstablishLeAsync());
        Disconnect = new DelegateCommand(_ => _ = _svc.DisconnectAsync());
        Retry = new DelegateCommand(_ => { if (_svc.Device is { } d) _ = _svc.UseDeviceAsync(d); });
        SyncContacts = new DelegateCommand(_ => _ = SyncContactsAsync());
        NotificationPositive = new DelegateCommand(p => { if (p is NotificationItem n) _ = _svc.PerformNotificationActionAsync(n.Uid, true); });
        NotificationNegative = new DelegateCommand(p => { if (p is NotificationItem n) _ = _svc.PerformNotificationActionAsync(n.Uid, false); });
        ContinueOnPc = new DelegateCommand(p => { if (p is NotificationItem n) Continue(n); });
        DismissNotification = new DelegateCommand(p => { if (p is NotificationItem n) Notifications.Remove(n); });
        ClearNotifications = new DelegateCommand(_ => Notifications.Clear());
        OpenConversation = new DelegateCommand(p => { if (p is ConversationItem c) SelectedConversation = c; });
        CloseConversation = new DelegateCommand(_ => SelectedConversation = null);
        SendReply = new DelegateCommand(_ => _ = SendReplyAsync(), _ => CanSend);
        PlayPause = new DelegateCommand(_ => _ = _svc.MediaCommandAsync(AmsRemoteCommandId.TogglePlayPause));
        NextTrack = new DelegateCommand(_ => _ = _svc.MediaCommandAsync(AmsRemoteCommandId.NextTrack));
        PreviousTrack = new DelegateCommand(_ => _ = _svc.MediaCommandAsync(AmsRemoteCommandId.PreviousTrack));
        CallBack = new DelegateCommand(p => { /* dialing needs PhoneLine.Dial: shown only with FullAudio (not wired yet) */ });

        ApplyStatus(_svc.Status);
        _callCapability = _svc.CallCapability;
        _nowPlaying = _svc.NowPlaying;
        foreach (var m in _svc.Messages.Messages) OnMessage(m.Handle, m);
    }

    public PhoneSettings Settings { get; }
    public IContinuityService Service => _svc;

    /// <summary>A new ringing call: the host opens IncomingCallWindow for it.</summary>
    public event Action<CallViewModel>? IncomingCall;
    /// <summary>A new (not pre-existing, not call) notification the host may show as a toast.</summary>
    public event Action<NotificationItem>? ToastRequested;

    public Task StartAsync(CancellationToken ct = default) => _svc.StartAsync(ct);

    // ---- status --------------------------------------------------------------------------------------------------

    private PhoneLinkStatus _status = PhoneLinkStatus.NotPaired;
    public PhoneLinkStatus Status { get => _status; private set => Set(ref _status, value); }
    public string StatusTitle => Status.Title;
    public string? StatusDetail => Status.Detail;
    public bool HasStatusDetail => !string.IsNullOrEmpty(Status.Detail);
    public string DeviceName => Status.DeviceName ?? _svc.Device?.Name ?? "iPhone";
    /// <summary>"iPhone 15 Pro" from the phone's Bluetooth model number; null until it was read.</summary>
    public string? PhoneModelName => DeviceModels.DescribeIdentifier(Status.ModelId);
    public bool HasPhoneModel => PhoneModelName is not null;
    /// <summary>Device art for the header (Themes/DeviceArt.xaml key); island iPhone until the model is known.</summary>
    public string PhoneArtKey => DeviceModels.ArtKeyForIdentifier(Status.ModelId);
    public bool IsConnected => Status.State == PhoneLinkState.Connected;
    public bool IsPaired => Status.State is not (PhoneLinkState.NotPaired or PhoneLinkState.Pairing or PhoneLinkState.NoBluetooth) || _svc.Device is not null;
    /// <summary>Show the pairing guide instead of the content sections.</summary>
    public bool NeedsSetup => !IsPaired;
    public bool IsBusy => Status.State is PhoneLinkState.Connecting or PhoneLinkState.Reconnecting or PhoneLinkState.Pairing;
    /// <summary>"ok" / "warn" / "error" / "busy" / "off" — the status dot colour.</summary>
    public string StatusKind => Status.State switch
    {
        PhoneLinkState.Connected when Status.Issue == PhoneLinkIssue.None => "ok",
        PhoneLinkState.Connected => "warn",
        PhoneLinkState.Connecting or PhoneLinkState.Reconnecting or PhoneLinkState.Pairing => "busy",
        PhoneLinkState.Blocked or PhoneLinkState.NoBluetooth => "error",
        PhoneLinkState.Disconnected => "warn",
        _ => "off",
    };
    public bool IsBlocked => Status.State == PhoneLinkState.Blocked;
    public bool NeedsLeLink => Status.Issue == PhoneLinkIssue.NoLeLink;
    public bool HasNotificationsFeature => Status.Features.HasFlag(PhoneFeatures.Notifications);
    public bool HasMessagesFeature => Status.Features.HasFlag(PhoneFeatures.Messages);
    public bool HasMediaFeature => Status.Features.HasFlag(PhoneFeatures.Media);
    public bool HasCallsFeature => Status.Features.HasFlag(PhoneFeatures.Calls);

    private void ApplyStatus(PhoneLinkStatus s)
    {
        Status = s;
        Raise(string.Empty);   // every derived property
        if (s.State == PhoneLinkState.Connected && PairingStep is PairStep.Busy or PairStep.Confirm or PairStep.Searching or PairStep.Pick)
            PairingStep = PairStep.Success;
    }

    private CallCapabilityInfo _callCapability = CallCapabilityInfo.Unknown;
    public CallCapabilityInfo CallCapability
    {
        get => _callCapability;
        private set
        {
            if (!Set(ref _callCapability, value)) return;
            Raise(nameof(CallCapabilityTitle)); Raise(nameof(CallCapabilityReason)); Raise(nameof(CallCapabilityFix));
            Raise(nameof(HasCallCapabilityFix)); Raise(nameof(HasFullAudio));
        }
    }
    public string CallCapabilityTitle => CallCapability.Title;
    public string CallCapabilityReason => CallCapability.Reason;
    public string? CallCapabilityFix => CallCapability.Fix;
    public bool HasCallCapabilityFix => !string.IsNullOrEmpty(CallCapability.Fix);
    public bool HasFullAudio => CallCapability.Capability == Nitro.Continuity.Runtime.CallCapability.FullAudio;

    // ---- sections ------------------------------------------------------------------------------------------------

    private PhoneSection _section = PhoneSection.Messages;
    public PhoneSection Section
    {
        get => _section;
        set { if (Set(ref _section, value)) { Raise(nameof(IsMessages)); Raise(nameof(IsNotifications)); Raise(nameof(IsCalls)); } }
    }
    public bool IsMessages { get => Section == PhoneSection.Messages; set { if (value) Section = PhoneSection.Messages; } }
    public bool IsNotifications { get => Section == PhoneSection.Notifications; set { if (value) Section = PhoneSection.Notifications; } }
    public bool IsCalls { get => Section == PhoneSection.Calls; set { if (value) Section = PhoneSection.Calls; } }

    public DelegateCommand ShowSection { get; }

    // ---- pairing -------------------------------------------------------------------------------------------------

    public static IReadOnlyList<IosToggle> IosToggles { get; } =
    [
        new("Sistem Bildirimlerini Paylaş", "Bildirimler ve gelen aramalar bu bilgisayarda görünür.", ""),
        new("Bildirimleri Göster", "SMS ve iMessage mesajlarını okuyup buradan yanıtlarsınız.", ""),
        new("Kişileri Eşitle", "Arayanların ve mesaj gönderenlerin adları ve fotoğrafları görünür.", ""),
    ];

    public static IReadOnlyList<GuideStep> GuideSteps { get; } =
    [
        new(1, "iPhone'da Bluetooth ekranını açın", "Ayarlar → Bluetooth. Ekran açık kaldıkça iPhone bu bilgisayardan görünür."),
        new(2, "\"iPhone'u bul\"a basın ve listeden seçin", "Her iki ekranda aynı 6 haneli kod çıkar; iPhone'da \"Eşleştir\"e, burada \"Kodlar aynı\"ya dokunun."),
        new(3, "Üç anahtarı açın", "iPhone'da Ayarlar → Bluetooth → bu bilgisayarın yanındaki (i) simgesi."),
    ];

    private PairStep _pairStep = PairStep.Guide;
    public PairStep PairingStep
    {
        get => _pairStep;
        private set
        {
            if (!Set(ref _pairStep, value)) return;
            foreach (var n in new[] { nameof(IsGuideStep), nameof(IsSearchingStep), nameof(IsPickStep), nameof(IsConfirmStep),
                         nameof(IsPinStep), nameof(IsBusyStep), nameof(IsSuccessStep), nameof(IsErrorStep) }) Raise(n);
        }
    }
    public bool IsGuideStep => PairingStep == PairStep.Guide;
    public bool IsSearchingStep => PairingStep == PairStep.Searching;
    public bool IsPickStep => PairingStep == PairStep.Pick;
    public bool IsConfirmStep => PairingStep == PairStep.Confirm;
    public bool IsPinStep => PairingStep == PairStep.EnterPin;
    public bool IsBusyStep => PairingStep == PairStep.Busy;
    public bool IsSuccessStep => PairingStep == PairStep.Success;
    public bool IsErrorStep => PairingStep == PairStep.Error;

    public ObservableCollection<PhoneDevice> FoundPhones { get; } = new();
    private string? _pairingMessage;
    public string? PairingMessage { get => _pairingMessage; private set => Set(ref _pairingMessage, value); }
    private string? _pairingCode;
    public string? PairingCode { get => _pairingCode; private set => Set(ref _pairingCode, value); }
    private string _pinInput = "";
    public string PinInput { get => _pinInput; set => Set(ref _pinInput, value); }
    private string _pairingDevice = "iPhone";
    public string PairingDeviceName { get => _pairingDevice; private set => Set(ref _pairingDevice, value); }

    public DelegateCommand StartPairing { get; }
    public DelegateCommand PairDevice { get; }
    public DelegateCommand ConfirmCode { get; }
    public DelegateCommand RejectCode { get; }
    public DelegateCommand SubmitPin { get; }
    public DelegateCommand CancelPairing { get; }
    public DelegateCommand EstablishLe { get; }
    public DelegateCommand Disconnect { get; }
    public DelegateCommand Retry { get; }
    public DelegateCommand SyncContacts { get; }

    /// <summary>Already-paired iPhones are offered first; otherwise a 12 s classic inquiry.</summary>
    public async Task StartPairingAsync()
    {
        CancelPair();
        _pairCts = new CancellationTokenSource();
        var ct = _pairCts.Token;
        PairingStep = PairStep.Searching;
        PairingMessage = "iPhone aranıyor… iPhone'da Ayarlar → Bluetooth açık kalsın.";
        try
        {
            var paired = await _svc.GetPairedPhonesAsync(ct);
            var found = paired.Count > 0 ? paired : await _svc.DiscoverPhonesAsync(TimeSpan.FromSeconds(12), ct);
            _post(() =>
            {
                FoundPhones.Clear();
                foreach (var p in found) FoundPhones.Add(p);
                PairingStep = found.Count > 0 ? PairStep.Pick : PairStep.Error;
                PairingMessage = found.Count > 0 ? null : "iPhone bulunamadı. iPhone'un kilidini açın, Ayarlar → Bluetooth ekranında bekleyin ve tekrar deneyin.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _post(() => { PairingStep = PairStep.Error; PairingMessage = e.Message; }); }
    }

    public async Task PairAsync(PhoneDevice device)
    {
        _pairCts ??= new CancellationTokenSource();
        PairingDeviceName = device.Name;
        PairingStep = PairStep.Busy;
        PairingMessage = device.IsPaired ? $"{device.Name} bağlanıyor…" : $"{device.Name} ile eşleşiliyor…";
        try
        {
            if (device.IsPaired)
            {
                await _svc.UseDeviceAsync(device, _pairCts.Token);
                _post(() => { PairingStep = PairStep.Success; PairingMessage = $"{device.Name} kullanılıyor."; });
                return;
            }
            var r = await _svc.PairAsync(device, this, _pairCts.Token);
            _post(() => { PairingStep = r.Success ? PairStep.Success : PairStep.Error; PairingMessage = r.Message; });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _post(() => { PairingStep = PairStep.Error; PairingMessage = e.Message; }); }
    }

    private async Task EstablishLeAsync()
    {
        PairingMessage = "iPhone'u bilgisayarın yanında tutun…";
        try
        {
            var r = await _svc.EstablishNotificationLinkAsync(this);
            _post(() => PairingMessage = r.Message);
        }
        catch (Exception e) { _post(() => PairingMessage = e.Message); }
    }

    private async Task SyncContactsAsync()
    {
        try
        {
            int n = await _svc.SyncContactsAsync();
            _post(() => { ContactsMessage = n > 0 ? $"{n} kişi eşitlendi." : "Kişi alınamadı: iPhone'da \"Kişileri Eşitle\" açık mı?"; RefreshNames(); });
        }
        catch (Exception e) { _post(() => ContactsMessage = $"Kişiler alınamadı ({e.Message})."); }
    }

    private string? _contactsMessage;
    public string? ContactsMessage { get => _contactsMessage; private set => Set(ref _contactsMessage, value); }

    private void CancelPair()
    {
        _pairCts?.Cancel();
        _pairCts = null;
        _confirm?.TrySetResult(false);
        _pin?.TrySetResult(null);
        PairingStep = PairStep.Guide;
        PairingMessage = null;
        PairingCode = null;
    }

    Task<bool> IPairingPrompt.ConfirmAsync(PairingKind kind, string deviceName, string? pin, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            PairingDeviceName = deviceName;
            PairingCode = FormatCode(pin);
            if (kind == PairingKind.DisplayPin)
            {
                PairingMessage = "Bu kodu iPhone'a girin.";
                tcs.TrySetResult(true);
                return;
            }
            PairingMessage = pin is null ? $"{deviceName} ile eşleşilsin mi?" : "iPhone'daki kod bununla aynı mı?";
            _confirm?.TrySetResult(false);
            _confirm = tcs;
            PairingStep = PairStep.Confirm;
        });
        ct.Register(() => tcs.TrySetResult(false));
        return tcs.Task.ContinueWith(t => { _post(() => { if (PairingStep == PairStep.Confirm) PairingStep = PairStep.Busy; }); return t.Result; },
            TaskScheduler.Default);
    }

    Task<string?> IPairingPrompt.ProvidePinAsync(string deviceName, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            PairingDeviceName = deviceName;
            PinInput = "";
            PairingMessage = "iPhone'da görünen kodu yazın.";
            _pin = tcs;
            PairingStep = PairStep.EnterPin;
        });
        ct.Register(() => tcs.TrySetResult(null));
        return tcs.Task;
    }

    private static string? FormatCode(string? pin) =>
        pin is { Length: 6 } p && p.All(char.IsAsciiDigit) ? $"{p[..3]} {p[3..]}" : pin;

    // ---- notifications -------------------------------------------------------------------------------------------

    public ObservableCollection<NotificationItem> Notifications { get; } = new();
    public bool HasNotifications => Notifications.Count > 0;
    public DelegateCommand NotificationPositive { get; }
    public DelegateCommand NotificationNegative { get; }
    public DelegateCommand ContinueOnPc { get; }
    public DelegateCommand DismissNotification { get; }
    public DelegateCommand ClearNotifications { get; }

    private void OnNotification(PhoneNotification n, bool added)
    {
        if (n.IsIncomingCall) return;   // the call window shows those
        var existing = Notifications.FirstOrDefault(x => x.Uid == n.Uid);
        var item = existing ?? new NotificationItem { Uid = n.Uid };
        item.AppId = n.AppIdentifier;
        item.AppName = n.AppDisplayName ?? AppNameFromBundle(n.AppIdentifier);
        item.Title = DisplayTitle(n);
        item.Subtitle = n.Subtitle;
        item.Message = n.Message ?? "";
        item.Time = n.Date is { } d ? new DateTimeOffset(d) : _now();
        item.Category = n.Category;
        item.PositiveLabel = n.HasPositiveAction ? n.PositiveActionLabel ?? "Tamam" : null;
        item.NegativeLabel = n.HasNegativeAction ? n.NegativeActionLabel ?? "Sil" : null;
        item.IsPreExisting = n.IsPreExisting;
        item.Continuation = n.AppIdentifier == "com.apple.MobileSMS" ? null : ContinuationMap.Resolve(n.AppIdentifier, null);
        item.TimeText = PhoneFormat.When(item.Time, _now());
        if (existing is null)
        {
            int at = 0;
            while (at < Notifications.Count && Notifications[at].Time > item.Time) at++;
            Notifications.Insert(at, item);
            while (Notifications.Count > 200) Notifications.RemoveAt(Notifications.Count - 1);
            Raise(nameof(HasNotifications));
            if (added && !n.IsPreExisting && Settings.NotificationToasts && n.Category != AncsCategoryId.MissedCall)
                ToastRequested?.Invoke(item);
        }
        else item.RaiseAll();
    }

    private void OnNotificationRemoved(PhoneNotification n)
    {
        // History keeps the item (like Notification Center on macOS after it was seen); only the actions go away.
        var item = Notifications.FirstOrDefault(x => x.Uid == n.Uid);
        if (item is null) return;
        item.PositiveLabel = null;
        item.NegativeLabel = null;
        item.RaiseAll();
    }

    private string DisplayTitle(PhoneNotification n) =>
        n.AppIdentifier == "com.apple.MobileSMS" && _contacts.ByNumber(n.Title) is { } c ? c.Name : n.Title ?? n.AppDisplayName ?? "";

    private static string AppNameFromBundle(string? id) => id switch
    {
        null => "iPhone",
        _ when ContinuationMap.Resolve(id, null) is { } t => t.DisplayName,
        _ => id.Split('.').Last(),
    };

    /// <summary>
    /// "PC'de devam et": Messages → our conversation view; known apps → Nitro.Sync's ContinuationMap (app URI when its
    /// handler is installed, else the web app). Returns where it went (null = nothing to open).
    /// </summary>
    public ContinuationTarget? Continue(NotificationItem n)
    {
        if (n.IsMessagesApp)
        {
            Section = PhoneSection.Messages;
            SelectedConversation = Conversations.FirstOrDefault(c => c.DisplayName == n.Title || c.Key == PhoneNumbers.Key(n.Title))
                                   ?? SelectedConversation;
            return new ContinuationTarget("messages", "Mesajlar", "");
        }
        if (n.Continuation is not { } t) return null;
        if (_opener is not null)
        {
            if (t.AppUri is { } uri && _opener.TryOpenAppUri(uri)) return t;
            if (t.WebUrl.Length > 0) _opener.OpenWeb(t.WebUrl);
        }
        return t;
    }

    // ---- messages ------------------------------------------------------------------------------------------------

    public ObservableCollection<ConversationItem> Conversations { get; } = new();
    public bool HasConversations => Conversations.Count > 0;
    private ConversationItem? _selected;
    public ConversationItem? SelectedConversation
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value is not null) value.Unread = 0;
            Raise(nameof(HasSelectedConversation)); Raise(nameof(CanSend));
            SendReply.RaiseCanExecuteChanged();
        }
    }
    public bool HasSelectedConversation => SelectedConversation is not null;
    private string _replyText = "";
    public string ReplyText
    {
        get => _replyText;
        set { if (Set(ref _replyText, value)) { Raise(nameof(CanSend)); SendReply.RaiseCanExecuteChanged(); } }
    }
    public bool CanSend => SelectedConversation is { CanReply: true } && !string.IsNullOrWhiteSpace(ReplyText);
    public int UnreadMessages => Conversations.Sum(c => c.Unread);

    public DelegateCommand OpenConversation { get; }
    public DelegateCommand CloseConversation { get; }
    public DelegateCommand SendReply { get; }

    private void OnMessage(string oldHandle, PhoneMessage m)
    {
        // A sending placeholder can change handle (local-n → MAP handle): find it by the old one first.
        foreach (var c in Conversations)
        {
            var b = c.Messages.FirstOrDefault(x => x.Handle == oldHandle || x.Handle == m.Handle);
            if (b is null) continue;
            b.Message = m;
            c.Touch();
            return;
        }
        var conv = Conversations.FirstOrDefault(c => c.Key == m.ConversationKey);
        if (conv is null)
        {
            var contact = _contacts.ByNumber(m.Address);
            conv = new ConversationItem
            {
                Key = m.ConversationKey, Address = m.Address,
                DisplayName = contact?.Name ?? m.Name ?? PhoneNumbers.Pretty(m.Address),
                Photo = contact?.Photo,
            };
            Conversations.Add(conv);
            Raise(nameof(HasConversations));
        }
        int at = conv.Messages.Count;
        while (at > 0 && conv.Messages[at - 1].Time > m.Time) at--;
        conv.Messages.Insert(at, new MessageBubble(m));
        if (!m.Outgoing && !m.Read && conv != SelectedConversation) conv.Unread++;
        conv.LastTimeText = PhoneFormat.When(conv.LastTime, _now());
        conv.Touch();
        SortConversations();
        Raise(nameof(UnreadMessages));
    }

    private void OnMessageRemoved(PhoneMessage m)
    {
        foreach (var c in Conversations.ToList())
        {
            var b = c.Messages.FirstOrDefault(x => x.Handle == m.Handle);
            if (b is null) continue;
            c.Messages.Remove(b);
            if (c.Messages.Count == 0)
            {
                Conversations.Remove(c);
                if (SelectedConversation == c) SelectedConversation = null;
            }
            else c.Touch();
        }
        Raise(nameof(HasConversations));
        Raise(nameof(UnreadMessages));
    }

    private void SortConversations()
    {
        var sorted = Conversations.OrderByDescending(c => c.LastTime).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Conversations.IndexOf(sorted[i]);
            if (cur != i) Conversations.Move(cur, i);
        }
    }

    public async Task<bool> SendReplyAsync()
    {
        if (!CanSend || SelectedConversation is not { } c) return false;
        var text = ReplyText.Trim();
        ReplyText = "";
        return await _svc.SendMessageAsync(c.Address, text);
    }

    private void RefreshNames()
    {
        foreach (var c in Conversations)
            if (_contacts.ByNumber(c.Address) is { } contact) { c.DisplayName = contact.Name; c.Photo = contact.Photo; }
    }

    // ---- calls ---------------------------------------------------------------------------------------------------

    public ObservableCollection<RecentCallItem> RecentCalls { get; } = new();
    public bool HasRecentCalls => RecentCalls.Count > 0;
    public int MissedCalls => RecentCalls.Count(c => c.IsMissed);
    public DelegateCommand CallBack { get; }
    private CallViewModel? _activeCall;
    public CallViewModel? ActiveCall { get => _activeCall; private set { if (Set(ref _activeCall, value)) Raise(nameof(HasActiveCall)); } }
    public bool HasActiveCall => ActiveCall is not null;

    private void OnCall(PhoneCallInfo c)
    {
        if (!_calls.TryGetValue(c.Id, out var vm))
        {
            if (c.State == CallState.Ended) return;
            var contact = _contacts.ByNumber(c.Number) ?? _contacts.ByName(c.Name);
            vm = new CallViewModel(c, contact, _svc, CallCapability, _now);
            _calls[c.Id] = vm;
            ActiveCall = vm;
            if (Settings.CallToasts || c.State != CallState.Ringing) IncomingCall?.Invoke(vm);
            return;
        }
        vm.Update(c);
        if (c.State == CallState.Ended)
        {
            _calls.Remove(c.Id);
            if (ActiveCall == vm) ActiveCall = _calls.Values.LastOrDefault();
        }
    }

    private void AddRecent(RecentCall c)
    {
        var contact = _contacts.ByNumber(c.Number) ?? _contacts.ByName(c.Name);
        var item = new RecentCallItem
        {
            Call = c, Number = c.Number,
            DisplayName = contact?.Name ?? c.Name ?? (c.Number is { Length: > 0 } n ? PhoneNumbers.Pretty(n) : "Bilinmeyen"),
            TimeText = PhoneFormat.When(c.At, _now()),
        };
        int at = 0;
        while (at < RecentCalls.Count && RecentCalls[at].Call.At > c.At) at++;
        RecentCalls.Insert(at, item);
        while (RecentCalls.Count > 100) RecentCalls.RemoveAt(RecentCalls.Count - 1);
        Raise(nameof(HasRecentCalls)); Raise(nameof(MissedCalls));
    }

    // ---- now playing ---------------------------------------------------------------------------------------------

    private NowPlaying? _nowPlaying;
    public NowPlaying? NowPlaying
    {
        get => _nowPlaying;
        private set
        {
            if (!Set(ref _nowPlaying, value)) return;
            Raise(nameof(HasNowPlaying)); Raise(nameof(NowPlayingTitle)); Raise(nameof(NowPlayingSubtitle));
            Raise(nameof(IsPlaying)); Raise(nameof(PlayPauseGlyph)); Raise(nameof(NowPlayingProgress));
        }
    }
    public bool HasNowPlaying => NowPlaying is { HasTrack: true };
    public string NowPlayingTitle => NowPlaying?.Title ?? "";
    public string NowPlayingSubtitle => string.Join(" — ", new[] { NowPlaying?.Artist, NowPlaying?.PlayerName }.Where(s => !string.IsNullOrEmpty(s)));
    public bool IsPlaying => NowPlaying?.IsPlaying == true;
    public string PlayPauseGlyph => IsPlaying ? "" : "";
    public double NowPlayingProgress => NowPlaying is { DurationSeconds: > 0 and var d, ElapsedSeconds: { } e } ? Math.Clamp(e / d, 0, 1) : 0;
    public DelegateCommand PlayPause { get; }
    public DelegateCommand NextTrack { get; }
    public DelegateCommand PreviousTrack { get; }

    // ---- housekeeping --------------------------------------------------------------------------------------------

    /// <summary>Refreshes relative times ("5 dk önce") and call timers; the host calls it every few seconds.</summary>
    public void Tick()
    {
        var now = _now();
        foreach (var n in Notifications) n.TimeText = PhoneFormat.When(n.Time, now);
        foreach (var c in Conversations) c.LastTimeText = PhoneFormat.When(c.LastTime, now);
        foreach (var call in _calls.Values) call.Tick();
    }

    public void SaveSettings() => Settings.Save();

    public bool NotificationToasts { get => Settings.NotificationToasts; set { Settings.NotificationToasts = value; Raise(); Settings.Save(); } }
    public bool CallToasts { get => Settings.CallToasts; set { Settings.CallToasts = value; Raise(); Settings.Save(); } }
    public bool ShowPreviews { get => Settings.ShowPreviews; set { Settings.ShowPreviews = value; Raise(); Settings.Save(); } }

    public ValueTask DisposeAsync()
    {
        CancelPair();
        return _svc.DisposeAsync();
    }
}

/// <summary>One call for IncomingCallWindow (ringing toast, then in-call mini window).</summary>
public sealed class CallViewModel : PhoneObservable
{
    private readonly IContinuityService _svc;
    private readonly Func<DateTimeOffset> _now;
    private PhoneCallInfo _info;

    public CallViewModel(PhoneCallInfo info, PhoneContact? contact, IContinuityService svc, CallCapabilityInfo capability, Func<DateTimeOffset>? clock = null)
    {
        _info = info;
        _svc = svc;
        _now = clock ?? (() => DateTimeOffset.Now);
        Capability = capability;
        DisplayName = contact?.Name ?? info.Name ?? (info.Number is { Length: > 0 } n ? PhoneNumbers.Pretty(n) : "Bilinmeyen arayan");
        Photo = contact?.Photo;
        Answer = new DelegateCommand(_ => _ = _svc.AnswerCallAsync(Id));
        Decline = new DelegateCommand(_ => _ = _svc.DeclineCallAsync(Id));
        End = new DelegateCommand(_ => _ = _svc.EndCallAsync(Id));
        ToggleMute = new DelegateCommand(_ => _ = _svc.SetMuteAsync(Id, !Info.Muted), _ => Info.CanMute);
        TransferAudio = new DelegateCommand(_ => _ = _svc.SetAudioOnPcAsync(Id, !Info.AudioOnPc), _ => Info.CanTransferAudio);
        Tick();
    }

    public PhoneCallInfo Info => _info;
    public string Id => _info.Id;
    public CallCapabilityInfo Capability { get; }
    public string DisplayName { get; }
    public byte[]? Photo { get; }
    public bool HasPhoto => Photo is { Length: > 0 };
    public string Initials => PhoneFormat.Initials(DisplayName);
    /// <summary>Second line: the number when we show a name, else "iPhone".</summary>
    public string Subtitle => _info.Number is { Length: > 0 } n && PhoneNumbers.Pretty(n) != DisplayName ? PhoneNumbers.Pretty(n) : "iPhone";
    public bool IsRinging => _info.State == CallState.Ringing;
    public bool IsActive => _info.State is CallState.Active or CallState.Held or CallState.Dialing;
    public bool IsEnded => _info.State == CallState.Ended;
    public bool Muted => _info.Muted;
    public bool AudioOnPc => _info.AudioOnPc;
    public bool CanEnd => _info.CanEnd;
    public bool CanMute => _info.CanMute;
    public bool CanTransferAudio => _info.CanTransferAudio;
    public string MuteLabel => Muted ? "Sesi aç" : "Sessiz";
    public string MuteGlyph => Muted ? "" : "";
    public string TransferLabel => AudioOnPc ? "Sesi telefona aktar" : "Sesi bilgisayara al";
    public string EndLabel => CanEnd ? "Kapat" : "Gizle";
    public string RingingCaption => Capability.Capability == Nitro.Continuity.Runtime.CallCapability.FullAudio
        ? "iPhone · gelen arama" : "iPhone · ses telefonda";
    public string AudioNote => _info.Source == CallSource.Ancs
        ? "Görüşme iPhone'da sürüyor. Bu bilgisayar yalnızca yanıtlayıp reddedebilir."
        : AudioOnPc ? "Ses bu bilgisayarda" : "Ses iPhone'da";

    private string _duration = "";
    public string DurationText { get => _duration; private set => Set(ref _duration, value); }

    public DelegateCommand Answer { get; }
    public DelegateCommand Decline { get; }
    public DelegateCommand End { get; }
    public DelegateCommand ToggleMute { get; }
    public DelegateCommand TransferAudio { get; }

    /// <summary>Raised once when the call ends (the window closes itself).</summary>
    public event Action? Ended;

    public void Update(PhoneCallInfo info)
    {
        bool wasEnded = IsEnded;
        _info = info;
        Raise(string.Empty);
        ToggleMute.RaiseCanExecuteChanged();
        TransferAudio.RaiseCanExecuteChanged();
        Tick();
        if (!wasEnded && IsEnded) Ended?.Invoke();
    }

    public void Tick() =>
        DurationText = _info.State switch
        {
            CallState.Ringing => "Arıyor…",
            CallState.Dialing => "Aranıyor…",
            CallState.Ended => "Arama bitti",
            CallState.Held => "Beklemede",
            _ => _info.ConnectedAt is { } t ? PhoneFormat.Duration(_now() - t) : "00:00",
        };
}
