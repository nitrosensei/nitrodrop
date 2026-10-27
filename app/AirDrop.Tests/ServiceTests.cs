using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AirDrop.App.Services;
using AirDrop.Core;
using AirDrop.Core.Security;
using AirDrop.Core.Transfer;
using AppTransferProgress = AirDrop.App.Services.TransferProgress;

namespace AirDrop.Tests;

/// <summary>Two CoreAirDropService instances talking over loopback (mDNS on a private port + HTTPS).</summary>
public class ServiceTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Cert = CertificateStore.CreateEphemeral();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private sealed class Pair : IAsyncDisposable
    {
        public required CoreAirDropService A, B;
        public required TempDir DirA, DirB, Src;
        public readonly ConcurrentDictionary<string, PeerInfo> PeersOfA = new();
        public readonly ConcurrentQueue<string> LostByA = new();
        public PeerInfo? PeerB;

        public async ValueTask DisposeAsync()
        {
            await A.DisposeAsync();
            await B.DisposeAsync();
            DirA.Dispose();
            DirB.Dispose();
            Src.Dispose();
        }
    }

    private static CoreAirDropService NewService(int mdnsPort) => new(new CoreAirDropServiceSettings
    {
        Interface = "loopback",
        Port = 0,
        MdnsPort = mdnsPort,
        Certificate = Cert,
        PeerLostGrace = TimeSpan.FromSeconds(3),
    });

    private static async Task<Pair> StartPair(int mdnsPort, bool compatible = true)
    {
        var p = new Pair
        {
            A = NewService(mdnsPort), B = NewService(mdnsPort),
            DirA = new TempDir(), DirB = new TempDir(), Src = new TempDir(),
        };
        p.A.PeerFound += (_, peer) => p.PeersOfA[peer.Id] = peer;
        p.A.PeerLost += (_, id) => { p.LostByA.Enqueue(id); p.PeersOfA.TryRemove(id, out PeerInfo? _); };
        await Task.WhenAll(
            p.A.StartAsync(new ServiceOptions { DeviceName = "Gönderen PC", ReceiveFolder = p.DirA.Path }, ReceiveMode.Everyone),
            p.B.StartAsync(new ServiceOptions { DeviceName = "Alıcı PC", ReceiveFolder = p.DirB.Path, PreferCompatibleFormats = compatible }, ReceiveMode.Everyone));
        p.PeerB = await WaitFor(() => p.PeersOfA.Values.FirstOrDefault(x => x.Name == "Alıcı PC"));
        return p;
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, TimeSpan? timeout = null) where T : class
    {
        var until = DateTime.UtcNow + (timeout ?? Timeout);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private static async Task WaitUntil(Func<bool> probe, TimeSpan? timeout = null) =>
        await WaitFor(() => probe() ? "ok" : null, timeout);

    [Fact]
    public async Task Discovery_SendWithProgress_Received()
    {
        await using var p = await StartPair(53611);
        var peer = p.PeerB!;
        Assert.Equal(DeviceKind.Windows, peer.Kind);          // recognised via the airdrop-win marker
        Assert.Equal(AirDropIdentity.DefaultModelName, peer.ModelName);
        Assert.Empty(peer.ContactHashes!);
        // A never lists itself.
        Assert.DoesNotContain(p.PeersOfA.Values, x => x.Name == "Gönderen PC");

        var f1 = Path.Combine(p.Src.Path, "büyük dosya.bin");
        var data = new byte[3_000_000];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(f1, data);
        var f2 = Path.Combine(p.Src.Path, "not.txt");
        File.WriteAllText(f2, "çğıöşü");

        IncomingRequest? seen = null;
        ReceivedBatch? batch = null;
        var receiveProgress = new ConcurrentQueue<AppTransferProgress>();
        p.B.AskHandler = r => { seen = r; return Task.FromResult(true); };
        p.B.Received += (_, b) => batch = b;
        p.B.ReceiveProgress += (_, pr) => receiveProgress.Enqueue(pr);

        var reports = new ConcurrentQueue<AppTransferProgress>();
        var outcome = await p.A.SendAsync(peer.Id, [f1, f2], new SyncProgress<AppTransferProgress>(reports.Enqueue));
        Assert.Equal(SendOutcome.Delivered, outcome);

        Assert.NotNull(seen);
        Assert.Equal("Gönderen PC", seen!.SenderName);
        Assert.Equal(DeviceKind.Windows, seen.SenderKind);
        Assert.Equal(AirDropIdentity.DefaultModelName, seen.SenderModelName);
        Assert.Equal(["büyük dosya.bin", "not.txt"], seen.Files.Select(f => f.Name));
        Assert.Equal("public.plain-text", seen.Files[1].Uti);
        Assert.Empty(seen.Links);

        await WaitFor(() => batch);
        Assert.Equal("Gönderen PC", batch!.SenderName);
        Assert.Equal(seen.SenderKey, batch.SenderKey);
        Assert.Equal(2, batch.Paths.Count);
        Assert.Equal(data.Length + Encoding.UTF8.GetByteCount("çğıöşü"), batch.TotalBytes);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(p.DirB.Path, "büyük dosya.bin")));

        var last = reports.Last();
        Assert.Equal(batch.TotalBytes, last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1.0, last.Fraction);
        Assert.True(reports.Count >= 2);
        Assert.All(reports, r => Assert.True(r.BytesDone <= r.BytesTotal));
        Assert.NotEmpty(receiveProgress);
        Assert.Equal(batch.TotalBytes, receiveProgress.Last().BytesDone);
    }

    [Fact]
    public async Task Decline_ReturnsDeclined_NothingWritten()
    {
        await using var p = await StartPair(53612);
        var f = Path.Combine(p.Src.Path, "a.txt");
        File.WriteAllText(f, "x");
        bool received = false;
        p.B.AskHandler = _ => Task.FromResult(false);
        p.B.Received += (_, _) => received = true;
        Assert.Equal(SendOutcome.Declined, await p.A.SendAsync(p.PeerB!.Id, [f], new SyncProgress<AppTransferProgress>(_ => { })));
        Assert.False(received);
        Assert.Empty(Directory.GetFileSystemEntries(p.DirB.Path));

        // No handler at all also declines.
        p.B.AskHandler = null;
        Assert.Equal(SendOutcome.Declined, await p.A.SendAsync(p.PeerB.Id, [f], new SyncProgress<AppTransferProgress>(_ => { })));
        // Unknown peer → Failed (not an exception).
        Assert.Equal(SendOutcome.Failed, await p.A.SendAsync("nope", [f], new SyncProgress<AppTransferProgress>(_ => { })));
    }

    [Fact]
    public async Task LinkSharing_DeliveredWithoutUpload()
    {
        await using var p = await StartPair(53613);
        IncomingRequest? seen = null;
        ReceivedBatch? batch = null;
        p.B.AskHandler = r => { seen = r; return Task.FromResult(true); };
        p.B.Received += (_, b) => batch = b;
        var url = new Uri("https://nitrobyte.com.tr/airdrop?q=çay");
        Assert.Equal(SendOutcome.Delivered, await p.A.SendLinkAsync(p.PeerB!.Id, url));
        Assert.Equal(url, Assert.Single(seen!.Links));
        Assert.Empty(seen.Files);
        await WaitFor(() => batch);
        Assert.Equal(url, Assert.Single(batch!.Links));
        Assert.Empty(batch.Paths);
        Assert.Empty(Directory.GetFileSystemEntries(p.DirB.Path));

        p.B.AskHandler = _ => Task.FromResult(false);
        Assert.Equal(SendOutcome.Declined, await p.A.SendLinkAsync(p.PeerB.Id, url));
    }

    [Fact]
    public async Task ReceiveModeOff_WithdrawsAndRejects_ThenComesBack()
    {
        await using var p = await StartPair(53614);
        var id = p.PeerB!.Id;
        int asked = 0;
        p.B.AskHandler = _ => { asked++; return Task.FromResult(true); };

        await p.B.SetModeAsync(ReceiveMode.Off);
        await WaitUntil(() => p.LostByA.Contains(id));

        // Direct Core client against B's port: hidden on /Discover and /Ask is refused without asking the user.
        var f = Path.Combine(p.Src.Path, "a.txt");
        File.WriteAllText(f, "x");
        using (var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, p.B.Port)))
        {
            Assert.False((await client.DiscoverAsync()).IsDiscoverable);
            Assert.False(await client.AskAsync([f]));
        }
        Assert.Equal(0, asked);
        Assert.Equal(SendOutcome.Failed, await p.A.SendAsync(id, [f], new SyncProgress<AppTransferProgress>(_ => { })));

        // ContactsOnly is treated as Off.
        await p.B.SetModeAsync(ReceiveMode.ContactsOnly);
        using (var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, p.B.Port)))
            Assert.False(await client.AskAsync([f]));

        await p.B.SetModeAsync(ReceiveMode.Everyone);
        await WaitFor(() => p.PeersOfA.Values.FirstOrDefault(x => x.Name == "Alıcı PC"));
        Assert.Equal(SendOutcome.Delivered, await p.A.SendAsync(id, [f], new SyncProgress<AppTransferProgress>(_ => { })));
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task MediaCapabilities_FollowOptions_AndNameUpdates()
    {
        await using var p = await StartPair(53615, compatible: false);
        using var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, p.B.Port));
        var d = await client.DiscoverAsync();
        Assert.True(MediaCapabilities.SupportsHeif(d.MediaCapabilitiesJson));
        Assert.True(d.IsAirDropWin);

        await p.B.UpdateOptionsAsync(new ServiceOptions { DeviceName = "Yeni Ad", ReceiveFolder = p.DirB.Path, PreferCompatibleFormats = true });
        d = await client.DiscoverAsync();
        Assert.Equal(MediaCapabilities.CompatibleJson, d.MediaCapabilitiesJson);
        Assert.False(MediaCapabilities.SupportsHeif(d.MediaCapabilitiesJson));
        Assert.Equal("Yeni Ad", d.ReceiverComputerName);
    }

    [Theory]
    [InlineData("iPhone15,2", DeviceKind.iPhone)]
    [InlineData("iPad13,18", DeviceKind.iPad)]
    [InlineData("MacBookAir10,1", DeviceKind.Mac)]
    [InlineData("Mac14,2", DeviceKind.Mac)]
    [InlineData("iMac21,1", DeviceKind.Mac)]
    [InlineData("airdrop-win", DeviceKind.Windows)]
    [InlineData("OpenDrop", DeviceKind.Unknown)]
    [InlineData(null, DeviceKind.Unknown)]
    public void ModelToKind(string? model, DeviceKind expected) => Assert.Equal(expected, CoreAirDropService.KindOf(model));

    [Fact]
    public void ThroughputMeter_SmoothsAndThrottles()
    {
        var now = TimeSpan.Zero;
        var m = new ThroughputMeter(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), () => now);
        Assert.NotNull(m.Update(0, 10_000_000));             // first report immediately
        long bytes = 0;
        int reports = 0;
        for (int i = 0; i < 300; i++)                         // 3 s at 1 MB/s in 10 ms steps
        {
            now += TimeSpan.FromMilliseconds(10);
            bytes += 10_000;
            if (m.Update(bytes, 10_000_000) is not null) reports++;
        }
        Assert.InRange(m.BytesPerSecond, 0.95e6, 1.05e6);
        Assert.InRange(reports, 25, 35);                      // ~every 100 ms
        // Speed halves: after ~1 s the EMA has moved most of the way (1 - e^-1 ≈ 63 %).
        for (int i = 0; i < 100; i++)
        {
            now += TimeSpan.FromMilliseconds(10);
            bytes += 5_000;
            m.Update(bytes, 10_000_000);
        }
        Assert.InRange(m.BytesPerSecond, 0.6e6, 0.75e6);
        Assert.NotNull(m.Update(10_000_000, 10_000_000));    // completion always reported
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }
}
