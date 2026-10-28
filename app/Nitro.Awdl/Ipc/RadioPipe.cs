// Nitro.Awdl: wire protocol between NitroDrop.exe (unelevated UI) and NitroDrop.Radio.exe (elevated radio host).
// Copyright (c) 2026 Nitrobyte.
//
// One JSON object per line (UTF-8, '\n'), over a byte-mode named pipe "NitroDrop.Radio.<user SID>" that the host
// creates with FILE_FLAG_FIRST_PIPE_INSTANCE and a DACL granting only the launching user. Host → app: hello, status,
// log. App → host: start, stop, getStatus, quit. Unknown types and fields are ignored (forward compatible).

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nitro.Awdl.Ipc;

public enum RadioState { Starting, Ready, Failed, Stopped }

/// <summary>Why the radio host could not bring AWDL up (the UI turns it into Turkish text).</summary>
public enum RadioFailureKind
{
    None,
    /// <summary>Our driver's device interface is missing (driver not installed / not bound to the card).</summary>
    DriverMissing,
    /// <summary>The device refused to open (another process holds it, or not elevated).</summary>
    AccessDenied,
    /// <summary>No verified wintun.dll (not downloaded yet, or it failed the SHA-256 / signature check).</summary>
    WintunMissing,
    /// <summary>The hardware radio switch / airplane mode is off.</summary>
    RfKill,
    /// <summary>The card or firmware failed (START, RX, TX).</summary>
    Radio,
    /// <summary>The Wintun adapter could not be created, configured or did not come up.</summary>
    Network,
    Other,
}

public sealed record RadioPeer(string Mac, string Name, sbyte? Rssi = null);

/// <summary>Radio host state as the app sees it.</summary>
public sealed record RadioStatus
{
    public RadioState State { get; init; }
    public int Channel { get; init; }
    public IReadOnlyList<RadioPeer> Peers { get; init; } = [];
    /// <summary>Technical detail of the failure (exception message), when <see cref="State"/> is Failed.</summary>
    public string? Failure { get; init; }
    public RadioFailureKind FailureKind { get; init; }
    /// <summary>Name of the Wintun interface AirDrop.Core binds to (e.g. "NitroDrop AWDL"), once Ready.</summary>
    public string? Interface { get; init; }
    public int? InterfaceIndex { get; init; }
    /// <summary>Our AWDL MAC and its EUI-64 link-local address.</summary>
    public string? Mac { get; init; }
    public string? Address { get; init; }

    public static RadioStatus Starting(int channel = 0) => new() { State = RadioState.Starting, Channel = channel };
    public static RadioStatus Stopped() => new() { State = RadioState.Stopped };
    public static RadioStatus Failed(RadioFailureKind kind, string? detail) =>
        new() { State = RadioState.Failed, FailureKind = kind, Failure = detail };
}

/// <summary>Everything that goes over the pipe; <see cref="Type"/> says which fields matter.</summary>
public sealed record RadioMessage
{
    public string Type { get; init; } = "";
    /// <summary>hello: protocol version and the host's process id.</summary>
    public int? Version { get; init; }
    public int? Pid { get; init; }
    /// <summary>status.</summary>
    public RadioStatus? Status { get; init; }
    /// <summary>log.</summary>
    public string? Message { get; init; }
    /// <summary>start: AWDL host name (ARPA TLV); null = machine name.</summary>
    public string? Name { get; init; }
    /// <summary>start: receive only, never transmit (debugging).</summary>
    public bool? ListenerOnly { get; init; }

    public static RadioMessage Hello(int pid) => new() { Type = RadioPipe.Hello, Version = RadioPipe.Version, Pid = pid };
    public static RadioMessage OfStatus(RadioStatus s) => new() { Type = RadioPipe.Status, Status = s };
    public static RadioMessage Log(string message) => new() { Type = RadioPipe.LogType, Message = message };
    public static RadioMessage Start(string? name = null, bool listenerOnly = false) =>
        new() { Type = RadioPipe.StartCmd, Name = name, ListenerOnly = listenerOnly ? true : null };
    public static RadioMessage Stop() => new() { Type = RadioPipe.StopCmd };
    public static RadioMessage GetStatus() => new() { Type = RadioPipe.GetStatusCmd };
    public static RadioMessage Quit() => new() { Type = RadioPipe.QuitCmd };
}

public static class RadioPipe
{
    public const int Version = 1;
    public const string NamePrefix = "NitroDrop.Radio.";
    /// <summary>The Wintun adapter's name (= <c>WintunAdapter.DefaultName</c>).</summary>
    public const string InterfaceName = "NitroDrop AWDL";
    /// <summary>Longest line either side accepts; a status with 512 peers is ~40 KB.</summary>
    public const int MaxLine = 256 * 1024;

    public const string Hello = "hello", Status = "status", LogType = "log";
    public const string StartCmd = "start", StopCmd = "stop", GetStatusCmd = "getStatus", QuitCmd = "quit";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    /// <summary>"NitroDrop.Radio.&lt;SID&gt;": one radio host per user; the SID keeps users on one PC apart.</summary>
    public static string NameFor(string userSid)
    {
        if (!IsValidSid(userSid)) throw new ArgumentException($"Not a SID: {userSid}", nameof(userSid));
        return NamePrefix + userSid;
    }

    /// <summary>"S-1-…" with digits and dashes only (no path characters can reach the pipe name).</summary>
    public static bool IsValidSid(string? sid) =>
        sid is { Length: > 4 and < 200 } && sid.StartsWith("S-1-", StringComparison.Ordinal) && sid.All(c => char.IsAsciiDigit(c) || c is '-' or 'S');

    /// <summary>One line, no trailing newline (JSON escapes control characters, so it never contains '\n').</summary>
    public static string Encode(RadioMessage m) => JsonSerializer.Serialize(m, Json);

    /// <summary>Null for blank, malformed or oversized lines (the reader skips them).</summary>
    public static RadioMessage? Decode(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLine) return null;
        try
        {
            var m = JsonSerializer.Deserialize<RadioMessage>(line, Json);
            return m is { Type.Length: > 0 } ? m : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The pipe's security: only <paramref name="userSid"/> may open it (read/write). The creating (elevated) process
    /// keeps its server handle regardless; nobody else, Administrators included, gets a client handle.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string pipeName, string userSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Named pipe ACLs are Windows-only");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(userSid),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, security);
    }

    /// <summary>The other end's process id (GetNamedPipeServerProcessId / ClientProcessId), or null.</summary>
    public static int? ServerProcessId(PipeStream pipe) =>
        OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;

    public static int? ClientProcessId(PipeStream pipe) =>
        OperatingSystem.IsWindows() && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
}

/// <summary>JSON-lines reader/writer over a duplex stream. Writes are serialised; one reader at a time.</summary>
public sealed class RadioPipeChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _write = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public RadioPipeChannel(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
    }

    /// <summary>Next well-formed message, or null at end of stream (the other side closed).</summary>
    public async Task<RadioMessage?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            string? line;
            try { line = await _reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (IOException) { return null; }          // broken pipe
            catch (ObjectDisposedException) { return null; }
            if (line == null) return null;
            if (RadioPipe.Decode(line) is { } m) return m;
        }
    }

    public async Task WriteAsync(RadioMessage message, CancellationToken ct = default)
    {
        var bytes = Utf8.GetBytes(RadioPipe.Encode(message) + "\n");
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _write.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _write.Dispose();
    }
}
