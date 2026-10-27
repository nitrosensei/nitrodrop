using System.Xml.Linq;
using Nitro.Continuity.Obex;

namespace Nitro.Continuity.Map;

/// <summary>PBAP application parameter tags (PBAP 1.2 "Application Parameters Header").</summary>
public static class PbapTag
{
    public const byte Order = 0x01;
    public const byte SearchValue = 0x02;
    public const byte SearchProperty = 0x03;
    public const byte MaxListCount = 0x04;        // 2
    public const byte ListStartOffset = 0x05;     // 2
    public const byte PropertySelector = 0x06;    // 8 (bit mask)
    public const byte Format = 0x07;              // 1: 0 = vCard 2.1, 1 = vCard 3.0
    public const byte PhonebookSize = 0x08;       // 2
    public const byte NewMissedCalls = 0x09;      // 1
}

public static class PbapRequests
{
    public static ObexPacket Connect() => ObexPacket.Connect(PbapUuids.PseTarget);

    // PropertySelector bits: 0 VERSION, 1 FN, 2 N, 3 PHOTO, 7 TEL, 8 EMAIL.
    public const ulong DefaultProperties = (1UL << 0) | (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 7) | (1UL << 8);

    /// <summary>PullPhoneBook (absolute name, e.g. "telecom/pb.vcf"; "telecom/cch.vcf" = combined call history).</summary>
    public static ObexPacket PullPhoneBook(uint connectionId, string name = "telecom/pb.vcf", ushort maxCount = 0xFFFF,
        bool vcard30 = true, ulong properties = DefaultProperties)
    {
        var ap = new ObexAppParameters()
            .Add(PbapTag.PropertySelector, BitConverter.GetBytes(properties).Reverse().ToArray()) // 8 bytes big-endian
            .AddByte(PbapTag.Format, (byte)(vcard30 ? 1 : 0))
            .AddUInt16(PbapTag.MaxListCount, maxCount);
        return new ObexPacket { Code = (byte)ObexOpcode.GetFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Name(name))
            .Add(ObexHeader.Type(PbapUuids.PhonebookType))
            .Add(ap.ToHeader());
    }
}

public static class MapFolders
{
    /// <summary>GetFolderListing of the current folder (x-obex/folder-listing).</summary>
    public static ObexPacket GetFolderListing(uint connectionId, ushort maxCount = 64) =>
        new ObexPacket { Code = (byte)ObexOpcode.GetFinal }
            .Add(ObexHeader.ConnectionId(connectionId))
            .Add(ObexHeader.Type(MapTypes.FolderListing))
            .Add(new ObexAppParameters().AddUInt16(MapTag.MaxListCount, maxCount).ToHeader());

    /// <summary>Parses &lt;folder-listing&gt;&lt;folder name="inbox"/&gt;… into folder names.</summary>
    public static IReadOnlyList<string> ParseFolderListing(string xml)
    {
        var doc = XDocument.Parse(xml.TrimEnd('\0'));
        return doc.Root!.Elements("folder").Select(f => (string?)f.Attribute("name") ?? "").Where(n => n.Length > 0).ToList();
    }
}
