// End to end over simulated AWDL: two AwdlHosts (real threads, real timers, host clock) on a SimulatedAir, joined to
// user-mode packet sinks, carrying a real AirDrop.Core HTTPS exchange (AirDropClient -> AirDropServer).
//
// There's no user-mode TCP stack here, so the test tunnels each loopback TCP connection through AWDL as IPv6+TCP-shaped
// packets (valid headers and checksums, link-local EUI-64 addresses, SYN/PSH|ACK/FIN flags) with a small window and
// per-segment ACKs, and re-terminates it on the far side against the AirDrop server. Every byte of TLS goes over the
// simulated air as AWDL data frames, the same way the Wintun path would carry the OS stack's segments.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AirDrop.Core;
using AirDrop.Core.Security;
using Nitro.Awdl;
using Nitro.Awdl.Host;
using Nitro.Awdl.Ip;
using Nitro.Awdl.Radio;

namespace AirDrop.Tests.Awdl;

/// <summary>Carries loopback TCP connections across a <see cref="UserModePacketSink"/> as TCP-shaped IPv6 packets.</summary>
internal sealed class TcpShapedTunnel : IAsyncDisposable
{
    private const byte Fin = 0x01, Syn = 0x02, Psh = 0x08, Ack = 0x10;
    private const int Segment = 1200, Window = 24;

    private sealed class Conn
    {
        public required ushort Id;
        public required Socket Socket;
        public readonly SemaphoreSlim Credit = new(Window, Window);
        public uint NextSeq;
        public uint Expected;
        public readonly SortedDictionary<uint, (byte Flags, byte[] Data)> Pending = new();
        public readonly SemaphoreSlim WriteLock = new(1, 1);
    }

    private readonly UserModePacketSink _sink;
    private readonly byte[] _local, _remote;
    private readonly IPEndPoint? _connectTo;
    private readonly TcpListener? _listener;
    private readonly ConcurrentDictionary<ushort, Conn> _conns = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = [];
    private int _nextId = 40000;
    public long SegmentsSent, SegmentsReceived;

    /// <summary>Listening side: accepts loopback connections and tunnels them to the peer.</summary>
    public TcpShapedTunnel(UserModePacketSink sink, MacAddr local, MacAddr remote)
    {
        _sink = sink;
        _local = local.LinkLocal().GetAddressBytes();
        _remote = remote.LinkLocal().GetAddressBytes();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _tasks.Add(Task.Run(AcceptLoop));
        _tasks.Add(Task.Run(ReceiveLoop));
    }

    /// <summary>Terminating side: connections arriving from the peer are opened against <paramref name="connectTo"/>.</summary>
    public TcpShapedTunnel(UserModePacketSink sink, MacAddr local, MacAddr remote, IPEndPoint connectTo)
    {
        _sink = sink;
        _local = local.LinkLocal().GetAddressBytes();
        _remote = remote.LinkLocal().GetAddressBytes();
        _connectTo = connectTo;
        _tasks.Add(Task.Run(ReceiveLoop));
    }

    public IPEndPoint ListenEndPoint => (IPEndPoint)_listener!.LocalEndpoint;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener!.AcceptSocketAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            var c = new Conn { Id = (ushort)Interlocked.Increment(ref _nextId), Socket = s };
            _conns[c.Id] = c;
            SendSegment(c, Syn, []);
            _tasks.Add(Task.Run(() => Pump(c)));
        }
    }

    private async Task Pump(Conn c)
    {
        var buf = new byte[Segment];
        try
        {
            while (true)
            {
                int n = await c.Socket.ReceiveAsync(buf, SocketFlags.None, _cts.Token);
                await c.Credit.WaitAsync(_cts.Token);
                if (n == 0)
                {
                    SendSegment(c, Fin | Ack, []);
                    return;
                }
                SendSegment(c, Psh | Ack, buf.AsSpan(0, n));
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (SocketException) { SendSegment(c, Fin | Ack, []); }
        catch (ObjectDisposedException) { }
    }

    private void SendSegment(Conn c, byte flags, ReadOnlySpan<byte> data)
    {
        // The id plays both ports; seq counts segments (only order matters here, not bytes).
        var seg = new byte[20 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(seg, c.Id);
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), c.Id);
        uint seq = (flags & (Psh | Fin | Syn)) != 0 ? c.NextSeq++ : 0;
        BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(4), seq);
        seg[12] = 5 << 4;
        seg[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(14), 0xffff);
        data.CopyTo(seg.AsSpan(20));
        ushort sum = Ipv6Packet.Checksum(_local, _remote, Ipv6Packet.Tcp, seg);
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(16), sum);
        Interlocked.Increment(ref SegmentsSent);
        _sink.Send(Ipv6Packet.Build(_local, _remote, Ipv6Packet.Tcp, 64, seg));
    }

    private void SendAck(Conn c) => SendSegment(c, Ack, []);

    private async Task ReceiveLoop()
    {
        try
        {
            await foreach (var p in _sink.Delivered.ReadAllAsync(_cts.Token))
            {
                if (p.Length < 60 || p[6] != Ipv6Packet.Tcp) continue;
                var seg = p.AsSpan(40);
                if (Ipv6Packet.Checksum(p.AsSpan(8, 16), p.AsSpan(24, 16), Ipv6Packet.Tcp, seg) != 0)
                    throw new InvalidDataException("bad TCP checksum after AWDL");
                Interlocked.Increment(ref SegmentsReceived);
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(seg);
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(seg[4..]);
                byte flags = seg[13];
                var data = seg[20..].ToArray();
                if (flags == Syn && _connectTo != null)
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    await s.ConnectAsync(_connectTo, _cts.Token);
                    var c = new Conn { Id = id, Socket = s, Expected = 1 };
                    _conns[id] = c;
                    _tasks.Add(Task.Run(() => Pump(c)));
                    continue;
                }
                if (!_conns.TryGetValue(id, out var conn)) continue;
                if (flags == Ack)
                {
                    conn.Credit.Release();
                    continue;
                }
                SendAck(conn);
                conn.Pending[seq] = (flags, data);
                while (conn.Pending.Remove(conn.Expected, out var next))
                {
                    conn.Expected++;
                    if (next.Data.Length > 0) await conn.Socket.SendAsync(next.Data, SocketFlags.None, _cts.Token);
                    if ((next.Flags & Fin) != 0)
                    {
                        try { conn.Socket.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
                    }
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        foreach (var c in _conns.Values) c.Socket.Dispose();
        try { await Task.WhenAll(_tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
        _cts.Dispose();
    }
}

public class AwdlEndToEndTests
{
    private static readonly MacAddr MacA = new(0x02, 0x4e, 0x44, 0x00, 0x00, 0x0a);
    private static readonly MacAddr MacB = new(0x02, 0x4e, 0x44, 0x00, 0x00, 0x0b);

    private static AwdlConfig Cfg(MacAddr mac, string name) => new() { Addr = mac, Name = name, Radio = RadioConstraints.IntelExclusive };

    private static async Task WaitFor(Func<bool> cond, TimeSpan timeout, string what)
    {
        var until = DateTime.UtcNow + timeout;
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact] // spec: T-AWDL-4 (sim), NET-1, NET-2
    public async Task TwoHosts_CarryAnAirDropHttpsTransfer_OverSimulatedAwdl()
    {
        var air = new SimulatedAir();
        var options = new AwdlHostOptions { Mmcss = false, Priority = ThreadPriority.AboveNormal };
        using var sinkA = new UserModePacketSink();
        using var sinkB = new UserModePacketSink();
        await using var hostA = new AwdlHost(Cfg(MacA, "sender"), air.Join(MacA, new Channel(6)), sinkA, options);
        await using var hostB = new AwdlHost(Cfg(MacB, "receiver"), air.Join(MacB, new Channel(6)), sinkB, options);
        await hostA.StartAsync();
        await hostB.StartAsync();
        await WaitFor(() => sinkA.Up.ContainsKey(MacB) && sinkB.Up.ContainsKey(MacA), TimeSpan.FromSeconds(10), "peers up");
        Assert.Equal("receiver", sinkA.Up[MacB].Name);

        using var src = new TempDir();
        using var dst = new TempDir();
        var file = Path.Combine(src.Path, "awdl-üzerinden.bin");
        var data = new byte[120_000];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(file, data);

        await using var server = new AirDropServer(new AirDropServerOptions
        {
            ReceiveDirectory = dst.Path,
            Identity = new AirDropIdentity { ComputerName = "AWDL Alıcı", ModelName = "TestModel" },
            Port = 0,
            EnableMdns = false,
            Certificate = CertificateStore.CreateEphemeral(),
            AskHandler = (_, _) => Task.FromResult(true),
        });
        ReceivedFiles? received = null;
        server.FilesReceived += r => received = r;
        await server.StartAsync();

        await using var far = new TcpShapedTunnel(sinkB, MacB, MacA, new IPEndPoint(IPAddress.Loopback, server.Port));
        await using var near = new TcpShapedTunnel(sinkA, MacA, MacB);

        using var client = new AirDropClient(near.ListenEndPoint,
            new AirDropIdentity { ComputerName = "AWDL Gönderen", ModelName = "SenderModel", ServiceId = "a1b2c3d4e5f6" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var discover = await client.DiscoverAsync(ct: timeout.Token);
        Assert.Equal("AWDL Alıcı", discover.ReceiverComputerName);
        Assert.True(await client.SendAsync([file], ct: timeout.Token));

        await WaitFor(() => received != null, TimeSpan.FromSeconds(10), "files received");
        var got = Directory.GetFiles(dst.Path, "awdl-üzerinden.bin", SearchOption.AllDirectories);
        Assert.Equal(data, File.ReadAllBytes(Assert.Single(got)));

        // It really went over AWDL: data frames on the air, delivered by the nodes, peers still up.
        var sa = hostA.Stats!;
        var sb = hostB.Stats!;
        Assert.True(sa.Node.TxUnicast > 100, sa.Node.ToString());
        Assert.True(sb.Node.Delivered > 100, sb.Node.ToString());
        Assert.Equal(0ul, sa.Node.DroppedQueueFull + sb.Node.DroppedQueueFull);
        Assert.Equal(0ul, sa.OutboundForeignSource + sb.OutboundForeignSource);
        Assert.Null(hostA.Failure);
        Assert.Null(hostB.Failure);
        Assert.True(near.SegmentsSent > 100 && far.SegmentsReceived > 100);
    }

    [Fact]
    public async Task Host_DropsPacketsNotFromItsOwnLinkLocal()
    {
        var air = new SimulatedAir();
        using var sinkA = new UserModePacketSink();
        using var sinkB = new UserModePacketSink();
        var options = new AwdlHostOptions { Mmcss = false };
        await using var hostA = new AwdlHost(Cfg(MacA, "a"), air.Join(MacA, new Channel(6)), sinkA, options);
        await using var hostB = new AwdlHost(Cfg(MacB, "b"), air.Join(MacB, new Channel(6)), sinkB, options);
        await hostA.StartAsync();
        await hostB.StartAsync();
        await WaitFor(() => sinkA.Up.ContainsKey(MacB), TimeSpan.FromSeconds(10), "peer up");
        // As Windows would send from its own random link-local, or during DAD: not ours, so it doesn't go.
        var foreign = new MacAddr(0x02, 0x99, 0, 0, 0, 1).LinkLocal().GetAddressBytes();
        sinkA.Send(Ipv6Packet.Build(foreign, MacB.LinkLocal().GetAddressBytes(), 17, 64, new byte[8]));
        sinkA.Send(Ipv6Packet.Build(IPAddress.Parse("fe80::1234:5678:9abc:def0").GetAddressBytes(), MacB.LinkLocal().GetAddressBytes(), 17, 64, new byte[8]));
        await WaitFor(() => hostA.Stats is { } s && s.OutboundForeignSource + s.OutboundNotAwdl >= 2, TimeSpan.FromSeconds(5), "drops counted");
        Assert.Equal(0ul, hostA.Stats!.Node.TxUnicast);
    }
}
