using System.Text;
using Nitro.Continuity.Bluetooth;
using Nitro.Continuity.Map;
using Nitro.Continuity.Obex;

namespace AirDrop.Tests.Continuity;

public class ObexMapTests
{
    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
    const string MasUuidHex = "BB582B40420C11DBB0DE0800200C9A66";

    [Fact]
    public void Connect_ToMas_ExactBytes()
    {
        // 80 | len 001A | ver 10 | flags 00 | maxlen FFFF | Target(46) len 0013 + 16-byte UUID
        Assert.Equal(H("80 001A 10 00 FFFF 46 0013" + MasUuidHex), MapRequests.Connect().Encode());
    }

    [Fact]
    public void ConnectResponse_Decodes_ConnectionIdAndWho()
    {
        var raw = H("A0 001F 10 00 0400 CB 00000001 4A 0013" + MasUuidHex);
        var p = ObexPacket.Decode(raw, isConnectResponse: true);
        Assert.Equal(ObexResponseCode.Success, p.Response);
        Assert.Equal(0x0400, p.MaxPacketLength);
        Assert.Equal(1u, p.Find(ObexHeaderId.ConnectionId)!.Value);
        Assert.Equal(H(MasUuidHex), p.Find(ObexHeaderId.Who)!.Bytes);
        Assert.Equal(raw, p.Encode());
    }

    [Fact]
    public void NameHeader_Utf16BeNullTerminated()
    {
        var p = new ObexPacket { Code = (byte)ObexOpcode.GetFinal }.Add(ObexHeader.Name("inbox"));
        Assert.Equal(H("83 0012 01 000F 0069 006E 0062 006F 0078 0000"), p.Encode());
        Assert.Equal("inbox", ObexPacket.Decode(p.Encode()).Find(ObexHeaderId.Name)!.Text);
    }

    [Fact]
    public void SetPath_Root_EmptyName()
    {
        // 85 | len 0008 | flags 02 (don't create) | constants 00 | Name 01 0003 (empty)
        var b = ObexPacket.SetPath("").Encode();
        Assert.Equal(H("85 0008 02 00 01 0003"), b);
        var back = ObexPacket.Decode(b);
        Assert.True(back.IsSetPath);
        Assert.Equal("", back.Find(ObexHeaderId.Name)!.Text);
        Assert.Equal(H("85 0005 03 00"), ObexPacket.SetPath(null, backup: true).Encode());
    }

    [Fact]
    public void GetMessagesListing_HeadersAndAppParameters()
    {
        var p = MapRequests.GetMessagesListing(connectionId: 1, folder: "inbox", maxCount: 50, offset: 0);
        var b = p.Encode();
        var d = ObexPacket.Decode(b);
        Assert.Equal((byte)ObexOpcode.GetFinal, d.Code);
        Assert.Equal(1u, d.Find(ObexHeaderId.ConnectionId)!.Value);
        Assert.Equal("x-bt/MAP-msg-listing", d.Find(ObexHeaderId.Type)!.TypeString);
        Assert.Equal(0, d.Find(ObexHeaderId.Type)!.Bytes![^1]); // NUL-terminated
        var ap = d.Find(ObexHeaderId.AppParameters)!.Bytes!;
        Assert.Equal(H("01 02 0032 02 02 0000 13 01 64"), ap);
        var parsed = ObexAppParameters.Decode(ap);
        Assert.Equal(50u, parsed.GetUInt(MapTag.MaxListCount));
        Assert.Equal(100u, parsed.GetUInt(MapTag.SubjectLength));
    }

    [Fact]
    public void AppParameters_TruncatedThrows() =>
        Assert.Throws<FormatException>(() => ObexAppParameters.Decode(H("01 02 00")));

    [Fact]
    public void Decode_TruncatedHeaderThrows() =>
        Assert.Throws<FormatException>(() => ObexPacket.Decode(H("A0 0008 48 0010 41 42")));

    [Fact]
    public void Decode_ByteAndIntHeaders()
    {
        // Success | SRM(97)=01 | Length(C3)=0x00000400 | EndOfBody "hi"
        var p = ObexPacket.Decode(H("A0 000F 97 01 C3 00000400 49 0005 6869"));
        Assert.Equal(1u, p.Find(ObexHeaderId.SingleResponseMode)!.Value);
        Assert.Equal(1024u, p.Find(ObexHeaderId.Length)!.Value);
        Assert.Equal("hi", Encoding.ASCII.GetString(p.Find(ObexHeaderId.EndOfBody)!.Bytes!));
    }

    [Fact]
    public async Task ReadRaw_FramesConsecutivePackets()
    {
        var a = H("90 0003");
        var b = H("A0 0008 49 0005 6F6B");
        using var ms = new MemoryStream(a.Concat(b).ToArray());
        Assert.Equal(a, await ObexPacket.ReadRawAsync(ms));
        Assert.Equal(b, await ObexPacket.ReadRawAsync(ms));
        Assert.Equal(ObexResponseCode.Continue, ObexPacket.Decode(a).Response);
    }

    [Fact]
    public void HeaderEncodingMismatch_Throws() =>
        Assert.Throws<ArgumentException>(() => ObexHeader.UInt8(ObexHeaderId.Name, 1));

    [Fact]
    public void NotificationRegistration_On()
    {
        var d = ObexPacket.Decode(MapRequests.SetNotificationRegistration(7, true).Encode());
        Assert.Equal((byte)ObexOpcode.PutFinal, d.Code);
        Assert.Equal("x-bt/MAP-NotificationRegistration", d.Find(ObexHeaderId.Type)!.TypeString);
        Assert.Equal(H("0E 01 01"), d.Find(ObexHeaderId.AppParameters)!.Bytes);
        Assert.Equal(H("30"), d.Find(ObexHeaderId.EndOfBody)!.Bytes);
    }

    [Fact]
    public void MessageListing_Parses()
    {
        const string xml = """
            <MAP-msg-listing version="1.0">
              <msg handle="20000100001" subject="Kargo yolda" datetime="20261028T101500" sender_name="Ayşe"
                   sender_addressing="+905551112233" recipient_addressing="" type="SMS_GSM" size="11"
                   attachment_size="0" priority="no" read="no" sent="no" protected="no"/>
              <msg handle="20000100002" subject="Tamam" datetime="20261028T101700+0300" type="SMS_GSM" read="yes"/>
            </MAP-msg-listing>
            """;
        var list = MapXml.ParseMessageListing(xml);
        Assert.Equal(2, list.Count);
        Assert.Equal("20000100001", list[0].Handle);
        Assert.Equal("Ayşe", list[0].SenderName);
        Assert.Equal(new DateTime(2026, 10, 28, 10, 15, 0), list[0].DateTime);
        Assert.False(list[0].Read);
        Assert.True(list[1].Read);
        Assert.NotNull(list[1].DateTime);
    }

    [Fact]
    public void EventReport_NewMessage_Parses()
    {
        const string xml = """
            <MAP-event-report version="1.0">
              <event type="NewMessage" handle="20000100003" folder="TELECOM/MSG/INBOX" msg_type="SMS_GSM"/>
            </MAP-event-report>
            """;
        var ev = Assert.Single(MapXml.ParseEventReport(xml));
        Assert.Equal("NewMessage", ev.Type);
        Assert.Equal("20000100003", ev.Handle);
        Assert.Equal("SMS_GSM", ev.MessageType);
    }

    [Fact]
    public void BMessage_Build_LengthCountsUtf8BytesOfMsgBlock()
    {
        var text = BMessage.Outgoing("+905551112233", "Geliyorum, 5 dk.\nÇok şükür", "Ayşe").Build();
        Assert.StartsWith("BEGIN:BMSG\r\nVERSION:1.0\r\n", text);
        Assert.Contains("FOLDER:telecom/msg/outbox\r\n", text);
        Assert.Contains("TEL:+905551112233\r\n", text);
        int begin = text.IndexOf("BEGIN:MSG", StringComparison.Ordinal);
        int end = text.IndexOf("END:MSG\r\n", StringComparison.Ordinal) + "END:MSG\r\n".Length;
        int declared = int.Parse(text.Split("LENGTH:")[1].Split("\r\n")[0]);
        Assert.Equal(Encoding.UTF8.GetByteCount(text[begin..end]), declared);
        Assert.EndsWith("END:BBODY\r\nEND:BENV\r\nEND:BMSG\r\n", text);

        var back = BMessage.Parse(text);
        Assert.Equal("SMS_GSM", back.Type);
        Assert.Equal("+905551112233", back.RecipientTel);
        Assert.Equal("Geliyorum, 5 dk.\nÇok şükür", back.Body);
    }

    [Fact]
    public void BMessage_ParseIncoming_Originator()
    {
        const string incoming =
            "BEGIN:BMSG\r\nVERSION:1.0\r\nSTATUS:UNREAD\r\nTYPE:SMS_GSM\r\nFOLDER:TELECOM/MSG/INBOX\r\n" +
            "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8:Yılmaz;Ayşe\r\nTEL:+905551112233\r\nEND:VCARD\r\n" +
            "BEGIN:BENV\r\nBEGIN:BBODY\r\nCHARSET:UTF-8\r\nLENGTH:26\r\nBEGIN:MSG\r\nSelam\r\nEND:MSG\r\n" +
            "END:BBODY\r\nEND:BENV\r\nEND:BMSG\r\n";
        var m = BMessage.Parse(incoming);
        Assert.Equal("+905551112233", m.OriginatorTel);
        Assert.Null(m.RecipientTel);
        Assert.Equal("Selam", m.Body);
        Assert.Equal("UNREAD", m.Status);
    }

    [Fact]
    public void Uuids_ShortFormRoundTrip()
    {
        Assert.Equal("00001132-0000-1000-8000-00805f9b34fb", MapUuids.MasServiceClass.ToString());
        Assert.Equal(0x1133u, BluetoothUuids.ToShort(MapUuids.MnsServiceClass));
        Assert.Equal(0x112Fu, BluetoothUuids.ToShort(PbapUuids.PseServiceClass));
        Assert.Null(BluetoothUuids.ToShort(MapUuids.MasTarget));
        Assert.Equal("HFP Audio Gateway (AG)", BluetoothUuids.Describe(BluetoothUuids.FromShort(0x111F)));
    }
}
