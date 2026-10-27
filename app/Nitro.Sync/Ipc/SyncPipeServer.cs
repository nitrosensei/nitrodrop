using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nitro.Sync.Ipc;

/// <summary>One connected native-messaging host (= one browser profile with the extension).</summary>
public sealed class PipeSession
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public string Browser { get; internal set; } = "other";
    public string? Origin { get; internal set; }

    internal PipeSession(Stream stream) => _stream = stream;

    public async Task SendAsync(JsonNode message, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > NativeMessaging.MaxToBrowser) return; // the browser would kill the host
        await _writeLock.WaitAsync(ct);
        try { await NativeMessaging.WriteAsync(_stream, bytes, NativeMessaging.MaxToBrowser, ct); }
        finally { _writeLock.Release(); }
    }
}

/// <summary>
/// Named pipe "NitroDrop.Sync.&lt;SID&gt;" (ACL: current user only, remote clients denied) that the native
/// messaging host relays the extension's frames to. Several sessions at once (Chrome + Edge).
/// </summary>
public sealed class SyncPipeServer : IAsyncDisposable
{
    private const int MaxInstances = 6;
    private readonly string _name;
    private readonly Func<PipeSession, JsonObject, CancellationToken, Task<JsonObject?>> _handler;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PipeSession> _sessions = [];
    private readonly List<Task> _loops = [];

    public event Action<PipeSession>? SessionClosed;

    public SyncPipeServer(string name, Func<PipeSession, JsonObject, CancellationToken, Task<JsonObject?>> handler, Action<string> log)
    {
        _name = name;
        _handler = handler;
        _log = log;
    }

    public IReadOnlyList<PipeSession> Sessions { get { lock (_sessions) return _sessions.ToList(); } }

    public void Start()
    {
        // Two listeners waiting at a time is plenty; each accepted connection starts a new listener.
        for (int i = 0; i < 2; i++) _loops.Add(Task.Run(ListenLoop));
    }

    private NamedPipeServerStream Create()
    {
        const PipeOptions opts = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly; // non-Windows fallback
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var sec = new PipeSecurity();
            sec.SetOwner(me);
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            // CurrentUserOnly cannot be combined with a custom PipeSecurity; the explicit DACL above enforces it.
            return NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, sec);
        }
        return new NamedPipeServerStream(_name, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, opts);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create();
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"pipe: {e.Message}");
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        var session = new PipeSession(pipe);
        lock (_sessions) _sessions.Add(session);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await NativeMessaging.ReadAsync(pipe, NativeMessaging.MaxFromBrowser, _cts.Token);
                if (frame == null) break;
                JsonObject? msg;
                try { msg = JsonNode.Parse(frame) as JsonObject; }
                catch (JsonException) { msg = null; }
                if (msg == null) { await session.SendAsync(new JsonObject { ["type"] = "error", ["error"] = "bad_json" }); continue; }
                JsonObject? reply;
                try { reply = await _handler(session, msg, _cts.Token); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log($"pipe handler: {e.Message}");
                    reply = new JsonObject { ["type"] = "error", ["error"] = "internal" };
                }
                if (reply != null)
                {
                    if (msg["reqId"] is JsonNode id) reply["reqId"] = id.DeepClone();
                    await session.SendAsync(reply);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            lock (_sessions) _sessions.Remove(session);
            await pipe.DisposeAsync();
            SessionClosed?.Invoke(session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_loops); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
