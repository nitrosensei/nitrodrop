using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nitro.Continuity.Ancs;
using Nitro.Continuity.Map;
using Nitro.Continuity.Runtime;

namespace AirDrop.App.Services;

// No WPF types here: linked into AirDrop.Tests.

/// <summary>
/// Fake iPhone for <c>--demo</c> / <c>--snapshot</c> and the tests: seeded notifications, messages, calls and now playing,
/// plus Simulate* methods. With <c>autoplay</c> it rings once and receives a message a few seconds after start.
/// Calls behave like the real ones: <see cref="Capability"/> picks FullAudio (PhoneLine-like) or ControlOnly (ANCS-like).
/// </summary>
public sealed class DemoContinuityService : IContinuityService
{
    private readonly bool _autoplay;
    private readonly Dictionary<string, PhoneCallInfo> _calls = new();
    private readonly List<PhoneNotification> _notifications = new();
    private uint _uid = 100;
    private int _callSeq;
    private CancellationTokenSource? _cts;

    public DemoContinuityService(bool autoplay = false, CallCapability capability = Nitro.Continuity.Runtime.CallCapability.ControlOnly, bool paired = true)
    {
        _autoplay = autoplay;
        Capability = capability;
        Paired = paired;
    }

    public CallCapability Capability { get; set; }
    public bool Paired { get; private set; }
    public List<(uint Uid, bool Positive)> PerformedActions { get; } = new();
    public List<(string Address, string Text)> Sent { get; } = new();
    public List<AmsRemoteCommandId> MediaCommands { get; } = new();
    /// <summary>When false, SendMessageAsync fails (to exercise the "Gönderilemedi" state).</summary>
    public bool SendSucceeds { get; set; } = true;

    public PhoneLinkStatus Status { get; private set; } = PhoneLinkStatus.NotPaired;
    public CallCapabilityInfo CallCapability { get; private set; } = CallCapabilityInfo.Unknown;
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

    /// <summary>What the demo iPhone's Device Information Service reports (iPhone 15 Pro).</summary>
    public const string DemoModelId = "iPhone16,1";

    public static readonly PhoneDevice DemoPhone = new("demo-iphone", "Emre'nin iPhone'u", 0xA4C3F0112233, true, true);

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!Paired) { SetStatus(PhoneLinkStatus.NotPaired); return Task.CompletedTask; }
        Connect();
        Seed();
        if (_autoplay)
        {
            _cts = new CancellationTokenSource();
            _ = AutoplayAsync(_cts.Token);
        }
        return Task.CompletedTask;
    }

    private void Connect()
    {
        Device = DemoPhone;
        SetStatus(new PhoneLinkStatus(PhoneLinkState.Connected, $"{DemoPhone.Name} bağlı", null, DemoPhone.Name, PhoneLinkIssue.None,
            PhoneFeatures.Notifications | PhoneFeatures.Messages | PhoneFeatures.Media | PhoneFeatures.Calls | PhoneFeatures.Contacts,
            DemoModelId));
        CallCapability = CallCapabilityPolicy.Decide(Capability == Nitro.Continuity.Runtime.CallCapability.FullAudio
            ? new CallEnvironment(true, true, PhoneLineAccess.Allowed, true, true, true)
            : new CallEnvironment(false, AncsAvailable: true));
        CallCapabilityChanged?.Invoke(CallCapability);
    }

    private void SetStatus(PhoneLinkStatus s) { Status = s; StatusChanged?.Invoke(s); }

    private void Seed()
    {
        var now = DateTime.Now;
        AddNotification("net.whatsapp.WhatsApp", "WhatsApp", "Zeynep Kaya", null, "Akşam 8'de Kadıköy'de buluşalım mı?", now.AddMinutes(-2),
            preExisting: true, AncsCategoryId.Social);
        AddNotification("com.burbn.instagram", "Instagram", "Instagram", null, "ahmet.yilmaz gönderini beğendi.", now.AddMinutes(-11),
            preExisting: true, AncsCategoryId.Social);
        AddNotification("com.apple.mobilecal", "Takvim", "Nitrobyte haftalık", "15:00 – 15:30", "Toplantı 25 dakika sonra başlıyor.", now.AddMinutes(-25),
            preExisting: true, AncsCategoryId.Schedule);
        AddNotification("com.apple.mobilemail", "Mail", "Turkcell", "Ekim faturanız hazır", "Fatura tutarınız 249,90 TL. Son ödeme 5 Kasım.", now.AddHours(-2),
            preExisting: true, AncsCategoryId.Email);

        var t = DateTimeOffset.Now;
        Messages.Upsert(new PhoneMessage("m1", "+905321112233", null, "Eve gelirken ekmek alır mısın?", t.AddMinutes(-48), false, true));
        Messages.Upsert(new PhoneMessage("m2", "+905321112233", null, "Tamam, 7 gibi oradayım.", t.AddMinutes(-46), true, true, MessageStatus.Sent));
        Messages.Upsert(new PhoneMessage("m3", "+905321112233", null, "Annenler de geliyor bu arada", t.AddMinutes(-5), false, false));
        Messages.Upsert(new PhoneMessage("m4", "+905554443322", null, "Sunum dosyasını attım, bakabilir misin?", t.AddHours(-3), false, true));
        Messages.Upsert(new PhoneMessage("m5", "+905554443322", null, "Baktım, harika olmuş 👍", t.AddHours(-3).AddMinutes(4), true, true, MessageStatus.Sent));
        Messages.Upsert(new PhoneMessage("m6", "GARANTI BBVA", "GARANTI BBVA", "Doğrulama kodunuz: 482913. Kimseyle paylaşmayın.", t.AddHours(-6), false, true));

        CallLogged?.Invoke(new RecentCall("+905069998877", null, CallDirection.Incoming, CallOutcome.Missed, t.AddMinutes(-35), TimeSpan.Zero));
        CallLogged?.Invoke(new RecentCall("+905321112233", null, CallDirection.Incoming, CallOutcome.Answered, t.AddHours(-4), TimeSpan.FromMinutes(6.4)));
        CallLogged?.Invoke(new RecentCall("+905427776655", null, CallDirection.Outgoing, CallOutcome.Outgoing, t.AddDays(-1), TimeSpan.FromMinutes(2.1)));
        CallLogged?.Invoke(new RecentCall("+902125550000", null, CallDirection.Incoming, CallOutcome.Declined, t.AddDays(-1).AddHours(-3), TimeSpan.Zero));

        NowPlaying = new NowPlaying("Bir Derdim Var", "mor ve ötesi", "Dünya Yalan Söylüyor", "Spotify", true, 254, 81);
        NowPlayingChanged?.Invoke(NowPlaying);
    }

    private async Task AutoplayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(4000, ct);
            SimulateIncomingCall("+905321112233", null);
            await Task.Delay(9000, ct);
            await SimulateIncomingMessageAsync("+905554443322", "Yarın 10'da ofiste misin?");
        }
        catch (OperationCanceledException) { }
    }

    public PhoneNotification AddNotification(string appId, string appName, string title, string? subtitle, string message, DateTime date,
        bool preExisting = false, AncsCategoryId category = AncsCategoryId.Social, string? positive = null, string? negative = "Sil")
    {
        var flags = AncsEventFlags.NegativeAction | (preExisting ? AncsEventFlags.PreExisting : 0) | (positive is null ? 0 : AncsEventFlags.PositiveAction);
        var n = new PhoneNotification
        {
            Uid = ++_uid, Category = category, Flags = flags, AppIdentifier = appId, AppDisplayName = appName, Title = title,
            Subtitle = subtitle, Message = message, Date = date, PositiveActionLabel = positive, NegativeActionLabel = negative, AttributesLoaded = true,
        };
        _notifications.Add(n);
        NotificationAdded?.Invoke(n);
        return n;
    }

    /// <summary>Rings like the real service: FullAudio → PhoneLine-style call, ControlOnly → ANCS IncomingCall notification + call.</summary>
    public PhoneCallInfo SimulateIncomingCall(string? number, string? name)
    {
        uint? uid = null;
        if (Capability != Nitro.Continuity.Runtime.CallCapability.FullAudio)
        {
            var n = AddNotification("com.apple.mobilephone", "Telefon", name ?? number ?? "Bilinmeyen", null, "Gelen arama", DateTime.Now,
                category: AncsCategoryId.IncomingCall, positive: "Yanıtla", negative: "Reddet");
            uid = n.Uid;
        }
        var call = new PhoneCallInfo
        {
            Id = uid is { } u ? $"ancs-{u}" : $"demo-{++_callSeq}", Number = number, Name = name, State = CallState.Ringing,
            Source = Capability == Nitro.Continuity.Runtime.CallCapability.FullAudio ? CallSource.Demo : CallSource.Ancs, AncsUid = uid,
            StartedAt = DateTimeOffset.Now,
        };
        _calls[call.Id] = call;
        CallUpdated?.Invoke(call);
        return call;
    }

    public void SimulateCallEndedRemotely(string callId)
    {
        if (!_calls.Remove(callId, out var c)) return;
        CallUpdated?.Invoke(c with { State = CallState.Ended });
        CallLogged?.Invoke(new RecentCall(c.Number, c.Name, c.Direction, c.ConnectedAt is null ? CallOutcome.Missed : CallOutcome.Answered,
            c.StartedAt, c.ConnectedAt is { } t ? DateTimeOffset.Now - t : TimeSpan.Zero));
        RemoveCallNotification(c);
    }

    /// <summary>Like an MNS NewMessage event followed by GetMessage.</summary>
    public Task<PhoneMessage?> SimulateIncomingMessageAsync(string from, string text)
    {
        string handle = $"h{++_uid}";
        var b = new BMessage("SMS_GSM", "telecom/msg/inbox", "UNREAD", from, null, null, text);
        AddNotification("com.apple.MobileSMS", "Mesajlar", from, null, text, DateTime.Now, category: AncsCategoryId.Social);
        return Messages.ApplyEventAsync(new MapEvent("NewMessage", handle, "telecom/msg/inbox", "SMS_GSM", null, DateTime.Now, null),
            _ => Task.FromResult<BMessage?>(b));
    }

    public void SimulateModified(PhoneNotification n) => NotificationModified?.Invoke(n);

    public void SimulateNowPlaying(NowPlaying? np) { NowPlaying = np; NowPlayingChanged?.Invoke(np); }

    public void SimulateStatus(PhoneLinkStatus s) => SetStatus(s);

    public void RemoveNotification(uint uid)
    {
        var n = _notifications.FirstOrDefault(x => x.Uid == uid);
        if (n is null) return;
        _notifications.Remove(n);
        NotificationRemoved?.Invoke(n);
    }

    private void RemoveCallNotification(PhoneCallInfo c) { if (c.AncsUid is { } u) RemoveNotification(u); }

    // ---- IContinuityService -------------------------------------------------------------------------------------

    public Task<IReadOnlyList<PhoneDevice>> GetPairedPhonesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PhoneDevice>>(Paired ? [DemoPhone] : []);

    public async Task<IReadOnlyList<PhoneDevice>> DiscoverPhonesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1200, timeout.TotalMilliseconds)), ct);
        return [DemoPhone with { IsPaired = false, IsConnected = false }];
    }

    public async Task<PairingResult> PairAsync(PhoneDevice device, IPairingPrompt prompt, CancellationToken ct = default)
    {
        SetStatus(new PhoneLinkStatus(PhoneLinkState.Pairing, "Eşleştiriliyor…", "iPhone'da çıkan isteğe \"Eşleştir\" ile yanıt verin.", device.Name));
        bool ok = await prompt.ConfirmAsync(PairingKind.ConfirmPinMatch, device.Name, "482 913", ct);
        if (!ok)
        {
            SetStatus(PhoneLinkStatus.NotPaired);
            return new PairingResult(PairingOutcome.Rejected, "Eşleşme reddedildi.");
        }
        Paired = true;
        await UseDeviceAsync(device, ct);
        return new PairingResult(PairingOutcome.Paired, $"{device.Name} eşleşti.");
    }

    public Task UseDeviceAsync(PhoneDevice device, CancellationToken ct = default)
    {
        Paired = true;
        Connect();
        return Task.CompletedTask;
    }

    public Task<PairingResult> EstablishNotificationLinkAsync(IPairingPrompt prompt, CancellationToken ct = default) =>
        Task.FromResult(new PairingResult(PairingOutcome.Paired, "Bildirim bağlantısı kuruldu."));

    public Task DisconnectAsync()
    {
        Device = null;
        Paired = false;
        Messages.Clear();
        SetStatus(PhoneLinkStatus.NotPaired);
        CallCapability = CallCapabilityInfo.Unknown;
        CallCapabilityChanged?.Invoke(CallCapability);
        return Task.CompletedTask;
    }

    public Task<bool> PerformNotificationActionAsync(uint uid, bool positive)
    {
        PerformedActions.Add((uid, positive));
        if (!positive) RemoveNotification(uid);
        return Task.FromResult(true);
    }

    public Task<bool> AnswerCallAsync(string callId)
    {
        if (!_calls.TryGetValue(callId, out var c) || c.State != CallState.Ringing) return Task.FromResult(false);
        if (c.AncsUid is { } uid) PerformedActions.Add((uid, true));
        RemoveCallNotification(c);
        var active = c with { State = CallState.Active, ConnectedAt = DateTimeOffset.Now, AudioOnPc = c.Source != CallSource.Ancs };
        _calls[callId] = active;
        CallUpdated?.Invoke(active);
        return Task.FromResult(true);
    }

    public Task<bool> DeclineCallAsync(string callId)
    {
        if (!_calls.Remove(callId, out var c)) return Task.FromResult(false);
        if (c.AncsUid is { } uid) PerformedActions.Add((uid, false));
        RemoveCallNotification(c);
        CallUpdated?.Invoke(c with { State = CallState.Ended });
        CallLogged?.Invoke(new RecentCall(c.Number, c.Name, c.Direction, CallOutcome.Declined, c.StartedAt, TimeSpan.Zero));
        return Task.FromResult(true);
    }

    public Task<bool> EndCallAsync(string callId)
    {
        if (!_calls.ContainsKey(callId)) return Task.FromResult(false);
        SimulateCallEndedRemotely(callId);
        return Task.FromResult(true);
    }

    public Task<bool> SetMuteAsync(string callId, bool muted) => Update(callId, c => c.CanMute ? c with { Muted = muted } : null);

    public Task<bool> SetAudioOnPcAsync(string callId, bool onPc) => Update(callId, c => c.CanTransferAudio ? c with { AudioOnPc = onPc } : null);

    private Task<bool> Update(string id, Func<PhoneCallInfo, PhoneCallInfo?> f)
    {
        if (!_calls.TryGetValue(id, out var c) || f(c) is not { } n) return Task.FromResult(false);
        _calls[id] = n;
        CallUpdated?.Invoke(n);
        return Task.FromResult(true);
    }

    public Task<bool> SendMessageAsync(string address, string text, CancellationToken ct = default)
    {
        var local = Messages.AddOutgoing(address, null, text);
        Sent.Add((address, text));
        Messages.SetStatus(local.Handle, SendSucceeds ? MessageStatus.Sent : MessageStatus.Failed, SendSucceeds ? $"s{++_uid}" : null);
        return Task.FromResult(SendSucceeds);
    }

    public Task<bool> MediaCommandAsync(AmsRemoteCommandId command)
    {
        MediaCommands.Add(command);
        if (NowPlaying is { } np && command is AmsRemoteCommandId.TogglePlayPause or AmsRemoteCommandId.Play or AmsRemoteCommandId.Pause)
            SimulateNowPlaying(np with { IsPlaying = command switch { AmsRemoteCommandId.Play => true, AmsRemoteCommandId.Pause => false, _ => !np.IsPlaying } });
        return Task.FromResult(true);
    }

    public Task<int> SyncContactsAsync(CancellationToken ct = default) => Task.FromResult(4);

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        return ValueTask.CompletedTask;
    }
}
