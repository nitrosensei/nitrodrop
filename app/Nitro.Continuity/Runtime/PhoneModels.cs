using Nitro.Continuity.Ancs;

namespace Nitro.Continuity.Runtime;

// UI-facing models for the iPhone continuity runtime. Pure .NET (no WinRT/WPF) so the view model, the demo service and
// the tests share them with the Windows transport (Nitro.Continuity.Windows).

public enum PhoneLinkState
{
    /// <summary>No Bluetooth radio, or it is switched off.</summary>
    NoBluetooth,
    /// <summary>No iPhone chosen / paired yet.</summary>
    NotPaired,
    Pairing,
    Connecting,
    Connected,
    /// <summary>Lost the link; retrying with backoff.</summary>
    Reconnecting,
    Disconnected,
    /// <summary>Another consumer (Phone Link) owns the device; needs user action.</summary>
    Blocked,
}

public enum PhoneLinkIssue
{
    None,
    /// <summary>Phone Link (Telefon Bağlantısı) holds the iPhone: DeniedBySystem, garbled ANCS Data Source, MAP ServiceUnavailable.</summary>
    PhoneLinkHoldsDevice,
    /// <summary>ANCS is not visible: "Sistem Bildirimlerini Paylaş" is off on the iPhone (or no LE bond).</summary>
    NotificationsNotShared,
    /// <summary>MAP refused: "Bildirimleri Göster" is off.</summary>
    MessagesNotShared,
    /// <summary>PBAP refused: "Kişileri Eşitle" is off.</summary>
    ContactsNotShared,
    /// <summary>Classic pairing worked but no LE (ANCS) link could be found (CTKD did not happen).</summary>
    NoLeLink,
    BluetoothOff,
}

[Flags]
public enum PhoneFeatures
{
    None = 0,
    Notifications = 1,
    Media = 2,
    Messages = 4,
    Contacts = 8,
    Calls = 16,
}

public sealed record PhoneLinkStatus(
    PhoneLinkState State,
    string Title,
    string? Detail = null,
    string? DeviceName = null,
    PhoneLinkIssue Issue = PhoneLinkIssue.None,
    PhoneFeatures Features = PhoneFeatures.None,
    string? ModelId = null)
{
    // ModelId: hardware identifier from the phone's Device Information Service ("iPhone15,2"), when it could be read.
    public bool IsConnected => State == PhoneLinkState.Connected;

    public static PhoneLinkStatus NotPaired { get; } =
        new(PhoneLinkState.NotPaired, "iPhone bağlı değil", "Başlamak için iPhone'unuzu eşleştirin.");
}

/// <summary>A (paired or discovered) phone. <see cref="Id"/> is the classic BluetoothDevice id.</summary>
public sealed record PhoneDevice(string Id, string Name, ulong Address, bool IsPaired, bool IsConnected = false)
{
    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> (8 * (5 - i))) & 0xFF).ToString("X2")));
}

public enum PairingKind { ConfirmOnly, ConfirmPinMatch, DisplayPin, ProvidePin }

/// <summary>UI callbacks for Windows custom pairing (DevicePairingRequestedEventArgs).</summary>
public interface IPairingPrompt
{
    /// <summary>ConfirmOnly / ConfirmPinMatch / DisplayPin: show the code (when given) and ask the user to confirm it matches the iPhone.</summary>
    Task<bool> ConfirmAsync(PairingKind kind, string deviceName, string? pin, CancellationToken ct);
    /// <summary>ProvidePin: ask the user to type the code shown on the iPhone. Null = cancel.</summary>
    Task<string?> ProvidePinAsync(string deviceName, CancellationToken ct);
}

public enum PairingOutcome { Paired, AlreadyPaired, Rejected, Canceled, Failed, NotFound }

public sealed record PairingResult(PairingOutcome Outcome, string Message)
{
    public bool Success => Outcome is PairingOutcome.Paired or PairingOutcome.AlreadyPaired;
}

// ---------------------------------------------------------------------------------------------------------------
// Calls

public enum CallDirection { Incoming, Outgoing }

public enum CallState { Ringing, Dialing, Active, Held, Ended }

/// <summary>Which path carries a call: Windows inbox HFP via PhoneLine (audio on the PC) or ANCS actions (audio stays on the phone).</summary>
public enum CallSource { PhoneLine, Ancs, Demo }

public sealed record PhoneCallInfo
{
    public required string Id { get; init; }
    public string? Number { get; init; }
    /// <summary>Name as the phone reported it (ANCS Title / HFP +CLIP / PhoneCallInfo.DisplayName).</summary>
    public string? Name { get; init; }
    public CallDirection Direction { get; init; } = CallDirection.Incoming;
    public CallState State { get; init; } = CallState.Ringing;
    public CallSource Source { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? ConnectedAt { get; init; }
    public bool AudioOnPc { get; init; }
    public bool Muted { get; init; }
    /// <summary>ANCS notification UID when the call came in through ANCS.</summary>
    public uint? AncsUid { get; init; }

    /// <summary>ANCS can answer/decline a ringing call but cannot hang up, mute or move audio afterwards.</summary>
    public bool CanEnd => Source != CallSource.Ancs;
    public bool CanMute => Source != CallSource.Ancs;
    public bool CanTransferAudio => Source != CallSource.Ancs;
}

public enum CallOutcome { Answered, Missed, Declined, Outgoing }

public sealed record RecentCall(string? Number, string? Name, CallDirection Direction, CallOutcome Outcome, DateTimeOffset At, TimeSpan Duration);

public enum CallCapability
{
    /// <summary>No call support right now (not connected).</summary>
    None,
    /// <summary>Ring + answer/decline from the PC through ANCS; audio stays on the iPhone.</summary>
    ControlOnly,
    /// <summary>Windows inbox Hands-Free via PhoneLine: answer/end/mute and audio on the PC.</summary>
    FullAudio,
}

/// <param name="Reason">Why this level (Turkish, shown under the status card).</param>
/// <param name="Fix">What the user can do to get <see cref="CallCapability.FullAudio"/>; null when nothing to do.</param>
public sealed record CallCapabilityInfo(CallCapability Capability, string Reason, string? Fix = null)
{
    public string Title => Capability switch
    {
        CallCapability.FullAudio => "Aramalar: ses bu bilgisayarda",
        CallCapability.ControlOnly => "Aramalar: yanıtla / reddet (ses telefonda)",
        _ => "Aramalar kullanılamıyor",
    };

    public static CallCapabilityInfo Unknown { get; } = new(CallCapability.None, "iPhone bağlı değil.");
}

/// <summary>PhoneLineTransportDevice.RequestAccessAsync result (DeviceAccessStatus) plus "not tried".</summary>
public enum PhoneLineAccess { NotChecked, Allowed, DeniedByUser, DeniedBySystem, Unspecified, Error }

/// <summary>Facts gathered by the Windows CallService; <see cref="CallCapabilityPolicy"/> turns them into a decision.</summary>
public sealed record CallEnvironment(
    bool HasPackageIdentity,
    bool TransportFound = false,
    PhoneLineAccess Access = PhoneLineAccess.NotChecked,
    bool? Registered = null,
    bool? Connected = null,
    bool AncsAvailable = false,
    string? Error = null);

public static class CallCapabilityPolicy
{
    public const string FixPackage = "NitroDrop'u MSIX paketiyle kurun (docs/continuity-plan.md §9.5). Paket, Windows'un arama sesini uygulamaya vermesi için gerekli.";
    public const string FixPhoneLink = "Phone Link (Telefon Bağlantısı) uygulamasında bu iPhone'un bağlantısını kaldırın; ardından iPhone'u Windows Bluetooth ayarlarından kaldırıp NitroDrop ile yeniden eşleştirin.";

    /// <summary>
    /// FullAudio only when every PhoneLine step succeeded (identity → transport → access → RegisterApp → ConnectAsync).
    /// Otherwise ControlOnly if ANCS is up (answer/decline still work), else None. The first failing step names the reason.
    /// </summary>
    public static CallCapabilityInfo Decide(CallEnvironment e)
    {
        if (e.HasPackageIdentity && e.TransportFound && e.Access == PhoneLineAccess.Allowed && e.Registered == true && e.Connected == true)
            return new(CallCapability.FullAudio, "Windows Eller Serbest bağlantısı hazır: aramalar bu bilgisayarın hoparlör ve mikrofonuyla konuşulabilir.");

        var (reason, fix) = FirstBlocker(e);
        if (e.AncsAvailable)
            return new(CallCapability.ControlOnly, reason + " Aramaları buradan yanıtlayıp reddedebilirsiniz; konuşma iPhone'da sürer.", fix);
        return new(CallCapability.None, reason + " Bildirim bağlantısı (ANCS) da yok.", fix);
    }

    private static (string Reason, string? Fix) FirstBlocker(CallEnvironment e)
    {
        if (!e.HasPackageIdentity)
            return ("NitroDrop paketsiz çalışıyor; Windows arama sesini yalnızca paket kimliği olan uygulamalara veriyor.", FixPackage);
        if (e.Error is { Length: > 0 } err)
            return ($"Windows arama hattı açılamadı ({err}).", null);
        if (!e.TransportFound)
            return ("iPhone Windows'ta \"Eller Serbest\" (HFP) cihazı olarak görünmüyor.", "iPhone'u Windows Bluetooth ayarlarından eşleştirin; eşleşme sırasında \"Kişileri Eşitle\"ye izin verin.");
        return e.Access switch
        {
            PhoneLineAccess.DeniedBySystem => ("Bu iPhone'un aramaları başka bir uygulamaya (büyük olasılıkla Phone Link) kayıtlı.", FixPhoneLink),
            PhoneLineAccess.DeniedByUser => ("Telefon araması erişimine izin verilmedi.", "Windows Ayarlar → Gizlilik ve güvenlik → Telefon aramaları'ndan NitroDrop'a izin verin."),
            PhoneLineAccess.NotChecked or PhoneLineAccess.Unspecified or PhoneLineAccess.Error =>
                ("Windows arama erişimi doğrulanamadı.", null),
            _ when e.Registered != true => ("Windows uygulama kaydını kabul etmedi (RegisterApp).", FixPhoneLink),
            _ => ("Windows Eller Serbest bağlantısını kuramadı (ConnectAsync).", "iPhone'un Bluetooth'unun açık ve yakında olduğundan emin olun."),
        };
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Media (AMS)

public sealed record NowPlaying(string? Title, string? Artist, string? Album, string? PlayerName, bool IsPlaying,
    double? DurationSeconds = null, double? ElapsedSeconds = null)
{
    public bool HasTrack => !string.IsNullOrEmpty(Title);
}

/// <summary>Folds AMS Entity Update notifications into a <see cref="NowPlaying"/>.</summary>
public sealed class AmsNowPlayingTracker
{
    private NowPlaying _np = new(null, null, null, null, false);

    public NowPlaying Current => _np;

    /// <summary>Entity Update subscriptions to write after subscribing to the characteristic.</summary>
    public static IReadOnlyList<byte[]> Subscriptions { get; } =
    [
        AmsEntityUpdate.Subscribe(AmsEntityId.Player, (byte)AmsPlayerAttributeId.Name, (byte)AmsPlayerAttributeId.PlaybackInfo),
        AmsEntityUpdate.Subscribe(AmsEntityId.Track, (byte)AmsTrackAttributeId.Artist, (byte)AmsTrackAttributeId.Album,
            (byte)AmsTrackAttributeId.Title, (byte)AmsTrackAttributeId.Duration),
    ];

    public NowPlaying Apply(AmsEntityUpdate u)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _np = (u.Entity, u.AttributeId) switch
        {
            (AmsEntityId.Track, (byte)AmsTrackAttributeId.Title) => _np with { Title = Empty(u.Value) },
            (AmsEntityId.Track, (byte)AmsTrackAttributeId.Artist) => _np with { Artist = Empty(u.Value) },
            (AmsEntityId.Track, (byte)AmsTrackAttributeId.Album) => _np with { Album = Empty(u.Value) },
            (AmsEntityId.Track, (byte)AmsTrackAttributeId.Duration) =>
                _np with { DurationSeconds = double.TryParse(u.Value, System.Globalization.NumberStyles.Float, inv, out var d) ? d : null },
            (AmsEntityId.Player, (byte)AmsPlayerAttributeId.Name) => _np with { PlayerName = Empty(u.Value) },
            // PlaybackInfo = "PlaybackState,PlaybackRate,ElapsedTime"; state 0 paused, 1 playing, 2 rewinding, 3 fast-forwarding
            (AmsEntityId.Player, (byte)AmsPlayerAttributeId.PlaybackInfo) => ApplyPlayback(u.Value, inv),
            _ => _np,
        };
        return _np;
    }

    private NowPlaying ApplyPlayback(string v, IFormatProvider inv)
    {
        var parts = v.Split(',');
        bool playing = parts.Length > 0 && parts[0].Trim() is "1" or "2" or "3";
        double? elapsed = parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var e) ? e : null;
        return _np with { IsPlaying = playing, ElapsedSeconds = elapsed ?? _np.ElapsedSeconds };
    }

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

// ---------------------------------------------------------------------------------------------------------------
// Service contract

/// <summary>
/// Everything the "Telefon" tab needs. The Windows implementation lives in Nitro.Continuity.Windows
/// (ContinuityRuntime); AirDrop.App has a demo implementation for --demo / --snapshot.
/// Events may be raised on any thread.
/// </summary>
public interface IContinuityService : IAsyncDisposable
{
    PhoneLinkStatus Status { get; }
    CallCapabilityInfo CallCapability { get; }
    NowPlaying? NowPlaying { get; }
    MessageStore Messages { get; }
    PhoneDevice? Device { get; }

    event Action<PhoneLinkStatus>? StatusChanged;
    event Action<CallCapabilityInfo>? CallCapabilityChanged;
    event Action<PhoneNotification>? NotificationAdded;
    event Action<PhoneNotification>? NotificationModified;
    event Action<PhoneNotification>? NotificationRemoved;
    /// <summary>Ringing / active / ended call updates (Ended is the last one for an id).</summary>
    event Action<PhoneCallInfo>? CallUpdated;
    event Action<RecentCall>? CallLogged;
    event Action<NowPlaying?>? NowPlayingChanged;

    /// <summary>Opens the remembered iPhone (if any) and keeps the link alive.</summary>
    Task StartAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PhoneDevice>> GetPairedPhonesAsync(CancellationToken ct = default);
    /// <summary>Classic inquiry for phones in pairing mode (iOS: Ayarlar → Bluetooth open).</summary>
    Task<IReadOnlyList<PhoneDevice>> DiscoverPhonesAsync(TimeSpan timeout, CancellationToken ct = default);
    Task<PairingResult> PairAsync(PhoneDevice device, IPairingPrompt prompt, CancellationToken ct = default);
    /// <summary>Remember and connect to this (paired) iPhone.</summary>
    Task UseDeviceAsync(PhoneDevice device, CancellationToken ct = default);
    /// <summary>
    /// When classic pairing did not yield an LE link (no CTKD): procedure B (LE pairing to the nearest Apple advertiser),
    /// then C (HID bait the user taps in iOS Bluetooth settings).
    /// </summary>
    Task<PairingResult> EstablishNotificationLinkAsync(IPairingPrompt prompt, CancellationToken ct = default);
    /// <summary>Stop using the iPhone (does not unpair it from Windows).</summary>
    Task DisconnectAsync();

    Task<bool> PerformNotificationActionAsync(uint uid, bool positive);
    Task<bool> AnswerCallAsync(string callId);
    Task<bool> DeclineCallAsync(string callId);
    Task<bool> EndCallAsync(string callId);
    Task<bool> SetMuteAsync(string callId, bool muted);
    /// <summary>true = audio to this PC (LocalDevice), false = back to the iPhone.</summary>
    Task<bool> SetAudioOnPcAsync(string callId, bool onPc);

    Task<bool> SendMessageAsync(string address, string text, CancellationToken ct = default);
    Task<bool> MediaCommandAsync(AmsRemoteCommandId command);
    /// <summary>PBAP pull (read-only) into the contact book. Returns the number of contacts imported.</summary>
    Task<int> SyncContactsAsync(CancellationToken ct = default);
}
