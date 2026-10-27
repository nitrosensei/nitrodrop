using Nitro.Continuity.Ancs;
using Nitro.Continuity.Runtime;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using PhoneCallInfo = Nitro.Continuity.Runtime.PhoneCallInfo;

namespace Nitro.Continuity.Platform;

/// <summary>
/// Calls. Preferred path (docs/continuity-plan.md §9): Windows' inbox Hands-Free through PhoneLine — needs package
/// identity + rescap:phoneLineTransportManagement + uap:phoneCall, then audio can be on the PC. Fallback: ANCS
/// IncomingCall actions (answer/decline from the PC, conversation stays on the iPhone). <see cref="Capability"/> says
/// which one is live and why.
/// </summary>
public sealed class CallService : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PhoneCallInfo> _calls = new();
    private readonly Dictionary<string, PhoneCall> _lineCalls = new();
    private readonly Func<uint, bool, Task<bool>> _ancsAction;
    private PhoneLineTransportDevice? _transport;
    private PhoneLine? _line;
    private PhoneLineWatcher? _watcher;
    private bool _ancsAvailable;
    private CallEnvironment _env = new(PackageIdentity.HasIdentity);

    public CallService(Func<uint, bool, Task<bool>> ancsAction) => _ancsAction = ancsAction;

    public CallCapabilityInfo Capability { get; private set; } = CallCapabilityInfo.Unknown;
    public PhoneLineAccess LastAccess => _env.Access;

    public event Action<CallCapabilityInfo>? CapabilityChanged;
    public event Action<PhoneCallInfo>? CallUpdated;
    public event Action<RecentCall>? CallLogged;

    public void SetAncsAvailable(bool available)
    {
        _ancsAvailable = available;
        Publish(_env with { AncsAvailable = available });
    }

    /// <summary>
    /// Walks the PhoneLine steps for the phone with <paramref name="bluetoothAddress"/>: identity → transport →
    /// RequestAccessAsync → RegisterApp → ConnectAsync → PhoneLineWatcher. Unpackaged it stops at step one (no calls into
    /// PhoneLineTransportDevice at all, so nothing is registered and Phone Link is left alone).
    /// </summary>
    public async Task EvaluateAsync(ulong bluetoothAddress, CancellationToken ct = default)
    {
        var env = new CallEnvironment(PackageIdentity.HasIdentity, AncsAvailable: _ancsAvailable);
        if (!env.HasPackageIdentity) { Publish(env); return; }
        try
        {
            var infos = await DeviceInformation.FindAllAsync(PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth),
                ["System.DeviceInterface.Bluetooth.DeviceAddress"]).AsTask(ct);
            var info = infos.FirstOrDefault(i => i.Properties.TryGetValue("System.DeviceInterface.Bluetooth.DeviceAddress", out var a)
                                                 && a is string s && IphonePairing.ParseAddress(s) == bluetoothAddress)
                       ?? (infos.Count == 1 ? infos[0] : null);
            if (info is null) { Publish(env); return; }
            _transport = PhoneLineTransportDevice.FromId(info.Id);
            env = env with { TransportFound = true };

            var access = await _transport.RequestAccessAsync().AsTask(ct);
            env = env with { Access = access switch
            {
                DeviceAccessStatus.Allowed => PhoneLineAccess.Allowed,
                DeviceAccessStatus.DeniedByUser => PhoneLineAccess.DeniedByUser,
                DeviceAccessStatus.DeniedBySystem => PhoneLineAccess.DeniedBySystem,
                _ => PhoneLineAccess.Unspecified,
            } };
            if (env.Access != PhoneLineAccess.Allowed) { Publish(env); return; }

            if (!_transport.IsRegistered()) _transport.RegisterApp();
            env = env with { Registered = _transport.IsRegistered() };
            if (env.Registered != true) { Publish(env); return; }

            env = env with { Connected = await _transport.ConnectAsync().AsTask(ct) };
            if (env.Connected == true) await StartLineWatcherAsync(info.Id, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            env = env with { Error = $"0x{e.HResult:X8}" };
        }
        Publish(env);
    }

    private void Publish(CallEnvironment env)
    {
        _env = env;
        var cap = CallCapabilityPolicy.Decide(env);
        if (cap == Capability) return;
        Capability = cap;
        CapabilityChanged?.Invoke(cap);
    }

    private async Task StartLineWatcherAsync(string transportId, CancellationToken ct)
    {
        var store = await PhoneCallManager.RequestStoreAsync().AsTask(ct);
        _watcher = store.RequestLineWatcher();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.LineAdded += async (_, a) =>
        {
            try
            {
                var l = await PhoneLine.FromIdAsync(a.LineId);
                if (l.Transport == PhoneLineTransport.Bluetooth && (l.TransportDeviceId == transportId || _line is null))
                {
                    _line = l;
                    await RefreshCallsAsync();
                }
            }
            catch (Exception) { }
        };
        _watcher.EnumerationCompleted += (_, _) => done.TrySetResult();
        _watcher.Stopped += (_, _) => done.TrySetResult();
        _watcher.Start();
        await Task.WhenAny(done.Task, Task.Delay(8000, ct));
        PhoneCallManager.CallStateChanged += OnSystemCallStateChanged;
    }

    private async void OnSystemCallStateChanged(object? sender, object e)
    {
        try { await RefreshCallsAsync(); } catch (Exception) { }
    }

    private async Task RefreshCallsAsync()
    {
        if (_line is null) return;
        var r = await _line.GetAllActivePhoneCallsAsync();
        if (r.OperationStatus != PhoneLineOperationStatus.Succeeded) return;
        foreach (var call in r.AllActivePhoneCalls)
        {
            bool isNew;
            lock (_lock)
            {
                isNew = !_lineCalls.ContainsKey(call.CallId);
                if (isNew) _lineCalls[call.CallId] = call;
            }
            if (isNew)
            {
                call.StatusChanged += (c, _) => UpdateFromLine(c);
                call.IsMutedChanged += (c, _) => UpdateFromLine(c);
                call.AudioDeviceChanged += (c, _) => UpdateFromLine(c);
            }
            UpdateFromLine(call);
        }
    }

    private void UpdateFromLine(PhoneCall c)
    {
        PhoneCallInfo info;
        PhoneCallInfo? before;
        lock (_lock)
        {
            _calls.TryGetValue(c.CallId, out before);
            var meta = SafeInfo(c);
            var state = c.Status switch
            {
                PhoneCallStatus.Incoming => CallState.Ringing,
                PhoneCallStatus.Dialing => CallState.Dialing,
                PhoneCallStatus.Talking => CallState.Active,
                PhoneCallStatus.Held => CallState.Held,
                _ => CallState.Ended,
            };
            info = (before ?? new PhoneCallInfo
            {
                Id = c.CallId, Source = CallSource.PhoneLine,
                Direction = meta?.CallDirection == PhoneCallDirection.Outgoing ? CallDirection.Outgoing : CallDirection.Incoming,
                StartedAt = meta?.StartTime ?? DateTimeOffset.Now,
            }) with
            {
                Number = meta?.PhoneNumber ?? before?.Number,
                Name = meta?.DisplayName is { Length: > 0 } dn ? dn : before?.Name,
                State = state,
                ConnectedAt = before?.ConnectedAt ?? (state == CallState.Active ? DateTimeOffset.Now : null),
                Muted = c.IsMuted,
                AudioOnPc = c.AudioDevice == PhoneCallAudioDevice.LocalDevice,
            };
            if (state == CallState.Ended) { _calls.Remove(c.CallId); _lineCalls.Remove(c.CallId); }
            else _calls[c.CallId] = info;
        }
        if (before == info) return;
        CallUpdated?.Invoke(info);
        if (info.State == CallState.Ended) Log(info, before);
    }

    private sealed record LineMeta(PhoneCallDirection CallDirection, string? PhoneNumber, string? DisplayName, DateTimeOffset StartTime);

    private static LineMeta? SafeInfo(PhoneCall c)
    {
        try
        {
            var i = c.GetPhoneCallInfo();
            return new(i.CallDirection, i.PhoneNumber, i.DisplayName, i.StartTime);
        }
        catch (Exception) { return null; }
    }

    // ---- ANCS fallback -------------------------------------------------------------------------------------------

    /// <summary>Feed every ANCS add/modify. IncomingCall → ringing toast (unless PhoneLine owns calls); MissedCall → log.</summary>
    public void OnAncsNotification(PhoneNotification n)
    {
        if (n.Category == AncsCategoryId.MissedCall && !n.IsPreExisting)
        {
            CallLogged?.Invoke(new RecentCall(LooksLikeNumber(n.Title) ? n.Title : null, LooksLikeNumber(n.Title) ? null : n.Title,
                CallDirection.Incoming, CallOutcome.Missed, n.Date is { } d ? new DateTimeOffset(d) : DateTimeOffset.Now, TimeSpan.Zero));
            return;
        }
        if (!n.IsIncomingCall || Capability.Capability == CallCapability.FullAudio) return;
        string id = AncsCallId(n.Uid);
        PhoneCallInfo info;
        lock (_lock)
        {
            if (_calls.TryGetValue(id, out var existing) && existing.State != CallState.Ringing) return;
            info = new PhoneCallInfo
            {
                Id = id, Source = CallSource.Ancs, AncsUid = n.Uid, State = CallState.Ringing,
                Name = LooksLikeNumber(n.Title) ? null : n.Title,
                Number = LooksLikeNumber(n.Title) ? n.Title : LooksLikeNumber(n.Subtitle) ? n.Subtitle : null,
                StartedAt = DateTimeOffset.Now,
            };
            _calls[id] = info;
        }
        CallUpdated?.Invoke(info);
    }

    /// <summary>IncomingCall notification gone: if it was still ringing (answered/declined on the phone, or missed) it ends.</summary>
    public void OnAncsRemoved(PhoneNotification n)
    {
        if (!n.IsIncomingCall) return;
        PhoneCallInfo? ended = null;
        lock (_lock)
        {
            var id = AncsCallId(n.Uid);
            if (_calls.TryGetValue(id, out var c) && c.State == CallState.Ringing)
            {
                ended = c with { State = CallState.Ended };
                _calls.Remove(id);
            }
        }
        if (ended is not null) CallUpdated?.Invoke(ended);   // MissedCall (if any) is logged when its own notification arrives
    }

    public static string AncsCallId(uint uid) => $"ancs-{uid}";

    // ---- actions -------------------------------------------------------------------------------------------------

    private bool TryGet(string id, out PhoneCallInfo info) { lock (_lock) return _calls.TryGetValue(id, out info!); }

    public async Task<bool> AnswerAsync(string id)
    {
        if (!TryGet(id, out var c)) return false;
        if (c.Source == CallSource.Ancs)
        {
            if (c.AncsUid is not { } uid || !await _ancsAction(uid, true)) return false;
            var active = c with { State = CallState.Active, ConnectedAt = DateTimeOffset.Now };
            lock (_lock) _calls[id] = active;
            CallUpdated?.Invoke(active);
            return true;
        }
        if (!_lineCalls.TryGetValue(id, out var pc)) return false;
        var ok = await pc.AcceptIncomingAsync() == PhoneCallOperationStatus.Succeeded;
        if (ok) await pc.ChangeAudioDeviceAsync(PhoneCallAudioDevice.LocalDevice);
        return ok;
    }

    public async Task<bool> DeclineAsync(string id)
    {
        if (!TryGet(id, out var c)) return false;
        if (c.Source == CallSource.Ancs)
        {
            if (c.AncsUid is not { } uid || !await _ancsAction(uid, false)) return false;
            Finish(id, c, CallOutcome.Declined);
            return true;
        }
        return _lineCalls.TryGetValue(id, out var pc) && await pc.RejectIncomingAsync() == PhoneCallOperationStatus.Succeeded;
    }

    /// <summary>PhoneLine: hang up. ANCS: cannot hang up an active call — closes our view of it (the call continues on the phone).</summary>
    public async Task<bool> EndAsync(string id)
    {
        if (!TryGet(id, out var c)) return false;
        if (c.Source == CallSource.Ancs) { Finish(id, c, CallOutcome.Answered); return true; }
        return _lineCalls.TryGetValue(id, out var pc) && await pc.EndAsync() == PhoneCallOperationStatus.Succeeded;
    }

    public async Task<bool> SetMuteAsync(string id, bool muted)
    {
        if (!_lineCalls.TryGetValue(id, out var pc)) return false;
        return (muted ? await pc.MuteAsync() : await pc.UnmuteAsync()) == PhoneCallOperationStatus.Succeeded;
    }

    public async Task<bool> SetAudioOnPcAsync(string id, bool onPc)
    {
        if (!_lineCalls.TryGetValue(id, out var pc)) return false;
        return await pc.ChangeAudioDeviceAsync(onPc ? PhoneCallAudioDevice.LocalDevice : PhoneCallAudioDevice.RemoteDevice)
               == PhoneCallOperationStatus.Succeeded;
    }

    private void Finish(string id, PhoneCallInfo c, CallOutcome outcome)
    {
        var ended = c with { State = CallState.Ended };
        lock (_lock) _calls.Remove(id);
        CallUpdated?.Invoke(ended);
        CallLogged?.Invoke(new RecentCall(c.Number, c.Name, c.Direction, outcome, c.StartedAt,
            c.ConnectedAt is { } t ? DateTimeOffset.Now - t : TimeSpan.Zero));
    }

    private void Log(PhoneCallInfo ended, PhoneCallInfo? before)
    {
        var outcome = ended.Direction == CallDirection.Outgoing ? CallOutcome.Outgoing
            : before?.ConnectedAt is null ? CallOutcome.Missed : CallOutcome.Answered;
        CallLogged?.Invoke(new RecentCall(ended.Number, ended.Name, ended.Direction, outcome, ended.StartedAt,
            ended.ConnectedAt is { } t ? DateTimeOffset.Now - t : TimeSpan.Zero));
    }

    private static bool LooksLikeNumber(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Count(char.IsAsciiDigit) >= 5 && s.All(ch => char.IsAsciiDigit(ch) || " +()-".Contains(ch));

    public ValueTask DisposeAsync()
    {
        PhoneCallManager.CallStateChanged -= OnSystemCallStateChanged;
        try { _watcher?.Stop(); } catch (Exception) { }
        _watcher = null;
        return ValueTask.CompletedTask;
    }
}
