using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AirDrop.App.Services;

public enum DeviceKind { iPhone, iPad, Mac, Windows, Unknown }

public enum ReceiveMode { Off, ContactsOnly, Everyone }

public enum SendOutcome { Delivered, Declined, Failed, Cancelled }

/// <param name="Id">Session id of the peer (mDNS instance); changes between sessions.</param>
/// <param name="StableKey">Best-effort stable key (name + model) for remembering avatars / trust.</param>
/// <param name="ModelName">Raw model string from /Discover (e.g. "MacBookAir10,1"), if known.</param>
/// <param name="ContactHashes">Validated email/phone hashes from the peer's Apple-signed record
/// (ReceiverRecordData), lowercase hex; empty if none. Matched only against the user's own contacts.</param>
/// <param name="Host">Real hardware of a NitroDrop peer (its NitroDropDevice marker); null for Apple devices.</param>
public sealed record PeerInfo(string Id, string Name, DeviceKind Kind, string? ModelName = null,
    IReadOnlyList<string>? ContactHashes = null, AirDrop.Core.Devices.HostDevice? Host = null)
{
    public string StableKey => $"{Name}|{ModelName ?? Kind.ToString()}";
}

public sealed record IncomingFile(string Name, long Size, string? Uti = null);

/// <param name="Thumbnail">Raw FileIcon bytes from /Ask (usually JPEG 2000), may be null.</param>
/// <param name="Links">URLs shared instead of / in addition to files (e.g. Safari share).</param>
public sealed record IncomingRequest(
    string SenderName,
    DeviceKind SenderKind,
    string? SenderModelName,
    IReadOnlyList<IncomingFile> Files,
    IReadOnlyList<Uri> Links,
    byte[]? Thumbnail,
    IReadOnlyList<string>? SenderContactHashes = null,
    AirDrop.Core.Devices.HostDevice? SenderHost = null)
{
    public string SenderKey => $"{SenderName}|{SenderModelName ?? SenderKind.ToString()}";
}

public readonly record struct TransferProgress(long BytesDone, long BytesTotal, double BytesPerSecond)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;

    public TimeSpan? Remaining => BytesPerSecond > 1 && BytesTotal > BytesDone
        ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond)
        : null;
}

public sealed record ReceivedBatch(string SenderName, string SenderKey, IReadOnlyList<string> Paths,
    IReadOnlyList<Uri> Links, long TotalBytes);

public sealed record ServiceOptions
{
    public required string DeviceName { get; init; }
    public required string ReceiveFolder { get; init; }

    /// <summary>Advertise no HEIF/HEVC support so iOS converts photos to JPEG and videos to H.264.</summary>
    public bool PreferCompatibleFormats { get; init; } = true;
}

/// <summary>Boundary between the UI and the AirDrop stack (BLE + AWDL + HTTPS).</summary>
public interface IAirDropService : IAsyncDisposable
{
    /// <summary>False until the Wi-Fi driver and AWDL link are up.</summary>
    bool IsHardwareReady { get; }
    string HardwareStatus { get; }
    /// <summary>IsHardwareReady / HardwareStatus changed (radio host came up, failed, peer count…). Pool thread.</summary>
    event EventHandler? HardwareStatusChanged;

    event EventHandler<PeerInfo>? PeerFound;
    event EventHandler<string>? PeerLost;

    /// <summary>Asked when someone wants to send us files; return true to accept.</summary>
    Func<IncomingRequest, Task<bool>>? AskHandler { get; set; }

    event EventHandler<TransferProgress>? ReceiveProgress;
    event EventHandler<ReceivedBatch>? Received;

    Task StartAsync(ServiceOptions options, ReceiveMode mode, CancellationToken ct = default);
    Task SetModeAsync(ReceiveMode mode);
    Task UpdateOptionsAsync(ServiceOptions options);

    Task<SendOutcome> SendAsync(string peerId, IReadOnlyList<string> files,
        IProgress<TransferProgress> progress, CancellationToken ct = default);

    /// <summary>Share a link (opens in Safari on the iPhone / default browser on a Mac).</summary>
    Task<SendOutcome> SendLinkAsync(string peerId, Uri link, CancellationToken ct = default);
}
