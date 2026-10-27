using System.Text;
using Nitro.Continuity.Ancs;

namespace AirDrop.Tests.Continuity;

// Byte-level vectors built field-by-field from the layouts in Apple's ANCS Specification
// (Notification Source fig. "Format of GATT notifications delivered through the Notification Source characteristic",
// Get Notification Attributes / Get App Attributes / Perform Notification Action command & response formats).
// The spec gives layouts, not hex dumps, so each vector below spells out the fields it is made of.
public class AncsTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    [Fact]
    public void NotificationSource_IncomingCall_Parses()
    {
        // EventID=Added(00) Flags=Positive|Negative(18) Category=IncomingCall(01) Count=1 UID=0x0000002A (LE)
        var ev = AncsNotificationEvent.Parse(H("00 18 01 01 2A000000"));
        Assert.Equal(AncsEventId.NotificationAdded, ev.EventId);
        Assert.Equal(AncsEventFlags.PositiveAction | AncsEventFlags.NegativeAction, ev.Flags);
        Assert.Equal(AncsCategoryId.IncomingCall, ev.Category);
        Assert.Equal(1, ev.CategoryCount);
        Assert.Equal(42u, ev.NotificationUid);
        Assert.Equal(H("00 18 01 01 2A000000"), ev.ToBytes());
    }

    [Fact]
    public void NotificationSource_RemovedPreExistingSocial_Parses()
    {
        // Removed(02), PreExisting|Silent(05), Social(04), count 3, UID 0x12345678
        var ev = AncsNotificationEvent.Parse(H("02 05 04 03 78563412"));
        Assert.Equal(AncsEventId.NotificationRemoved, ev.EventId);
        Assert.True(ev.Flags.HasFlag(AncsEventFlags.PreExisting));
        Assert.True(ev.Flags.HasFlag(AncsEventFlags.Silent));
        Assert.Equal(0x12345678u, ev.NotificationUid);
    }

    [Fact]
    public void NotificationSource_TooShort_Throws() =>
        Assert.Throws<FormatException>(() => AncsNotificationEvent.Parse(H("00 18 01")));

    [Fact]
    public void GetNotificationAttributes_EncodesMaxLengthOnlyForTextAttributes()
    {
        var cmd = AncsCommands.GetNotificationAttributes(42,
        [
            new(AncsNotificationAttributeId.AppIdentifier),
            new(AncsNotificationAttributeId.Title, 64),
            new(AncsNotificationAttributeId.Message, 512),
            new(AncsNotificationAttributeId.PositiveActionLabel),
        ]);
        // Cmd 00 | UID 2A000000 | 00 | 01 4000 | 03 0002 | 06
        Assert.Equal(H("00 2A000000 00 01 4000 03 0002 06"), cmd);
    }

    [Fact]
    public void PerformNotificationAction_AnswerCall()
    {
        Assert.Equal(H("02 2A000000 00"), AncsCommands.PerformNotificationAction(42, AncsActionId.Positive));
        Assert.Equal(H("02 2A000000 01"), AncsCommands.PerformNotificationAction(42, AncsActionId.Negative));
    }

    [Fact]
    public void GetAppAttributes_NulTerminatedIdentifier()
    {
        var cmd = AncsCommands.GetAppAttributes("com.apple.mobilephone", AncsAppAttributeId.DisplayName);
        var expected = new byte[] { 0x01 }.Concat(Encoding.ASCII.GetBytes("com.apple.mobilephone")).Concat(new byte[] { 0x00, 0x00 }).ToArray();
        Assert.Equal(expected, cmd);
    }

    static readonly AncsAttributeRequest[] CallAttrs =
    [
        new(AncsNotificationAttributeId.AppIdentifier),
        new(AncsNotificationAttributeId.Title, 64),
        new(AncsNotificationAttributeId.Message, 64),
        new(AncsNotificationAttributeId.PositiveActionLabel),
        new(AncsNotificationAttributeId.NegativeActionLabel),
    ];

    // Response: 00 | UID | {AttrID | Len(LE16) | UTF-8}*
    static byte[] CallResponse() => H(
        "00 2A000000" +
        "00 1500" + Convert.ToHexString(Encoding.UTF8.GetBytes("com.apple.mobilephone")) +
        "01 0400" + Convert.ToHexString(Encoding.UTF8.GetBytes("Anne")) +
        "03 0000" +
        "06 0600" + Convert.ToHexString(Encoding.UTF8.GetBytes("Answer")) +
        "07 0700" + Convert.ToHexString(Encoding.UTF8.GetBytes("Decline")));

    [Fact]
    public void DataSource_ResponseSplitAcrossNotifications_IncludingInsideLengthField()
    {
        var resp = CallResponse();
        var a = new AncsDataSourceAssembler();
        a.ExpectNotificationAttributes(42, CallAttrs);

        // Split: after cmd+uid+attrId+first length byte, then in the middle of the title, then the rest.
        int s1 = 7, s2 = 30;
        Assert.Empty(a.Feed(resp.AsSpan(0, s1)));
        Assert.Empty(a.Feed(resp.AsSpan(s1, s2 - s1)));
        var done = a.Feed(resp.AsSpan(s2));
        var r = Assert.IsType<AncsNotificationAttributesResponse>(Assert.Single(done));
        Assert.Equal(42u, r.NotificationUid);
        Assert.Equal("com.apple.mobilephone", r[AncsNotificationAttributeId.AppIdentifier]);
        Assert.Equal("", r[AncsNotificationAttributeId.Message]);
        Assert.Equal("Answer", r[AncsNotificationAttributeId.PositiveActionLabel]);
        Assert.Equal("Decline", r[AncsNotificationAttributeId.NegativeActionLabel]);
        Assert.Equal(0, a.PendingCount);
    }

    [Fact]
    public void DataSource_TitleWithTurkishChars_Utf8()
    {
        var title = Encoding.UTF8.GetBytes("Ayşe");   // 5 bytes: ş = C5 9F
        var resp = H("00 01000000 01 0500" + Convert.ToHexString(title));
        var a = new AncsDataSourceAssembler();
        a.ExpectNotificationAttributes(1, [new(AncsNotificationAttributeId.Title, 32)]);
        var r = (AncsNotificationAttributesResponse)Assert.Single(a.Feed(resp));
        Assert.Equal("Ayşe", r[AncsNotificationAttributeId.Title]);
    }

    [Fact]
    public void DataSource_TwoResponsesInOneChunk_AndAppAttributes()
    {
        var first = H("00 01000000 01 0200 4869");                         // Title "Hi"
        var app = new byte[] { 0x01 }.Concat(Encoding.ASCII.GetBytes("com.whatsapp.WhatsApp")).Concat(H("00 00 0800"))
            .Concat(Encoding.ASCII.GetBytes("WhatsApp")).ToArray();
        var a = new AncsDataSourceAssembler();
        a.ExpectNotificationAttributes(1, [new(AncsNotificationAttributeId.Title, 8)]);
        a.ExpectAppAttributes("com.whatsapp.WhatsApp", 1);
        var done = a.Feed(first.Concat(app).ToArray());
        Assert.Equal(2, done.Count);
        var aa = Assert.IsType<AncsAppAttributesResponse>(done[1]);
        Assert.Equal("com.whatsapp.WhatsApp", aa.AppIdentifier);
        Assert.Equal("WhatsApp", aa.Attributes[AncsAppAttributeId.DisplayName]);
    }

    [Fact]
    public void DataSource_UnexpectedCommandId_Throws()
    {
        var a = new AncsDataSourceAssembler();
        a.ExpectNotificationAttributes(1, [new(AncsNotificationAttributeId.Title, 8)]);
        Assert.Throws<FormatException>(() => a.Feed(H("01 41 00")));
    }

    [Fact]
    public void DateAttribute_Parses() =>
        Assert.Equal(new DateTime(2026, 10, 28, 14, 30, 5), AncsDataSourceAssembler.ParseDate("20261028T143005"));

    [Fact]
    public void NotificationCenter_IncomingCallFlow_AnswerIsPositiveAction()
    {
        var center = new AncsNotificationCenter(CallAttrs);
        var writes = new List<byte[]>();
        center.WriteControlPoint += writes.Add;
        PhoneNotification? added = null, modified = null, removed = null;
        center.Added += n => added = n;
        center.Modified += n => modified = n;
        center.Removed += n => removed = n;

        center.OnNotificationSource(H("00 18 01 01 2A000000"));
        Assert.Equal(AncsCommands.GetNotificationAttributes(42, CallAttrs), Assert.Single(writes));

        var resp = CallResponse();
        center.OnDataSource(resp.AsSpan(0, 20));
        Assert.Null(added);
        center.OnDataSource(resp.AsSpan(20));
        Assert.NotNull(added);
        Assert.True(added!.IsIncomingCall);
        Assert.True(added.HasPositiveAction);
        Assert.Equal("Answer", added.PositiveActionLabel);
        Assert.Equal("com.apple.mobilephone", added.AppIdentifier);
        Assert.Equal("Anne", added.Sender);
        // App display name is fetched once per app identifier.
        Assert.Equal(AncsCommands.GetAppAttributes("com.apple.mobilephone", AncsAppAttributeId.DisplayName), writes[1]);

        center.OnDataSource(new byte[] { 0x01 }.Concat(Encoding.ASCII.GetBytes("com.apple.mobilephone")).Concat(H("00 00 0500"))
            .Concat(Encoding.ASCII.GetBytes("Phone")).ToArray());
        Assert.Equal("Phone", modified?.AppDisplayName);

        Assert.Equal(H("02 2A000000 00"), center.PerformAction(42, AncsActionId.Positive));
        Assert.Equal(H("02 2A000000 00"), writes[2]);

        center.OnNotificationSource(H("02 00 01 00 2A000000"));
        Assert.Equal(42u, removed?.Uid);
        Assert.Empty(center.Items);
    }

    [Fact]
    public void Ams_EntityUpdate_Parses()
    {
        var u = AmsEntityUpdate.Parse(new byte[] { 2, 2, 0 }.Concat(Encoding.UTF8.GetBytes("Gülümse")).ToArray());
        Assert.Equal(AmsEntityId.Track, u.Entity);
        Assert.Equal((byte)AmsTrackAttributeId.Title, u.AttributeId);
        Assert.Equal("Gülümse", u.Value);
        Assert.Equal(new byte[] { 2, 0, 1, 2 }, AmsEntityUpdate.Subscribe(AmsEntityId.Track, 0, 1, 2));
    }

    [Fact]
    public void Uuids_MatchAppleSpec()
    {
        Assert.Equal("7905f431-b5ce-4e99-a40f-4b1e122d00d0", AncsUuids.Service.ToString());
        Assert.Equal("9fbf120d-6301-42d9-8c58-25e699a21dbd", AncsUuids.NotificationSource.ToString());
        Assert.Equal("69d1d8f3-45e1-49a8-9821-9bbdfdaad9d9", AncsUuids.ControlPoint.ToString());
        Assert.Equal("22eac6e9-24d6-4bb5-be44-b36ace7c7bfb", AncsUuids.DataSource.ToString());
    }
}
