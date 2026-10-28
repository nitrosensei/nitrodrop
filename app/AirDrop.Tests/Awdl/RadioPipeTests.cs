using System.Collections.Concurrent;
using System.Security.Principal;
using Nitro.Awdl.Ipc;

namespace AirDrop.Tests.Awdl;

/// <summary>App ↔ NitroDrop.Radio.exe pipe: JSON-lines codec and a real named-pipe round trip with a fake radio stack.</summary>
public class RadioPipeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string Sid => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "S-1-5-21-1-2-3-1001";
    private static string TestPipe() => "NitroDrop.Radio.Test." + Guid.NewGuid().ToString("N");

    private sealed class FakeStack : IRadioStack
    {
        public Exception? FailWith;
        public int Starts, Stops;
        public RadioMessage? LastStart;
        public Action<RadioStatus>? Report;

        public Task StartAsync(RadioMessage start, Action<RadioStatus> report, Action<string> log, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            LastStart = start;
            Report = report;
            if (FailWith != null) throw FailWith;
            log("fake radio up");
            report(new RadioStatus
            {
                State = RadioState.Ready, Channel = 6, Interface = RadioPipe.InterfaceName, InterfaceIndex = 42,
                Peers = [new RadioPeer("86:08:04:8a:2b:f4", "Emre's iPhone", -48)],
                Address = "fe80::8408:4ff:fe8a:2bf4",
            });
            return Task.CompletedTask;
        }

        public Task StopAsync() { Interlocked.Increment(ref Stops); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<RadioStatus> NextStatus(BlockingCollection<RadioStatus> q, Func<RadioStatus, bool> match)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (q.TryTake(out var s, 50) && match(s)) return s;
            await Task.Yield();
        }
        throw new TimeoutException("status not received");
    }

    private static (RadioHostServer server, Task<RadioHostExit> run) Serve(FakeStack stack, string pipe, TimeSpan? connectTimeout = null,
        Task? parentExited = null, int? expectedClient = null)
    {
        var server = new RadioHostServer(stack, new RadioHostServerOptions
        {
            PipeName = pipe, UserSid = Sid, ConnectTimeout = connectTimeout ?? Timeout, ParentExited = parentExited,
            ExpectedClientPid = expectedClient,
        });
        return (server, server.RunAsync());
    }

    [Fact]
    public void Codec_RoundTripsStatus_AndSkipsGarbage()
    {
        var status = new RadioStatus
        {
            State = RadioState.Failed, Channel = 6, FailureKind = RadioFailureKind.RfKill, Failure = "radio switch off\nline 2",
            Peers = [new RadioPeer("aa:bb:cc:dd:ee:ff", "iPhone \"15\"", -60)], Interface = "NitroDrop AWDL", InterfaceIndex = 7,
        };
        var line = RadioPipe.Encode(RadioMessage.OfStatus(status));
        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"type\":\"status\"", line);
        Assert.Contains("\"failureKind\":\"rfKill\"", line);
        Assert.Contains("\"state\":\"failed\"", line);

        var back = RadioPipe.Decode(line)!;
        Assert.Equal(RadioPipe.Status, back.Type);
        Assert.Equal(RadioState.Failed, back.Status!.State);
        Assert.Equal(RadioFailureKind.RfKill, back.Status.FailureKind);
        Assert.Equal("radio switch off\nline 2", back.Status.Failure);
        Assert.Equal("iPhone \"15\"", Assert.Single(back.Status.Peers).Name);
        Assert.Equal(7, back.Status.InterfaceIndex);

        var start = RadioPipe.Decode(RadioPipe.Encode(RadioMessage.Start("Masaüstü", listenerOnly: true)))!;
        Assert.Equal((RadioPipe.StartCmd, "Masaüstü", (bool?)true), (start.Type, start.Name, start.ListenerOnly));
        // Forward compatible: unknown fields ignored; blank, malformed, typeless and oversized lines skipped.
        Assert.Equal(RadioPipe.QuitCmd, RadioPipe.Decode("{\"type\":\"quit\",\"future\":[1,2]}")!.Type);
        Assert.Null(RadioPipe.Decode(""));
        Assert.Null(RadioPipe.Decode("{not json"));
        Assert.Null(RadioPipe.Decode("{\"version\":1}"));
        Assert.Null(RadioPipe.Decode("{\"type\":\"" + new string('x', RadioPipe.MaxLine) + "\"}"));
    }

    [Fact]
    public void PipeName_IsPerUser_AndRejectsNonSids()
    {
        Assert.Equal("NitroDrop.Radio.S-1-5-21-1-2-3-1001", RadioPipe.NameFor("S-1-5-21-1-2-3-1001"));
        Assert.True(RadioPipe.IsValidSid(Sid));
        Assert.Throws<ArgumentException>(() => RadioPipe.NameFor("..\\evil"));
        Assert.Throws<ArgumentException>(() => RadioPipe.NameFor("S-1-5-21\\x"));
        Assert.False(RadioPipe.IsValidSid(null));
    }

    [Fact]
    public async Task Channel_ReadsLinesAcrossPartialWrites()
    {
        var ms = new MemoryStream();
        var a = RadioPipe.Encode(RadioMessage.GetStatus());
        var b = RadioPipe.Encode(RadioMessage.Log("merhaba"));
        var bytes = System.Text.Encoding.UTF8.GetBytes(a + "\n\ngarbage\n" + b + "\n");
        ms.Write(bytes);
        ms.Position = 0;
        await using var ch = new RadioPipeChannel(ms);
        Assert.Equal(RadioPipe.GetStatusCmd, (await ch.ReadAsync())!.Type);
        Assert.Equal("merhaba", (await ch.ReadAsync())!.Message);
        Assert.Null(await ch.ReadAsync());
    }

    [Fact]
    public async Task NamedPipe_StartStop_RoundTrip_AndHostEndsWhenAppLeaves()
    {
        var stack = new FakeStack();
        var pipe = TestPipe();
        var (server, run) = Serve(stack, pipe, expectedClient: Environment.ProcessId);

        var client = await RadioHostClient.ConnectAsync(pipe, Environment.ProcessId, Timeout);
        var statuses = new BlockingCollection<RadioStatus>();
        var logs = new ConcurrentQueue<string>();
        client.StatusChanged += statuses.Add;
        client.Log += logs.Enqueue;

        await client.SendAsync(RadioMessage.Start("Test PC"));
        await NextStatus(statuses, s => s.State == RadioState.Starting);
        var ready = await NextStatus(statuses, s => s.State == RadioState.Ready);
        Assert.Equal(RadioPipe.InterfaceName, ready.Interface);
        Assert.Equal(6, ready.Channel);
        Assert.Equal("Emre's iPhone", Assert.Single(ready.Peers).Name);
        Assert.Equal("Test PC", stack.LastStart!.Name);
        Assert.Equal(Environment.ProcessId, client.HostPid);

        // Start while running: no second bring-up, current status repeated.
        await client.SendAsync(RadioMessage.Start());
        await NextStatus(statuses, s => s.State == RadioState.Ready);
        Assert.Equal(1, stack.Starts);

        // Peer change pushed by the stack.
        stack.Report!(ready with { Peers = [] });
        await NextStatus(statuses, s => s.State == RadioState.Ready && s.Peers.Count == 0);

        await client.SendAsync(RadioMessage.Stop());
        await NextStatus(statuses, s => s.State == RadioState.Stopped);
        Assert.True(stack.Stops >= 1);
        Assert.Contains(logs, l => l.Contains("fake radio up"));

        int stopsBefore = stack.Stops;
        await client.DisposeAsync();                       // app closed → host stops the radio and exits
        Assert.Equal(RadioHostExit.ClientLeft, await run.WaitAsync(Timeout));
        Assert.True(stack.Stops > stopsBefore);
    }

    [Fact]
    public async Task NamedPipe_StartFailure_ReportsKind_AndQuitEndsHost()
    {
        var stack = new FakeStack { FailWith = new RadioStackException(RadioFailureKind.DriverMissing, "no device interface") };
        var pipe = TestPipe();
        var (_, run) = Serve(stack, pipe);
        await using var client = await RadioHostClient.ConnectAsync(pipe, Environment.ProcessId, Timeout);
        var statuses = new BlockingCollection<RadioStatus>();
        client.StatusChanged += statuses.Add;

        await client.SendAsync(RadioMessage.Start());
        var failed = await NextStatus(statuses, s => s.State == RadioState.Failed);
        Assert.Equal(RadioFailureKind.DriverMissing, failed.FailureKind);
        Assert.Equal("no device interface", failed.Failure);

        var gone = new TaskCompletionSource();
        client.Disconnected += () => gone.TrySetResult();
        await client.SendAsync(RadioMessage.Quit());
        Assert.Equal(RadioHostExit.Quit, await run.WaitAsync(Timeout));
        await gone.Task.WaitAsync(Timeout);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Host_Exits_WithoutClient_OnParentExit_AndWhenNameTaken()
    {
        var (_, noClient) = Serve(new FakeStack(), TestPipe(), connectTimeout: TimeSpan.FromMilliseconds(300));
        Assert.Equal(RadioHostExit.NoClient, await noClient.WaitAsync(Timeout));

        var parent = new TaskCompletionSource();
        var pipe = TestPipe();
        var stack = new FakeStack();
        var (_, run) = Serve(stack, pipe, parentExited: parent.Task);
        await using var client = await RadioHostClient.ConnectAsync(pipe, Environment.ProcessId, Timeout);
        await client.SendAsync(RadioMessage.Start());
        // Second host on the same name: FILE_FLAG_FIRST_PIPE_INSTANCE refuses it.
        var (_, second) = Serve(new FakeStack(), pipe);
        Assert.Equal(RadioHostExit.PipeBusy, await second.WaitAsync(Timeout));

        parent.SetResult();                                // app process died with the pipe still open
        Assert.Equal(RadioHostExit.ParentExited, await run.WaitAsync(Timeout));
        Assert.True(stack.Stops >= 1);
    }

    [Fact]
    public async Task Host_RefusesUnexpectedClientProcess()
    {
        var pipe = TestPipe();
        var (_, run) = Serve(new FakeStack(), pipe, connectTimeout: TimeSpan.FromSeconds(1), expectedClient: int.MaxValue - 1);
        await using var client = await RadioHostClient.ConnectAsync(pipe, null, Timeout);
        var gone = new TaskCompletionSource();
        client.Disconnected += () => gone.TrySetResult();
        if (client.IsConnected) await gone.Task.WaitAsync(Timeout);
        Assert.Equal(RadioHostExit.NoClient, await run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Client_RefusesPipeServedBySomeoneElse()
    {
        var pipe = TestPipe();
        var (_, run) = Serve(new FakeStack(), pipe, connectTimeout: TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            RadioHostClient.ConnectAsync(pipe, expectedServerPid: int.MaxValue - 1, Timeout));
        await run.WaitAsync(Timeout);
    }
}
