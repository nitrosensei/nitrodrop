using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Nitro.Continuity.Obex;

namespace Nitro.Continuity.Map;

// Bluetooth Message Access Profile (MAP 1.4; iOS advertises MAP 1.4 per https://support.apple.com/HT204387).
// MAS (server, on the iPhone) = SDP UUID 0x1132, MNS (server, on the PC) = 0x1133, MAP profile = 0x1134.

public static class MapUuids
{
    public static readonly Guid MasServiceClass = Bluetooth.BluetoothUuids.FromShort(0x1132);
    public static readonly Guid MnsServiceClass = Bluetooth.BluetoothUuids.FromShort(0x1133);
    public static readonly Guid Profile = Bluetooth.BluetoothUuids.FromShort(0x1134);

    /// <summary>OBEX Target header on Connect to the MAS.</summary>
    public static readonly Guid MasTarget = new("BB582B40-420C-11DB-B0DE-0800200C9A66");
    /// <summary>OBEX Target header the MSE sends when it connects to our MNS.</summary>
    public static readonly Guid MnsTarget = new("BB582B41-420C-11DB-B0DE-0800200C9A66");
}

public static class MapTypes
{
    public const string FolderListing = "x-obex/folder-listing";
    public const string MessageListing = "x-bt/MAP-msg-listing";
    public const string Message = "x-bt/message";
    public const string MessageStatus = "x-bt/messageStatus";
    public const string MessageUpdate = "x-bt/MAP-messageUpdate";
    public const string NotificationRegistration = "x-bt/MAP-NotificationRegistration";
    public const string EventReport = "x-bt/MAP-event-report";
    public const string InstanceInformation = "x-bt/MASInstanceInformation";
}

/// <summary>MAP application parameter tags (MAP spec, "Application Parameters Header"). Lengths in comments.</summary>
public static class MapTag
{
    public const byte MaxListCount = 0x01;          // 2
    public const byte ListStartOffset = 0x02;       // 2
    public const byte FilterMessageType = 0x03;     // 1, bit mask of types to EXCLUDE
    public const byte FilterPeriodBegin = 0x04;     // var, "yyyyMMddTHHmmss"
    public const byte FilterPeriodEnd = 0x05;       // var
    public const byte FilterReadStatus = 0x06;      // 1: 0 all, 1 unread only, 2 read only
    public const byte FilterRecipient = 0x07;       // var
    public const byte FilterOriginator = 0x08;      // var
    public const byte FilterPriority = 0x09;        // 1
    public const byte Attachment = 0x0A;            // 1: 0 off, 1 on
    public const byte Transparent = 0x0B;           // 1: don't keep a copy in Sent
    public const byte Retry = 0x0C;                 // 1
    public const byte NewMessage = 0x0D;            // 1
    public const byte NotificationStatus = 0x0E;    // 1: 0 off, 1 on
    public const byte MasInstanceId = 0x0F;         // 1
    public const byte ParameterMask = 0x10;         // 4
    public const byte FolderListingSize = 0x11;     // 2
    public const byte MessagesListingSize = 0x12;   // 2 (ListingSize)
    public const byte SubjectLength = 0x13;         // 1
    public const byte Charset = 0x14;               // 1: 0 native, 1 UTF-8
    public const byte FractionRequest = 0x15;       // 1
    public const byte FractionDeliver = 0x16;       // 1
    public const byte StatusIndicator = 0x17;       // 1: 0 read status, 1 deleted status
    public const byte StatusValue = 0x18;           // 1
    public const byte MseTime = 0x19;               // var, "yyyyMMddTHHmmss+hhmm"
    // MAP 1.3+ (UNVERIFIED against the spec PDF: values from memory of the MAP 1.4 tag table)
    public const byte DatabaseIdentifier = 0x1A;
    public const byte ConversationListingVersionCounter = 0x1B;
    public const byte PresenceAvailability = 0x1C;
    public const byte PresenceText = 0x1D;
    public const byte LastActivity = 0x1E;
    public const byte FilterLastActivityBegin = 0x1F;
    public const byte FilterLastActivityEnd = 0x20;
    public const byte ChatState = 0x21;
    public const byte ConversationId = 0x22;
    public const byte FolderVersionCounter = 0x23;
    public const byte FilterMessageHandle = 0x24;
    public const byte NotificationFilterMask = 0x25;
    public const byte ConvParameterMask = 0x26;
    public const byte OwnerUci = 0x27;
    public const byte ExtendedData = 0x28;
    public const byte MapSupportedFeatures = 0x29;  // 4
    public const byte MessageHandle = 0x2A;
    public const byte ModifyText = 0x2B;
}

[Flags]
public enum MapMessageTypeFilter : byte
{
    None = 0, SmsGsm = 1, SmsCdma = 2, Email = 4, Mms = 8, Im = 16,
}

/// <summary>Builds the OBEX requests an MCE sends to the iPhone's MAS.</summary>
public static class MapRequests
{
    public static ObexPacket Connect() => ObexPacket.Connect(MapUuids.MasTarget);

    /// <summary>telecom/msg/inbox etc. are reached with SetPath "telecom", "msg", "inbox" one level at a time.</summary>
    public static IEnumerable<ObexPacket> SetFolder(string path, uint connectionId)
    {
        yield return WithConn(ObexPacket.SetPath(""), connectionId); // root
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            yield return WithConn(ObexPacket.SetPath(part), connectionId);
    }

    /// <summary>GetMessagesListing. Name = child folder relative to current (e.g. "inbox"), or "" for the current folder.</summary>
    public static ObexPacket GetMessagesListing(uint connectionId, string folder = "", ushort maxCount = 50, ushort offset = 0,
        bool unreadOnly = false, byte subjectLength = 100)
    {
        var ap = new ObexAppParameters()
            .AddUInt16(MapTag.MaxListCount, maxCount)
            .AddUInt16(MapTag.ListStartOffset, offset)
            .AddByte(MapTag.SubjectLength, subjectLength);
        if (unreadOnly) ap.AddByte(MapTag.FilterReadStatus, 1);
        return new ObexPacket { Code = (byte)ObexOpcode.GetFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Name(folder))
            .Add(ObexHeader.Type(MapTypes.MessageListing))
            .Add(ap.ToHeader());
    }

    public static ObexPacket GetMessage(uint connectionId, string handle, bool attachments = false, bool utf8 = true) =>
        new ObexPacket { Code = (byte)ObexOpcode.GetFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Name(handle))
            .Add(ObexHeader.Type(MapTypes.Message))
            .Add(new ObexAppParameters()
                .AddByte(MapTag.Attachment, (byte)(attachments ? 1 : 0))
                .AddByte(MapTag.Charset, (byte)(utf8 ? 1 : 0)).ToHeader());

    /// <summary>
    /// PushMessage into "outbox" (sends). Single PUT-final packet; callers must split when larger than the negotiated max
    /// packet length (Body chunks with Put, last one EndOfBody with PutFinal).
    /// </summary>
    public static ObexPacket PushMessage(uint connectionId, byte[] bMessage, string folder = "outbox", bool transparent = false) =>
        new ObexPacket { Code = (byte)ObexOpcode.PutFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Name(folder))
            .Add(ObexHeader.Type(MapTypes.Message))
            .Add(new ObexAppParameters()
                .AddByte(MapTag.Charset, 1)
                .AddByte(MapTag.Transparent, (byte)(transparent ? 1 : 0)).ToHeader())
            .Add(ObexHeader.ByteSeq(ObexHeaderId.EndOfBody, bMessage));

    /// <summary>SetNotificationRegistration: asks the MSE to connect back to our MNS (RFCOMM server for 0x1133).</summary>
    public static ObexPacket SetNotificationRegistration(uint connectionId, bool on) =>
        new ObexPacket { Code = (byte)ObexOpcode.PutFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Type(MapTypes.NotificationRegistration))
            .Add(new ObexAppParameters().AddByte(MapTag.NotificationStatus, (byte)(on ? 1 : 0)).ToHeader())
            .Add(ObexHeader.ByteSeq(ObexHeaderId.EndOfBody, [0x30])); // spec: body is a single filler byte "0"

    public static ObexPacket SetMessageRead(uint connectionId, string handle, bool read = true) =>
        new ObexPacket { Code = (byte)ObexOpcode.PutFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Name(handle))
            .Add(ObexHeader.Type(MapTypes.MessageStatus))
            .Add(new ObexAppParameters().AddByte(MapTag.StatusIndicator, 0).AddByte(MapTag.StatusValue, (byte)(read ? 1 : 0)).ToHeader())
            .Add(ObexHeader.ByteSeq(ObexHeaderId.EndOfBody, [0x30]));

    private static ObexPacket WithConn(ObexPacket p, uint id) { p.Headers.Insert(0, ObexHeader.ConnectionId(id)); return p; }
}

public sealed record MapMessageListItem(
    string Handle, string? Subject, DateTime? DateTime, string? SenderName, string? SenderAddressing,
    string? RecipientAddressing, string? Type, int? Size, bool? Read, bool? Sent, bool? Priority);

public sealed record MapEvent(string Type, string? Handle, string? Folder, string? MessageType,
    string? Subject, DateTime? DateTime, string? SenderName);

public static class MapXml
{
    /// <summary>Parses an x-bt/MAP-msg-listing body (&lt;MAP-msg-listing version="1.0"&gt;&lt;msg handle=... /&gt;).</summary>
    public static IReadOnlyList<MapMessageListItem> ParseMessageListing(string xml)
    {
        var doc = XDocument.Parse(xml.TrimEnd('\0'));
        return doc.Root!.Elements("msg").Select(m => new MapMessageListItem(
            (string?)m.Attribute("handle") ?? "",
            (string?)m.Attribute("subject"),
            ParseTime((string?)m.Attribute("datetime")),
            (string?)m.Attribute("sender_name"),
            (string?)m.Attribute("sender_addressing"),
            (string?)m.Attribute("recipient_addressing"),
            (string?)m.Attribute("type"),
            int.TryParse((string?)m.Attribute("size"), out var s) ? s : null,
            YesNo((string?)m.Attribute("read")),
            YesNo((string?)m.Attribute("sent")),
            YesNo((string?)m.Attribute("priority")))).ToList();
    }

    /// <summary>Parses an x-bt/MAP-event-report body (what the MSE PUTs to our MNS).</summary>
    public static IReadOnlyList<MapEvent> ParseEventReport(string xml)
    {
        var doc = XDocument.Parse(xml.TrimEnd('\0'));
        return doc.Root!.Elements("event").Select(e => new MapEvent(
            (string?)e.Attribute("type") ?? "",
            (string?)e.Attribute("handle"),
            (string?)e.Attribute("folder"),
            (string?)e.Attribute("msg_type"),
            (string?)e.Attribute("subject"),
            ParseTime((string?)e.Attribute("datetime")),
            (string?)e.Attribute("sender_name"))).ToList();
    }

    public static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        string[] fmts = ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmsszzz", "yyyyMMdd'T'HHmmss'Z'"];
        var t = s.Length > 15 && (s[15] == '+' || s[15] == '-') && s.Length == 20 ? s.Insert(18, ":") : s;
        return DateTime.TryParseExact(t, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private static bool? YesNo(string? v) => v?.ToLowerInvariant() switch { "yes" => true, "no" => false, _ => null };
}

/// <summary>bMessage (MAP §3.1.3) — the x-bt/message body for GetMessage / PushMessage.</summary>
public sealed record BMessage(string Type, string Folder, string? Status, string? OriginatorTel, string? RecipientTel, string? RecipientName, string Body)
{
    private const string Crlf = "\r\n";

    /// <summary>Outgoing message for PushMessage. Type "SMS_GSM" is what car kits use; iOS decides SMS vs iMessage itself (UNVERIFIED).</summary>
    public static BMessage Outgoing(string recipientTel, string text, string? recipientName = null, string type = "SMS_GSM") =>
        new(type, "telecom/msg/outbox", "READ", null, recipientTel, recipientName, text);

    public string Build()
    {
        var msg = new StringBuilder();
        msg.Append("BEGIN:MSG").Append(Crlf).Append(Body.Replace("\r\n", "\n").Replace("\n", Crlf)).Append(Crlf).Append("END:MSG").Append(Crlf);
        string msgBlock = msg.ToString();
        // LENGTH = bytes from "BEGIN:MSG" through "END:MSG\r\n" inclusive.
        int length = Encoding.UTF8.GetByteCount(msgBlock);

        var sb = new StringBuilder();
        sb.Append("BEGIN:BMSG").Append(Crlf)
          .Append("VERSION:1.0").Append(Crlf)
          .Append("STATUS:").Append(Status ?? "UNREAD").Append(Crlf)
          .Append("TYPE:").Append(Type).Append(Crlf)
          .Append("FOLDER:").Append(Folder).Append(Crlf);
        if (OriginatorTel is not null)
            sb.Append("BEGIN:VCARD").Append(Crlf).Append("VERSION:2.1").Append(Crlf).Append("N:").Append(Crlf)
              .Append("TEL:").Append(OriginatorTel).Append(Crlf).Append("END:VCARD").Append(Crlf);
        sb.Append("BEGIN:BENV").Append(Crlf);
        if (RecipientTel is not null)
            sb.Append("BEGIN:VCARD").Append(Crlf).Append("VERSION:2.1").Append(Crlf)
              .Append("N:").Append(RecipientName ?? "").Append(Crlf)
              .Append("TEL:").Append(RecipientTel).Append(Crlf).Append("END:VCARD").Append(Crlf);
        sb.Append("BEGIN:BBODY").Append(Crlf)
          .Append("CHARSET:UTF-8").Append(Crlf)
          .Append("LENGTH:").Append(length.ToString(CultureInfo.InvariantCulture)).Append(Crlf)
          .Append(msgBlock)
          .Append("END:BBODY").Append(Crlf)
          .Append("END:BENV").Append(Crlf)
          .Append("END:BMSG").Append(Crlf);
        return sb.ToString();
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(Build());

    /// <summary>Lenient parser: originator = first VCARD before BENV, recipient = first VCARD inside BENV, body = MSG block(s).</summary>
    public static BMessage Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string type = "", folder = "", status = "";
        string? orig = null, recip = null, recipName = null;
        bool inBenv = false, inVcard = false, inMsg = false;
        string? curTel = null, curName = null;
        var body = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw;
            if (inMsg)
            {
                if (line == "END:MSG") { inMsg = false; continue; }
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
                continue;
            }
            if (line == "BEGIN:MSG") { inMsg = true; if (body.Length > 0) body.Append('\n'); continue; }
            if (line == "BEGIN:BENV") { inBenv = true; continue; }
            if (line == "BEGIN:VCARD") { inVcard = true; curTel = curName = null; continue; }
            if (line == "END:VCARD")
            {
                inVcard = false;
                if (!inBenv) orig ??= curTel;
                else if (recip is null) { recip = curTel; recipName = curName; }
                continue;
            }
            int c = line.IndexOf(':');
            if (c < 0) continue;
            var key = line[..c].Split(';')[0].ToUpperInvariant();
            var val = line[(c + 1)..];
            if (inVcard)
            {
                if (key == "TEL") curTel ??= val;
                else if (key is "FN" or "N" && string.IsNullOrEmpty(curName)) curName = val.Replace(";", " ").Trim();
                continue;
            }
            switch (key)
            {
                case "TYPE" when !inBenv: type = val; break;
                case "FOLDER": folder = val; break;
                case "STATUS": status = val; break;
            }
        }
        return new BMessage(type, folder, status, orig, recip, string.IsNullOrEmpty(recipName) ? null : recipName, body.ToString());
    }
}

/// <summary>Phone Book Access Profile (PBAP 1.2 on iOS): caller names / contacts. PSE on the iPhone = SDP 0x112F.</summary>
public static class PbapUuids
{
    public static readonly Guid PseServiceClass = Bluetooth.BluetoothUuids.FromShort(0x112F);
    /// <summary>OBEX Target on Connect to the PSE.</summary>
    public static readonly Guid PseTarget = new("796135F0-F0C5-11D8-0966-0800200C9A66");
    public const string PhonebookType = "x-bt/phonebook";        // PullPhoneBook, Name e.g. "telecom/pb.vcf"
    public const string VCardListingType = "x-bt/vcard-listing";
    public const string VCardType = "x-bt/vcard";
}
