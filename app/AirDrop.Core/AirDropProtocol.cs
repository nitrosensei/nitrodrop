using System.Net;
using System.Security.Cryptography;

namespace AirDrop.Core;

/// <summary>
/// TXT "flags" bits, recovered by OpenDrop from sharingd`receiverSupportsX.
/// A node must have SupportsPipelining or SupportsMixedTypes to be listed by macOS.
/// </summary>
[Flags]
public enum AirDropReceiverFlags
{
    None = 0,
    SupportsUrl = 0x01,
    SupportsDvZip = 0x02,
    SupportsPipelining = 0x04,
    SupportsMixedTypes = 0x08,
    SupportsUnknown1 = 0x10,
    SupportsUnknown2 = 0x20,
    SupportsIris = 0x40,
    SupportsDiscoverMaybe = 0x80, // OpenDrop: "probably" means /Discover is supported
    SupportsUnknown3 = 0x100,
    SupportsAssetBundle = 0x200,
}

public static class AirDropConstants
{
    public const string ServiceType = "_airdrop._tcp.local.";
    public const int DefaultPort = 8770; // OpenDrop defaults to 8771; any port works since SRV carries it.

    /// <summary>OpenDrop's flags: 0x08 | 0x80 = 136. macOS default is 0x3fb.</summary>
    public const AirDropReceiverFlags DefaultFlags = AirDropReceiverFlags.SupportsMixedTypes | AirDropReceiverFlags.SupportsDiscoverMaybe;

    // Request headers OpenDrop copies from macOS (opendrop/client.py _get_headers).
    public const string UserAgent = "AirDrop/1.0";
    public const string AcceptLanguage = "en-us";
    public const string AcceptEncoding = "br, gzip, deflate";
    public const string PlistContentType = "application/octet-stream";
    public const string CpioContentType = "application/x-cpio";

    public const string SenderBundleId = "com.apple.finder"; // what OpenDrop claims to be

    // Plist keys.
    public const string ReceiverComputerName = nameof(ReceiverComputerName);
    public const string ReceiverModelName = nameof(ReceiverModelName);
    public const string ReceiverMediaCapabilities = nameof(ReceiverMediaCapabilities);
    public const string ReceiverRecordData = nameof(ReceiverRecordData);
    public const string SenderComputerName = nameof(SenderComputerName);
    public const string SenderModelName = nameof(SenderModelName);
    public const string SenderID = nameof(SenderID);
    public const string SenderRecordData = nameof(SenderRecordData);
    public const string BundleID = nameof(BundleID);
    public const string ConvertMediaFormats = nameof(ConvertMediaFormats);
    public const string Files = nameof(Files);
    public const string Items = nameof(Items);
    public const string FileIcon = nameof(FileIcon);
    public const string FileName = nameof(FileName);
    public const string FileType = nameof(FileType);
    public const string FileBomPath = nameof(FileBomPath);
    public const string FileIsDirectory = nameof(FileIsDirectory);

    /// <summary>Media capabilities OpenDrop sends: only Version, so senders fall back to legacy formats (JPEG instead of HEIC).</summary>
    public const string MediaCapabilitiesJson = MediaCapabilities.CompatibleJson;

    /// <summary>
    /// Our own extra key in /Discover and /Ask responses and in our /Ask requests so airdrop-win peers can recognise each other even
    /// when they claim a Mac model name. Apple's plist decoders ignore unknown keys.
    /// UNVERIFIED: that iOS/macOS tolerate an unknown key in these responses (Codable/NSDictionary normally do).
    /// </summary>
    public const string AirDropWinVersion = nameof(AirDropWinVersion);
    public const string AirDropWinVersionValue = "1";

    /// <summary>
    /// NitroDrop's second marker, next to <see cref="AirDropWinVersion"/>: the real hardware of a NitroDrop peer as a dict
    /// {Manufacturer, Model, Chassis, Platform} (<see cref="Devices.HostDevice"/>), because ReceiverModelName/SenderModelName
    /// must stay an Apple identifier for iOS. Sent only by NitroDrop; Apple peers ignore it (same assumption as above).
    /// </summary>
    public const string NitroDropDevice = nameof(NitroDropDevice);
}

/// <summary>Who we are on the wire.</summary>
public sealed class AirDropIdentity
{
    public string ComputerName { get; init; } = Environment.MachineName;
    /// <summary>
    /// ReceiverModelName / SenderModelName. Apple devices send their hardware model id ("MacBookPro18,3",
    /// "iPhone15,2"); the peer uses it only to pick the device glyph in the share sheet / accept dialog.
    /// UNVERIFIED: we default to a Mac model so iOS draws a laptop instead of a generic placeholder
    /// (OpenDrop sends "OpenDrop" and iOS still lists it, so any string works). Configurable.
    /// </summary>
    public string ModelName { get; init; } = DefaultModelName;
    public const string DefaultModelName = "MacBookPro18,3";
    /// <summary>Our real hardware, advertised to NitroDrop peers under <see cref="AirDropConstants.NitroDropDevice"/>; null = not sent.</summary>
    public Devices.HostDevice? Host { get; init; }

    /// <summary>
    /// Apple identifier to claim for a chassis so iOS picks a fitting glyph: laptop → MacBook Pro, desktop → Mac mini,
    /// all-in-one → iMac. UNVERIFIED: which glyph iOS draws per identifier (no capture yet); the default stays
    /// <see cref="DefaultModelName"/> and callers opt in.
    /// </summary>
    public static string ModelNameForChassis(string? chassis) => Devices.HostChassis.Normalize(chassis) switch
    {
        Devices.HostChassis.Desktop => "Macmini9,1",
        Devices.HostChassis.AllInOne => "iMac21,1",
        _ => DefaultModelName,
    };

    /// <summary>Adds our markers (<see cref="AirDropConstants.AirDropWinVersion"/> + <see cref="AirDropConstants.NitroDropDevice"/>) to a plist.</summary>
    public void AddMarkers(Dictionary<string, object> d)
    {
        d[AirDropConstants.AirDropWinVersion] = AirDropConstants.AirDropWinVersionValue;
        if (Host is { } h) d[AirDropConstants.NitroDropDevice] = h.ToPlist();
    }
    /// <summary>12 hex chars; used as DNS-SD instance name and SenderID.</summary>
    public string ServiceId { get; init; } = NewServiceId();
    /// <summary>mDNS host name, e.g. "MYPC.local.".</summary>
    public string HostName { get; init; } = SanitizeHost(Environment.MachineName) + ".local.";

    public static string NewServiceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public static string SanitizeHost(string s)
    {
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        var r = new string(chars).Trim('-');
        return r.Length == 0 ? "airdrop-win" : r.Length > 63 ? r[..63] : r;
    }
}

public sealed record AskFile(string FileName, string FileType, string FileBomPath, bool IsDirectory);

/// <summary>Parsed /Ask request, handed to the UI to accept or decline.</summary>
public sealed class AskRequest
{
    public string? SenderComputerName { get; init; }
    public string? SenderModelName { get; init; }
    public string? SenderId { get; init; }
    public string? BundleId { get; init; }
    public bool ConvertMediaFormats { get; init; }
    public IReadOnlyList<AskFile> Files { get; init; } = [];
    /// <summary>URLs when the sender shares links instead of files.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
    public byte[]? FileIcon { get; init; }
    public byte[]? SenderRecordData { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }
    public required IReadOnlyDictionary<string, object> Raw { get; init; }

    /// <summary>A link share (Safari etc.): URLs in Items, no files, so no /Upload follows.</summary>
    public bool IsLinkOnly => Files.Count == 0 && Items.Count > 0;

    /// <summary>Sent by another airdrop-win instance (our marker key is present).</summary>
    public bool IsAirDropWin => Raw.ContainsKey(AirDropConstants.AirDropWinVersion);

    /// <summary>Sender's real hardware when it is a NitroDrop peer that advertises it.</summary>
    public Devices.HostDevice? Host => Devices.HostDevice.FromPlist(Raw.GetValueOrDefault(AirDropConstants.NitroDropDevice));

    public static AskRequest FromPlist(Dictionary<string, object> d, IPEndPoint remote)
    {
        var files = new List<AskFile>();
        if (d.TryGetValue(AirDropConstants.Files, out var f) && f is List<object> list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                files.Add(new AskFile(
                    Str(item, AirDropConstants.FileName) ?? "",
                    Str(item, AirDropConstants.FileType) ?? "public.content",
                    Str(item, AirDropConstants.FileBomPath) ?? "",
                    item.TryGetValue(AirDropConstants.FileIsDirectory, out var isDir) && Truthy(isDir)));
            }
        }
        var items = d.TryGetValue(AirDropConstants.Items, out var it) && it is List<object> il
            ? il.OfType<string>().ToList() : [];
        return new AskRequest
        {
            SenderComputerName = Str(d, AirDropConstants.SenderComputerName),
            SenderModelName = Str(d, AirDropConstants.SenderModelName),
            SenderId = Str(d, AirDropConstants.SenderID),
            BundleId = Str(d, AirDropConstants.BundleID),
            ConvertMediaFormats = d.TryGetValue(AirDropConstants.ConvertMediaFormats, out var c) && Truthy(c),
            Files = files,
            Items = items,
            FileIcon = d.TryGetValue(AirDropConstants.FileIcon, out var icon) ? icon as byte[] : null,
            SenderRecordData = d.TryGetValue(AirDropConstants.SenderRecordData, out var rd) ? rd as byte[] : null,
            RemoteEndPoint = remote,
            Raw = d,
        };
    }

    private static string? Str(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;
    private static bool Truthy(object v) => v switch { bool b => b, long l => l != 0, _ => false };
}

public sealed record DiscoverResult(string? ReceiverComputerName, string? ReceiverModelName, string? MediaCapabilitiesJson,
    IReadOnlyDictionary<string, object> Raw)
{
    /// <summary>Apple-signed validation record of the receiver (contacts matching), if sent.</summary>
    public byte[]? ReceiverRecordData => Raw.GetValueOrDefault(AirDropConstants.ReceiverRecordData) as byte[];

    /// <summary>True if the receiver is another airdrop-win instance.</summary>
    public bool IsAirDropWin => Raw.ContainsKey(AirDropConstants.AirDropWinVersion);

    /// <summary>Receiver's real hardware when it is a NitroDrop peer that advertises it.</summary>
    public Devices.HostDevice? Host => Devices.HostDevice.FromPlist(Raw.GetValueOrDefault(AirDropConstants.NitroDropDevice));

    /// <summary>OpenDrop: receiver is discoverable iff it returned a computer name.</summary>
    public bool IsDiscoverable => !string.IsNullOrEmpty(ReceiverComputerName);
}

public sealed record TransferProgress(long BytesTransferred, long TotalBytes, string? CurrentFile);

/// <summary>Extension → Apple UTI for the Ask "FileType" field.</summary>
public static class UtiTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "public.jpeg", [".jpeg"] = "public.jpeg", [".jp2"] = "public.jpeg-2000",
        [".png"] = "public.png", [".gif"] = "com.compuserve.gif", [".heic"] = "public.heic",
        [".heif"] = "public.heif", [".tif"] = "public.tiff", [".tiff"] = "public.tiff", [".bmp"] = "com.microsoft.bmp",
        [".dng"] = "com.adobe.raw-image", [".mov"] = "com.apple.quicktime-movie", [".mp4"] = "public.mpeg-4",
        [".m4v"] = "com.apple.m4v-video", [".mp3"] = "public.mp3", [".m4a"] = "com.apple.m4a-audio",
        [".wav"] = "com.microsoft.waveform-audio", [".pdf"] = "com.adobe.pdf", [".txt"] = "public.plain-text",
        [".rtf"] = "public.rtf", [".html"] = "public.html", [".zip"] = "public.zip-archive",
        [".gz"] = "org.gnu.gnu-zip-archive", [".vcf"] = "public.vcard",
        [".docx"] = "org.openxmlformats.wordprocessingml.document",
        [".xlsx"] = "org.openxmlformats.spreadsheetml.sheet",
        [".pptx"] = "org.openxmlformats.presentationml.presentation",
    };

    public static string FromPath(string path) =>
        Directory.Exists(path) ? "public.folder" : Map.GetValueOrDefault(Path.GetExtension(path), "public.content");
}
