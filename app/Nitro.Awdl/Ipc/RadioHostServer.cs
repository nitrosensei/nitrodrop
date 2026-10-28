// Nitro.Awdl: the radio host's side of the pipe (NitroDrop.Radio.exe). Copyright (c) 2026 Nitrobyte.
//
// Lifetime rule: the host lives exactly as long as one app connection. It waits for the app to connect (bounded),
// serves it, and when the pipe closes (app quit, crashed, or disposed its client), the parent process exits, or the
// app says "quit", it stops the radio stack and returns, so nothing elevated keeps holding the Intel card.

using System.IO.Pipes;

namespace Nitro.Awdl.Ipc;

/// <summary>What the host runs: radio + Wintun + AWDL (real), or a fake in tests.</summary>
public interface IRadioStack : IAsyncDisposable
{
    /// <summary>
    /// Brings everything up and reports Ready (with the interface) through <paramref name="report"/>, and later peer
    /// changes or a failure. Throws <see cref="RadioStackException"/> (or anything else) when it can't come up.
    /// </summary>
    Task StartAsync(RadioMessage start, Action<RadioStatus> report, Action<string> log, CancellationToken ct);
    /// <summary>Stops AWDL and the radio (driver STOP), closes the adapter. Idempotent.</summary>
    Task StopAsync();
}

/// <summary>A start failure with a category the UI can explain.</summary>
public sealed class RadioStackException(RadioFailureKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public RadioFailureKind Kind { get; } = kind;
}

public sealed record RadioHostServerOptions
{
    public required string PipeName { get; init; }
    public required string UserSid { get; init; }
    /// <summary>Only this process may be the client (the app that launched us); null = any process of the user.</summary>
    public int? ExpectedClientPid { get; init; }
    /// <summary>Give up when the app hasn't connected by then.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Completes when the app process exits (in case the pipe somehow stays open).</summary>
    public Task? ParentExited { get; init; }
    public Action<string>? Log { get; init; }
}

public enum RadioHostExit { ClientLeft = 0, Quit = 0, NoClient = 3, PipeBusy = 4, ParentExited = 5 }

public sealed class RadioHostServer(IRadioStack stack, RadioHostServerOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RadioPipeChannel? _channel;
    private RadioStatus _status = RadioStatus.Stopped();
    private CancellationTokenSource? _startCts;
    private Task _starting = Task.CompletedTask;

    public RadioStatus Status => _status;

    /// <summary>Serves one client, then stops the stack. Returns why it ended.</summary>
    public async Task<RadioHostExit> RunAsync(CancellationToken ct = default)
    {
        NamedPipeServerStream pipe;
        try { pipe = RadioPipe.CreateServer(options.PipeName, options.UserSid); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"pipe {options.PipeName} is already in use: {e.Message}");
            return RadioHostExit.PipeBusy;
        }

        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var parent = options.ParentExited ?? Task.Delay(Timeout.Infinite, life.Token);
        try
        {
            if (!await AcceptAsync(pipe, parent, life.Token).ConfigureAwait(false))
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return parent.IsCompleted ? RadioHostExit.ParentExited : RadioHostExit.NoClient;
            }

            await using var channel = _channel = new RadioPipeChannel(pipe);
            try { await channel.WriteAsync(RadioMessage.Hello(Environment.ProcessId), life.Token).ConfigureAwait(false); }
            catch (IOException)
            {
                Log("app disconnected right away");
                return RadioHostExit.ClientLeft;
            }
            await SendStatusAsync(_status).ConfigureAwait(false);

            var reader = ServeAsync(channel, life.Token);
            var done = await Task.WhenAny(reader, parent).ConfigureAwait(false);
            life.Cancel();
            var exit = done == parent ? RadioHostExit.ParentExited : await reader.ConfigureAwait(false);
            Log(exit == RadioHostExit.ParentExited ? "app process exited" : "app disconnected");
            return exit;
        }
        finally
        {
            _channel = null;
            _startCts?.Cancel();
            try { await _starting.ConfigureAwait(false); } catch { /* reported already */ }
            await stack.StopAsync().ConfigureAwait(false);
            Log("radio stopped, exiting");
        }
    }

    /// <summary>Waits for our app to connect; a client that isn't the expected process is dropped.</summary>
    private async Task<bool> AcceptAsync(NamedPipeServerStream pipe, Task parent, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(options.ConnectTimeout);
        while (true)
        {
            var connect = pipe.WaitForConnectionAsync(t.Token);
            try
            {
                if (await Task.WhenAny(connect, parent).ConfigureAwait(false) != connect) return false;
                await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("no app connected in time");
                return false;
            }
            if (options.ExpectedClientPid is not int want || RadioPipe.ClientProcessId(pipe) is not int got || got == want)
                return true;
            Log($"refusing client process {got} (expected {want})");
            pipe.Disconnect();
        }
    }

    private async Task<RadioHostExit> ServeAsync(RadioPipeChannel channel, CancellationToken ct)
    {
        while (await channel.ReadAsync(ct).ConfigureAwait(false) is { } m)
        {
            switch (m.Type)
            {
                case RadioPipe.StartCmd: await StartAsync(m, ct).ConfigureAwait(false); break;
                case RadioPipe.StopCmd: await StopAsync().ConfigureAwait(false); break;
                case RadioPipe.GetStatusCmd: await SendStatusAsync(_status).ConfigureAwait(false); break;
                case RadioPipe.QuitCmd: return RadioHostExit.Quit;
            }
        }
        return RadioHostExit.ClientLeft;
    }

    private async Task StartAsync(RadioMessage start, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_status.State is RadioState.Starting or RadioState.Ready) { await SendStatusAsync(_status).ConfigureAwait(false); return; }
            _startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Report(RadioStatus.Starting()).ConfigureAwait(false);
            var token = _startCts.Token;
            // Not awaited inline: "stop"/"quit" must still be read while the radio comes up.
            _starting = Task.Run(async () =>
            {
                try { await stack.StartAsync(start, s => _ = Report(s), Log, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception e)
                {
                    Log($"start failed: {e}");
                    var kind = e is RadioStackException r ? r.Kind : RadioFailureKind.Other;
                    await stack.StopAsync().ConfigureAwait(false);
                    await Report(RadioStatus.Failed(kind, e.Message)).ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _startCts?.Cancel();
            try { await _starting.ConfigureAwait(false); } catch { }
            await stack.StopAsync().ConfigureAwait(false);
            await Report(RadioStatus.Stopped()).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private Task Report(RadioStatus s)
    {
        _status = s;
        return SendStatusAsync(s);
    }

    private async Task SendStatusAsync(RadioStatus s)
    {
        if (_channel is not { } c) return;
        try { await c.WriteAsync(RadioMessage.OfStatus(s)).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private void Log(string line)
    {
        options.Log?.Invoke(line);
        if (_channel is { } c) _ = c.WriteAsync(RadioMessage.Log(line)).ContinueWith(_ => { }, TaskScheduler.Default);
    }
}
