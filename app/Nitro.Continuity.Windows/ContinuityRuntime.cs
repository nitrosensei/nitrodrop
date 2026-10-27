using System.Text.Json;
using Nitro.Continuity.Ancs;
using Nitro.Continuity.Map;
using Nitro.Continuity.Obex;
using Nitro.Continuity.Runtime;
using Windows.Devices.Bluetooth;

namespace Nitro.Continuity.Platform;

/// <summary>
/// The real <see cref="IContinuityService"/>: composes <see cref="IphoneLink"/>, <see cref="AncsClient"/>,
/// <see cref="AmsClient"/>, <see cref="MapClient"/> + <see cref="MnsServer"/>, PBAP and <see cref="CallService"/>.
/// Remembers the chosen iPhone in <c>stateFile</c>. Does nothing with Bluetooth until <see cref="StartAsync"/> finds a
/// remembered phone or the user picks one.
/// </summary>
public sealed class ContinuityRuntime : IContinuityService
{
    private readonly string? _stateFile;
    private readonly Func<string, int>? _importVCards;
    private readonly IphoneLink _link = new();
    private readonly AncsClient _ancs = new();
    private readonly AmsClient _ams = new();
    private readonly MapClient _map = new();
    private readonly MnsServer _mns = new();
    private readonly CallService _calls;
    private readonly PhoneLinkConflictDetector _conflict = new();
    private readonly SemaphoreSlim _attach = new(1, 1);
    private GattStartResult _ancsResult = GattStartResult.Failed;
    private bool _amsOk, _mapOk;
    private PhoneLinkIssue _mapIssue;

    /// <param name="stateFile">JSON file remembering the chosen iPhone (null = in memory only).</param>
    /// <param name="importVCards">Imports PBAP vCard text into the contact book; returns how many were imported.</param>
    public ContinuityRuntime(string? stateFile, Func<string, int>? importVCards)
    {
        _stateFile = stateFile;
        _importVCards = importVCards;
        _calls = new CallService(_ancs.PerformActionAsync);

        _link.PhaseChanged += _ => PublishStatus();
        _link.LeReady += le => _ = AttachAsync(le);
        _link.LeLost += () => { _ancs.Center.Reset(); _calls.SetAncsAvailable(false); PublishStatus(); };

        _ancs.Center.Added += n => { NotificationAdded?.Invoke(n); _calls.OnAncsNotification(n); };
        _ancs.Center.Modified += n => { NotificationModified?.Invoke(n); _calls.OnAncsNotification(n); };
        _ancs.Center.Removed += n => { NotificationRemoved?.Invoke(n); _calls.OnAncsRemoved(n); };
        _ancs.StreamError += _ => { if (_conflict.ReportAncsStreamError(DateTimeOffset.Now)) PublishStatus(); };

        _ams.Changed += np => { NowPlaying = np; NowPlayingChanged?.Invoke(np); };
        _mns.EventReport += (mas, events) => _ = OnMapEventsAsync(events);

        _calls.CapabilityChanged += c => CallCapabilityChanged?.Invoke(c);
        _calls.CallUpdated += c => CallUpdated?.Invoke(c);
        _calls.CallLogged += c => CallLogged?.Invoke(c);
    }

    public PhoneLinkStatus Status { get; private set; } = PhoneLinkStatus.NotPaired;
    public CallCapabilityInfo CallCapability => _calls.Capability;
    public NowPlaying? NowPlaying { get; private set; }
    public MessageStore Messages { get; } = new();
    public PhoneDevice? Device { get; private set; }

    public event Action<PhoneLinkStatus>? StatusChanged;
    public event Action<CallCapabilityInfo>? CallCapabilityChanged;
    public event Action<PhoneNotification>? NotificationAdded;
    public event Action<PhoneNotification>? NotificationModified;
    public event Action<PhoneNotification>? NotificationRemoved;
    public event Action<PhoneCallInfo>? CallUpdated;
    public event Action<RecentCall>? CallLogged;
    public event Action<NowPlaying?>? NowPlayingChanged;

    private sealed record State(string? DeviceId, string? Name, ulong Address);

    public async Task StartAsync(CancellationToken ct = default)
    {
        var (present, on) = await IphonePairing.RadioStateAsync();
        if (!present || !on) { PublishStatus(); return; }
        if (LoadState() is { DeviceId: { } id } s)
            await UseDeviceAsync(new PhoneDevice(id, s.Name ?? "iPhone", s.Address, true), ct);
        else PublishStatus();
    }

    public Task<IReadOnlyList<PhoneDevice>> GetPairedPhonesAsync(CancellationToken ct = default) => IphonePairing.GetPairedPhonesAsync(ct);

    public Task<IReadOnlyList<PhoneDevice>> DiscoverPhonesAsync(TimeSpan timeout, CancellationToken ct = default) =>
        IphonePairing.DiscoverAsync(timeout, ct);

    public async Task<PairingResult> PairAsync(PhoneDevice device, IPairingPrompt prompt, CancellationToken ct = default)
    {
        Status = new PhoneLinkStatus(PhoneLinkState.Pairing, "Eşleştiriliyor…", "iPhone'da çıkan isteğe \"Eşleştir\" ile yanıt verin.", device.Name);
        StatusChanged?.Invoke(Status);
        var r = await IphonePairing.PairClassicAsync(device, prompt, ct);
        if (r.Success)
        {
            // AEP ids from discovery become a BluetoothDevice id after pairing; re-resolve by address.
            var paired = (await IphonePairing.GetPairedPhonesAsync(ct)).FirstOrDefault(p => p.Address == device.Address) ?? device with { IsPaired = true };
            await UseDeviceAsync(paired, ct);
        }
        else PublishStatus();
        return r;
    }

    public async Task UseDeviceAsync(PhoneDevice device, CancellationToken ct = default)
    {
        Device = device;
        SaveState(new State(device.Id, device.Name, device.Address));
        _conflict.Reset();
        await _link.UseAsync(device, ct);
        PublishStatus();
    }

    public async Task<PairingResult> EstablishNotificationLinkAsync(IPairingPrompt prompt, CancellationToken ct = default)
    {
        var name = Device?.Name ?? "iPhone";
        var (le, r) = await IphonePairing.PairLeNearestAppleAsync(name, prompt, TimeSpan.FromSeconds(8), ct);
        if (le is null)
        {
            await using var bait = new HidBait();
            le = await bait.WaitForPhoneAsync(TimeSpan.FromSeconds(90), ct);
            r = le is null
                ? new PairingResult(PairingOutcome.Failed, "iPhone bağlanmadı. iPhone'da Ayarlar → Bluetooth → Diğer Aygıtlar'da bu bilgisayara dokunun.")
                : new PairingResult(PairingOutcome.Paired, "Bildirim bağlantısı kuruldu.");
        }
        if (le is not null) await _link.AdoptLeAsync(le);
        if (le is not null) _ = AttachAsync(le);
        return r;
    }

    public async Task DisconnectAsync()
    {
        await _ancs.StopAsync();
        await _ams.StopAsync();
        await _map.DisconnectAsync();
        await _link.StopAsync();
        Device = null;
        SaveState(new State(null, null, 0));
        Messages.Clear();
        PublishStatus();
    }

    /// <summary>LE link usable: ANCS, AMS, then (classic) MAP + MNS, PBAP not automatic, then call capability.</summary>
    private async Task AttachAsync(BluetoothLEDevice le)
    {
        await _attach.WaitAsync();
        try
        {
            _ancsResult = await Safe(() => _ancs.StartAsync(le), GattStartResult.Failed);
            _calls.SetAncsAvailable(_ancsResult == GattStartResult.Ok);
            _amsOk = await Safe(() => _ams.StartAsync(le), GattStartResult.Failed) == GattStartResult.Ok;
            PublishStatus();

            if (_link.Classic is { } classic)
            {
                await ConnectMapAsync(classic);
                await Safe(async () => { await _calls.EvaluateAsync(classic.BluetoothAddress); return true; }, false);
                _conflict.ReportPhoneLineAccess(_calls.LastAccess);
            }
            PublishStatus();
        }
        finally { _attach.Release(); }
    }

    private async Task ConnectMapAsync(BluetoothDevice classic)
    {
        _mapOk = false; _mapIssue = PhoneLinkIssue.None;
        try
        {
            await _mns.StartAsync();
            await _map.ConnectAsync(classic);
            _mapOk = true;
            await _map.SetNotificationRegistrationAsync(true);
            // iOS lists only messages that arrived while connected (often nothing); load what it gives.
            foreach (var item in await _map.GetMessagesListingAsync("inbox", 30))
                Messages.Upsert(MessageStore.FromListing(item));
        }
        catch (ObexException e) when (e.Code is ObexResponseCode.ServiceUnavailable or ObexResponseCode.Forbidden)
        {
            _conflict.ReportObexConnectRefused((byte)e.Code);
            _mapIssue = PhoneLinkIssue.PhoneLinkHoldsDevice;
        }
        catch (RfcommServiceMissingException) { _mapIssue = PhoneLinkIssue.MessagesNotShared; }
        catch (Exception) { _mapIssue = PhoneLinkIssue.MessagesNotShared; }
    }

    private async Task OnMapEventsAsync(IReadOnlyList<MapEvent> events)
    {
        foreach (var ev in events)
        {
            try
            {
                await Messages.ApplyEventAsync(ev, async h => _map.IsConnected ? await _map.GetMessageAsync(h) : null);
            }
            catch (Exception) { /* one bad event must not stop the rest */ }
        }
    }

    public Task<bool> PerformNotificationActionAsync(uint uid, bool positive) => _ancs.PerformActionAsync(uid, positive);
    public Task<bool> AnswerCallAsync(string callId) => _calls.AnswerAsync(callId);
    public Task<bool> DeclineCallAsync(string callId) => _calls.DeclineAsync(callId);
    public Task<bool> EndCallAsync(string callId) => _calls.EndAsync(callId);
    public Task<bool> SetMuteAsync(string callId, bool muted) => _calls.SetMuteAsync(callId, muted);
    public Task<bool> SetAudioOnPcAsync(string callId, bool onPc) => _calls.SetAudioOnPcAsync(callId, onPc);
    public Task<bool> MediaCommandAsync(AmsRemoteCommandId command) => _ams.CommandAsync(command);

    public async Task<bool> SendMessageAsync(string address, string text, CancellationToken ct = default)
    {
        var local = Messages.AddOutgoing(address, null, text);
        try
        {
            if (!_map.IsConnected) throw new InvalidOperationException("MAP not connected");
            var handle = await _map.PushMessageAsync(address, text, null, ct);
            Messages.SetStatus(local.Handle, MessageStatus.Sent, handle);
            return true;
        }
        catch (Exception)
        {
            Messages.SetStatus(local.Handle, MessageStatus.Failed);
            return false;
        }
    }

    public async Task<int> SyncContactsAsync(CancellationToken ct = default)
    {
        if (_link.Classic is not { } classic || _importVCards is null) return 0;
        var vcf = await PbapClient.PullPhoneBookAsync(classic, ct);
        return _importVCards(vcf);
    }

    private void PublishStatus()
    {
        var name = Device?.Name;
        var features = PhoneFeatures.None;
        if (_ancsResult == GattStartResult.Ok && _ancs.IsRunning) features |= PhoneFeatures.Notifications;
        if (_amsOk) features |= PhoneFeatures.Media;
        if (_mapOk) features |= PhoneFeatures.Messages;
        if (_calls.Capability.Capability != Runtime.CallCapability.None) features |= PhoneFeatures.Calls;

        PhoneLinkStatus s;
        if (_conflict.ConflictDetected)
            s = new(PhoneLinkState.Blocked, "Phone Link bu iPhone'u kullanıyor", PhoneLinkConflictDetector.Guidance, name, PhoneLinkIssue.PhoneLinkHoldsDevice, features);
        else if (Device is null)
            s = _link.Phase == LinkPhase.NoBluetooth
                ? new(PhoneLinkState.NoBluetooth, "Bluetooth kapalı", "Windows Ayarlar → Bluetooth ve cihazlar'dan Bluetooth'u açın.", null, PhoneLinkIssue.BluetoothOff)
                : PhoneLinkStatus.NotPaired;
        else s = _link.Phase switch
        {
            LinkPhase.NoBluetooth => new(PhoneLinkState.NoBluetooth, "Bluetooth kapalı", "Windows Ayarlar → Bluetooth ve cihazlar'dan Bluetooth'u açın.", name, PhoneLinkIssue.BluetoothOff),
            LinkPhase.Connecting or LinkPhase.Idle => new(PhoneLinkState.Connecting, $"{name} bağlanıyor…", "iPhone'un kilidini açın ve yakında tutun.", name),
            LinkPhase.Lost => new(PhoneLinkState.Reconnecting, "Bağlantı koptu, yeniden deneniyor…", "iPhone menzil dışında ya da Bluetooth kapalı olabilir.", name, Features: features),
            LinkPhase.NoLeLink => new(PhoneLinkState.Disconnected, "Bildirim bağlantısı kurulamadı",
                "iPhone eşleşti ama bildirim (LE) bağlantısı açılmadı. \"Bildirim bağlantısını kur\"a basın ve iPhone'u yanınızda tutun.",
                name, PhoneLinkIssue.NoLeLink, features),
            _ when _ancsResult == GattStartResult.ServiceMissing => new(PhoneLinkState.Connected, $"{name} bağlı",
                "Bildirimler kapalı: iPhone'da Ayarlar → Bluetooth → bu bilgisayarın yanındaki (i) → \"Sistem Bildirimlerini Paylaş\"ı açın.",
                name, PhoneLinkIssue.NotificationsNotShared, features),
            _ when _mapIssue == PhoneLinkIssue.MessagesNotShared => new(PhoneLinkState.Connected, $"{name} bağlı",
                "Mesajlar kapalı: iPhone'da bu bilgisayarın (i) ekranında \"Bildirimleri Göster\"i açın.", name, PhoneLinkIssue.MessagesNotShared, features),
            _ => new(PhoneLinkState.Connected, $"{name} bağlı", null, name, PhoneLinkIssue.None, features),
        };
        if (s == Status) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    private static async Task<T> Safe<T>(Func<Task<T>> f, T fallback)
    {
        try { return await f(); }
        catch (Exception) { return fallback; }
    }

    private State? LoadState()
    {
        try { return _stateFile is not null && File.Exists(_stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile)) : null; }
        catch (Exception) { return null; }
    }

    private void SaveState(State s)
    {
        if (_stateFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(s));
        }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _calls.DisposeAsync();
        await _ancs.DisposeAsync();
        await _ams.DisposeAsync();
        await _map.DisposeAsync();
        await _mns.DisposeAsync();
        await _link.DisposeAsync();
        _attach.Dispose();
    }
}
