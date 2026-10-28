using System.IO.Pipelines;
using System.Text;
using AirDrop.App.Services;
using AirDrop.App.ViewModels;
using Nitro.Continuity.Ancs;
using Nitro.Continuity.Bluetooth;
using Nitro.Continuity.Map;
using Nitro.Continuity.Obex;
using Nitro.Continuity.Runtime;
using Nitro.Sync;
using CallCapability = Nitro.Continuity.Runtime.CallCapability;

namespace AirDrop.Tests.Continuity;

/// <summary>"Telefon" tab: view model over the demo service, call capability decisions, MNS → messages, continuation.</summary>
public class PhoneRuntimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(3));

    private static (DemoContinuityService Svc, PhoneViewModel Vm) Make(CallCapability cap = CallCapability.ControlOnly, bool paired = true,
        IUrlOpener? opener = null)
    {
        var svc = new DemoContinuityService(autoplay: false, capability: cap, paired: paired);
        var vm = new PhoneViewModel(svc, new DemoPhoneContacts(), a => a(), opener, new PhoneSettings());
        return (svc, vm);
    }

    private sealed class FakeOpener(bool appInstalled) : IUrlOpener
    {
        public List<string> Web { get; } = new();
        public List<string> Apps { get; } = new();
        public bool OpenWeb(string url) { Web.Add(url); return true; }
        public bool TryOpenAppUri(string uri) { Apps.Add(uri); return appInstalled; }
    }

    // ---- view model ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unpaired_ShowsSetupGuide()
    {
        var (_, vm) = Make(paired: false);
        await vm.StartAsync();
        Assert.True(vm.NeedsSetup);
        Assert.True(vm.IsGuideStep);
        Assert.Equal("off", vm.StatusKind);
        Assert.Equal(3, PhoneViewModel.IosToggles.Count);
    }

    [Fact]
    public async Task Paired_SeedsConversationsNotificationsCalls()
    {
        var (_, vm) = Make();
        await vm.StartAsync();
        Assert.False(vm.NeedsSetup);
        Assert.Equal("ok", vm.StatusKind);
        Assert.Equal(3, vm.Conversations.Count);
        Assert.Equal("Annem", vm.Conversations[0].DisplayName);       // newest first, contact name resolved
        Assert.Equal(1, vm.UnreadMessages);
        Assert.Equal(4, vm.Notifications.Count);
        Assert.Equal(1, vm.MissedCalls);
        Assert.True(vm.HasNowPlaying);
        Assert.False(vm.Conversations.Single(c => c.Address == "GARANTI BBVA").CanReply);
    }

    [Fact]
    public async Task Pairing_ConfirmCodeFlow()
    {
        var (svc, vm) = Make(paired: false);
        await vm.StartAsync();
        await vm.StartPairingAsync();
        Assert.True(vm.IsPickStep);
        var phone = Assert.Single(vm.FoundPhones);
        var pairing = vm.PairAsync(phone);
        Assert.True(vm.IsConfirmStep);
        Assert.Equal("482 913", vm.PairingCode);
        vm.ConfirmCode.Execute(null);
        await pairing;
        Assert.True(vm.IsSuccessStep);
        Assert.True(svc.Paired);
        Assert.False(vm.NeedsSetup);
    }

    [Fact]
    public async Task Pairing_RejectedCode_IsError()
    {
        var (svc, vm) = Make(paired: false);
        await vm.StartAsync();
        await vm.StartPairingAsync();
        var pairing = vm.PairAsync(vm.FoundPhones[0]);
        vm.RejectCode.Execute(null);
        await pairing;
        Assert.True(vm.IsErrorStep);
        Assert.False(svc.Paired);
    }

    [Fact]
    public async Task NewNotification_RaisesToast_PreExistingDoesNot()
    {
        var (svc, vm) = Make();
        var toasts = new List<NotificationItem>();
        vm.ToastRequested += toasts.Add;
        await vm.StartAsync();
        Assert.Empty(toasts);   // seeded ones are pre-existing
        svc.AddNotification("com.burbn.instagram", "Instagram", "Instagram", null, "yeni takipçi", DateTime.Now);
        Assert.Single(toasts);
        Assert.Equal(5, vm.Notifications.Count);
        Assert.Equal("Instagram", vm.Notifications[0].AppName);
    }

    [Fact]
    public async Task NotificationRemoved_KeepsHistoryDropsActions()
    {
        var (svc, vm) = Make();
        await vm.StartAsync();
        var n = svc.AddNotification("net.whatsapp.WhatsApp", "WhatsApp", "Zeynep", null, "selam", DateTime.Now, positive: "Yanıtla");
        var item = vm.Notifications.Single(x => x.Uid == n.Uid);
        Assert.True(item.HasPositive);
        vm.NotificationPositive.Execute(item);
        Assert.Contains((n.Uid, true), svc.PerformedActions);
        svc.RemoveNotification(n.Uid);
        Assert.Contains(item, vm.Notifications);
        Assert.False(item.HasPositive);
        Assert.False(item.HasNegative);
    }

    [Fact]
    public async Task Reply_SendsAndShowsPlaceholderThenSent()
    {
        var (svc, vm) = Make();
        await vm.StartAsync();
        vm.SelectedConversation = vm.Conversations.First(c => c.DisplayName == "Annem");
        Assert.Equal(0, vm.UnreadMessages);
        vm.ReplyText = "Geliyorum";
        Assert.True(vm.SendReply.CanExecute(null));
        Assert.True(await vm.SendReplyAsync());
        Assert.Equal("", vm.ReplyText);
        Assert.Contains(("+905321112233", "Geliyorum"), svc.Sent);
        var last = vm.SelectedConversation!.Messages.Last();
        Assert.True(last.Outgoing);
        Assert.Equal("Geliyorum", last.Body);
    }

    [Fact]
    public async Task IncomingMessage_UpdatesConversationAndUnread()
    {
        var (svc, vm) = Make();
        await vm.StartAsync();
        int before = vm.Conversations.Single(c => c.DisplayName == "Zeynep Kaya").Messages.Count;
        await svc.SimulateIncomingMessageAsync("+905554443322", "Yarın 10'da?");
        var z = vm.Conversations[0];
        Assert.Equal("Zeynep Kaya", z.DisplayName);              // moved to top
        Assert.Equal(before + 1, z.Messages.Count);
        Assert.Equal("Yarın 10'da?", z.Messages.Last().Body);
        Assert.Equal(1, z.Unread);
    }

    [Fact]
    public async Task IncomingCall_ControlOnly_AncsAnswerDecline()
    {
        var (svc, vm) = Make(CallCapability.ControlOnly);
        await vm.StartAsync();
        CallViewModel? call = null;
        vm.IncomingCall += c => call = c;
        var info = svc.SimulateIncomingCall("+905321112233", null);
        Assert.NotNull(call);
        Assert.Equal("Annem", call!.DisplayName);
        Assert.True(call.IsRinging);
        Assert.False(call.CanMute);              // ANCS: control only
        Assert.False(call.CanTransferAudio);
        Assert.DoesNotContain(vm.Notifications, n => n.Category == AncsCategoryId.IncomingCall);   // call window, not a card
        call.Decline.Execute(null);
        await Task.Yield();
        Assert.Contains((info.AncsUid!.Value, false), svc.PerformedActions);
    }

    [Fact]
    public async Task IncomingCall_FullAudio_AnswerMuteTransfer()
    {
        var (svc, vm) = Make(CallCapability.FullAudio);
        await vm.StartAsync();
        Assert.True(vm.HasFullAudio);
        Assert.False(vm.HasCallCapabilityFix);
        CallViewModel? call = null;
        vm.IncomingCall += c => call = c;
        var info = svc.SimulateIncomingCall("+905427776655", null);
        Assert.True(call!.CanMute);
        await svc.AnswerCallAsync(info.Id);
        Assert.True(call.IsActive);
        await svc.SetMuteAsync(info.Id, true);
        Assert.Equal("Sesi aç", call.MuteLabel);
        bool ended = false;
        call.Ended += () => ended = true;
        svc.SimulateCallEndedRemotely(info.Id);
        Assert.True(ended);
        Assert.False(vm.HasActiveCall);
    }

    // ---- call capability policy ---------------------------------------------------------------------------------

    [Fact]
    public void Policy_FullAudio_OnlyWhenEveryStepOk()
    {
        var ok = new CallEnvironment(true, true, PhoneLineAccess.Allowed, true, true, true);
        Assert.Equal(CallCapability.FullAudio, CallCapabilityPolicy.Decide(ok).Capability);
        Assert.Equal(CallCapability.ControlOnly, CallCapabilityPolicy.Decide(ok with { Connected = false }).Capability);
        Assert.Equal(CallCapability.None, CallCapabilityPolicy.Decide(ok with { Connected = false, AncsAvailable = false }).Capability);
    }

    [Fact]
    public void Policy_Unpackaged_FallsBackToAncs_WithPackageFix()
    {
        var d = CallCapabilityPolicy.Decide(new CallEnvironment(false, AncsAvailable: true));
        Assert.Equal(CallCapability.ControlOnly, d.Capability);
        Assert.Equal(CallCapabilityPolicy.FixPackage, d.Fix);
        Assert.Equal(CallBlocker.NoPackageIdentity, d.Blocker);
        // The app's localized text (tests run in Turkish).
        Assert.Contains("paketsiz", PhoneViewModel.CapabilityReason(d));
        Assert.Contains("yanıtlayıp reddedebilirsiniz", PhoneViewModel.CapabilityReason(d));
        Assert.Contains("MSIX", PhoneViewModel.CapabilityFix(d.Blocker));
    }

    [Theory]
    [InlineData(PhoneLineAccess.DeniedBySystem, true, CallCapabilityPolicy.FixPhoneLink)]
    [InlineData(PhoneLineAccess.Allowed, false, CallCapabilityPolicy.FixPhoneLink)]   // RegisterApp refused
    public void Policy_PhoneLinkHolding_PointsToPhoneLink(PhoneLineAccess access, bool registered, string fix)
    {
        var d = CallCapabilityPolicy.Decide(new CallEnvironment(true, true, access, registered, null, true));
        Assert.Equal(CallCapability.ControlOnly, d.Capability);
        Assert.Equal(fix, d.Fix);
        Assert.Contains("Phone Link", PhoneViewModel.CapabilityFix(d.Blocker));
    }

    [Fact]
    public void Policy_NoTransport_AndError()
    {
        Assert.Contains("HFP", CallCapabilityPolicy.Decide(new CallEnvironment(true, AncsAvailable: true)).Reason);
        Assert.Contains("boom", CallCapabilityPolicy.Decide(new CallEnvironment(true, Error: "boom")).Reason);
        var noHfp = CallCapabilityPolicy.Decide(new CallEnvironment(true, AncsAvailable: true));
        Assert.Equal(CallBlocker.NoHandsFreeTransport, noHfp.Blocker);
        Assert.Contains("HFP", PhoneViewModel.CapabilityReason(noHfp));
        var error = CallCapabilityPolicy.Decide(new CallEnvironment(true, Error: "boom"));
        Assert.Equal(CallBlocker.LineError, error.Blocker);
        Assert.Contains("boom", PhoneViewModel.CapabilityReason(error));
        Assert.Contains("ANCS", PhoneViewModel.CapabilityReason(error));   // None: no notification link either
    }

    // ---- MNS → MessageStore → view model ------------------------------------------------------------------------

    [Fact]
    public async Task MnsEventReport_OverObex_AddsMessageToConversation()
    {
        var (svc, vm) = Make();
        await vm.StartAsync();

        var toServer = new Pipe();
        var toClient = new Pipe();
        var session = new MnsSession();
        var events = new List<MapEvent>();
        session.EventReport += (_, evs) => events.AddRange(evs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = session.ServeAsync(toServer.Reader.AsStream(), toClient.Writer.AsStream(), cts.Token);

        var client = new ObexClient(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var connect = await client.ConnectAsync(MapUuids.MnsTarget, cts.Token);
        Assert.Equal(ObexResponseCode.Success, connect.Response);
        Assert.True(session.Connected);

        const string xml = """
            <MAP-event-report version="1.0">
              <event type="NewMessage" handle="20000100009" folder="TELECOM/MSG/INBOX" msg_type="SMS_GSM"/>
            </MAP-event-report>
            """;
        var put = new ObexPacket { Code = (byte)ObexOpcode.PutFinal }
            .Add(ObexHeader.ConnectionId(client.ConnectionId ?? 1))
            .Add(ObexHeader.Type(MapTypes.EventReport))
            .Add(ObexHeader.ByteSeq(ObexHeaderId.EndOfBody, Encoding.UTF8.GetBytes(xml)));
        var resp = await client.PutAsync(put, cts.Token);
        Assert.Equal(ObexResponseCode.Success, resp.Response);
        var ev = Assert.Single(events);
        Assert.Equal("20000100009", ev.Handle);

        // The runtime then fetches the message (GetMessage) and folds it into the store.
        var b = new BMessage("SMS_GSM", "telecom/msg/inbox", "UNREAD", "+905069998877", null, null, "Toplantı 15'e kaydı");
        var m = await svc.Messages.ApplyEventAsync(ev, h => Task.FromResult<BMessage?>(h == "20000100009" ? b : null));
        Assert.NotNull(m);
        var conv = vm.Conversations[0];
        Assert.Equal("Ahmet Yılmaz", conv.DisplayName);
        Assert.Equal("Toplantı 15'e kaydı", conv.LastText);
        Assert.Equal(1, conv.Unread);

        // Deleting on the phone removes it again.
        await svc.Messages.ApplyEventAsync(new MapEvent("MessageDeleted", "20000100009", "TELECOM/MSG/INBOX", "SMS_GSM", null, null, null),
            _ => Task.FromResult<BMessage?>(null));
        Assert.DoesNotContain(vm.Conversations, c => c.DisplayName == "Ahmet Yılmaz");

        await client.DisconnectAsync(cts.Token);
        toServer.Writer.Complete();
        cts.Cancel();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void MnsSession_RejectsWrongTarget()
    {
        var raw = ObexPacket.Connect(MapUuids.MasTarget).Encode();
        var resp = ObexPacket.Decode(new MnsSession().Handle(raw), isConnectResponse: true);
        Assert.Equal(ObexResponseCode.NotAcceptable, resp.Response);
    }

    // ---- continuation from notification bundle id ---------------------------------------------------------------

    [Fact]
    public async Task Continue_KnownApp_TriesAppUriThenWeb()
    {
        var opener = new FakeOpener(appInstalled: false);
        var (svc, vm) = Make(opener: opener);
        await vm.StartAsync();
        var item = vm.Notifications.Single(n => n.AppId == "net.whatsapp.WhatsApp");
        Assert.True(item.CanContinue);
        Assert.Equal("WhatsApp ile aç", item.ContinueLabel);
        var t = vm.Continue(item);
        Assert.Equal("whatsapp", t!.AppKey);
        Assert.Single(opener.Apps);
        Assert.Single(opener.Web);
        Assert.StartsWith("https://", opener.Web[0]);
    }

    [Fact]
    public async Task Continue_AppInstalled_DoesNotOpenWeb()
    {
        var opener = new FakeOpener(appInstalled: true);
        var (_, vm) = Make(opener: opener);
        await vm.StartAsync();
        vm.Continue(vm.Notifications.Single(n => n.AppId == "net.whatsapp.WhatsApp"));
        Assert.Single(opener.Apps);
        Assert.Empty(opener.Web);
    }

    [Fact]
    public async Task Continue_Messages_OpensOwnConversation()
    {
        var opener = new FakeOpener(appInstalled: true);
        var (svc, vm) = Make(opener: opener);
        await vm.StartAsync();
        vm.Section = PhoneSection.Calls;
        await svc.SimulateIncomingMessageAsync("+905321112233", "Neredesin?");
        var item = vm.Notifications.First(n => n.IsMessagesApp);
        Assert.Equal("Annem", item.Title);                     // number → contact name
        var t = vm.Continue(item);
        Assert.Equal("messages", t!.AppKey);
        Assert.True(vm.IsMessages);
        Assert.Equal("Annem", vm.SelectedConversation!.DisplayName);
        Assert.Empty(opener.Apps);
    }

    [Fact]
    public async Task Continue_UnknownBundle_NoTarget()
    {
        var (svc, vm) = Make(opener: new FakeOpener(true));
        await vm.StartAsync();
        svc.AddNotification("com.example.unknown", "Bilinmeyen", "x", null, "y", DateTime.Now);
        var item = vm.Notifications.Single(n => n.AppId == "com.example.unknown");
        Assert.False(item.CanContinue);
        Assert.Null(vm.Continue(item));
    }

    // ---- link health, SDP, formatting ---------------------------------------------------------------------------

    [Fact]
    public void Backoff_DoublesAndCaps()
    {
        var b = new ReconnectBackoff();
        var s = Enumerable.Range(0, 9).Select(_ => (int)b.Next().TotalSeconds).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }, s);
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next());
    }

    [Fact]
    public void ConflictDetector_TwoAncsErrorsInAMinute()
    {
        var d = new PhoneLinkConflictDetector();
        Assert.False(d.ReportAncsStreamError(Now));
        Assert.False(d.ReportAncsStreamError(Now.AddMinutes(2)));     // first one expired
        Assert.True(d.ReportAncsStreamError(Now.AddMinutes(2.5)));
        Assert.True(d.ConflictDetected);
        d.Reset();
        Assert.True(d.ReportPhoneLineAccess(PhoneLineAccess.DeniedBySystem));
        d.Reset();
        Assert.True(d.ReportObexConnectRefused(0xD3));
    }

    [Fact]
    public void Sdp_MnsRecord_HasProfileAndFeatures()
    {
        var a = Sdp.MapMnsAttributes();
        // 0x0009: sequence { sequence { uuid16 0x1134, uint16 0x0104 } }
        Assert.Equal(new byte[] { 0x35, 0x08, 0x35, 0x06, 0x19, 0x11, 0x34, 0x09, 0x01, 0x04 }, a[Sdp.AttrBluetoothProfileDescriptorList]);
        Assert.Equal(new byte[] { 0x0A, 0, 0, 0, 0x7F }, a[Sdp.AttrMapSupportedFeatures]);
        Assert.Equal(0x25, a[Sdp.AttrServiceName][0]);   // text, 1-byte length
    }

    [Theory]
    [InlineData("+905321112233", "5321112233")]
    [InlineData("0532 111 22 33", "5321112233")]
    [InlineData("GARANTI BBVA", "garanti bbva")]
    public void PhoneNumberKey_MatchesAcrossFormats(string input, string key) => Assert.Equal(key, PhoneNumbers.Key(input));

    [Fact]
    public void Format_WhenAndInitials()
    {
        Assert.Equal("Az önce", PhoneFormat.When(Now.AddSeconds(-20), Now));
        Assert.Equal("5 dk önce", PhoneFormat.When(Now.AddMinutes(-5), Now));
        Assert.Equal("ZK", PhoneFormat.Initials("Zeynep Kaya"));
        Assert.Equal("", PhoneFormat.Initials("+90 532 111 22 33"));
    }
}
