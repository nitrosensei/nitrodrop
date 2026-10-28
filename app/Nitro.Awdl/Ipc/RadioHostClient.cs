// Nitro.Awdl: the app's side of the radio host pipe. Copyright (c) 2026 Nitrobyte.

using System.IO.Pipes;

namespace Nitro.Awdl.Ipc;

/// <summary>A live connection to a radio host (real: <see cref="RadioHostClient"/>; tests: a fake).</summary>
public interface IRadioHostConnection : IAsyncDisposable
{
    /// <summary>Last status the host reported (Starting until the first one arrives).</summary>
    RadioStatus Status { get; }
    /// <summary>Every status the host sends. Raised on a pool thread.</summary>
    event Action<RadioStatus>? StatusChanged;
    /// <summary>Log lines from the host.</summary>
    event Action<string>? Log;
    /// <summary>The host closed the pipe or exited. Raised once.</summary>
    event Action? Disconnected;
    bool IsConnected { get; }
    Task SendAsync(RadioMessage command, CancellationToken ct = default);
}

/// <summary>Starts the elevated radio host and connects to it.</summary>
public interface IRadioHostLauncher
{
    /// <summary>
    /// Starts NitroDrop.Radio.exe (one UAC prompt) and returns the connected pipe.
    /// Throws <see cref="RadioLaunchCancelledException"/> when the user declines the prompt.
    /// </summary>
    Task<IRadioHostConnection> LaunchAsync(CancellationToken ct = default);
}

/// <summary>The user answered "No" to the UAC prompt (ERROR_CANCELLED).</summary>
public sealed class RadioLaunchCancelledException() : OperationCanceledException("Administrator permission was not granted");

/// <summary>Client end of the "NitroDrop.Radio.&lt;SID&gt;" pipe.</summary>
public sealed class RadioHostClient : IRadioHostConnection
{
    private readonly RadioPipeChannel _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private int _disconnected, _disposed;
    private volatile RadioStatus _status = RadioStatus.Starting();

    private RadioHostClient(Stream stream)
    {
        _channel = new RadioPipeChannel(stream);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>For tests and in-process use: wraps an already connected duplex stream.</summary>
    public static RadioHostClient Over(Stream connected) => new(connected);

    /// <summary>
    /// Connects to <paramref name="pipeName"/>. With <paramref name="expectedServerPid"/>, refuses a pipe served by any
    /// other process (someone squatting the name before our host started).
    /// </summary>
    public static async Task<RadioHostClient> ConnectAsync(string pipeName, int? expectedServerPid, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(timeout);
            await pipe.ConnectAsync(t.Token).ConfigureAwait(false);
            if (expectedServerPid is int want && RadioPipe.ServerProcessId(pipe) is int got && got != want)
                throw new UnauthorizedAccessException($"Pipe {pipeName} is served by process {got}, not the radio host ({want})");
            return new RadioHostClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public RadioStatus Status => _status;
    public int? HostPid { get; private set; }
    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;

    public event Action<RadioStatus>? StatusChanged;
    public event Action<string>? Log;
    public event Action? Disconnected;

    public Task SendAsync(RadioMessage command, CancellationToken ct = default) => _channel.WriteAsync(command, ct);

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _channel.ReadAsync(_cts.Token).ConfigureAwait(false) is { } m)
            {
                switch (m.Type)
                {
                    case RadioPipe.Hello: HostPid = m.Pid; break;
                    case RadioPipe.Status when m.Status is { } s:
                        _status = s;
                        StatusChanged?.Invoke(s);
                        break;
                    case RadioPipe.LogType when m.Message is { } line: Log?.Invoke(line); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (Interlocked.Exchange(ref _disconnected, 1) == 0) Disconnected?.Invoke();
        }
    }

    /// <summary>Closes the pipe: the host takes that as "app gone", stops the radio and exits.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _cts.Dispose();
    }
}
