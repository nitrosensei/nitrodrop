using System.Net;
using AirDrop.Core;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;
using AirDrop.Core.Security;

namespace AirDrop.Tests;

public class EndToEndTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Cert = CertificateStore.CreateEphemeral();

    private static AirDropServer NewServer(string dir, Func<AskRequest, CancellationToken, Task<bool>>? ask) =>
        new(new AirDropServerOptions
        {
            ReceiveDirectory = dir,
            Identity = new AirDropIdentity { ComputerName = "Alıcı PC", ModelName = "TestModel" },
            Port = 0,
            EnableMdns = false,
            Certificate = Cert,
            AskHandler = ask,
        });

    [Fact]
    public async Task Loopback_SendTwoFiles_IncludingTurkishName()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var f1 = Path.Combine(src.Path, "ğüşİöç belge.txt");
        var f2 = Path.Combine(src.Path, "photo.jpg");
        var data1 = "Merhaba dünya — ĞÜŞİÖÇ"u8.ToArray();
        var data2 = new byte[2_500_000];
        new Random(42).NextBytes(data2);
        File.WriteAllBytes(f1, data1);
        File.WriteAllBytes(f2, data2);

        AskRequest? seenAsk = null;
        ReceivedFiles? received = null;
        await using var server = NewServer(dst.Path, (ask, _) => { seenAsk = ask; return Task.FromResult(true); });
        server.FilesReceived += r => received = r;
        await server.StartAsync();

        using var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, server.Port),
            new AirDropIdentity { ComputerName = "Gönderen", ModelName = "SenderModel", ServiceId = "0123456789ab" });

        var discover = await client.DiscoverAsync();
        Assert.True(discover.IsDiscoverable);
        Assert.Equal("Alıcı PC", discover.ReceiverComputerName);
        Assert.Equal("TestModel", discover.ReceiverModelName);
        Assert.Equal("{\"Version\":1}", discover.MediaCapabilitiesJson);

        long lastProgress = 0;
        var progress = new SyncProgress(p => lastProgress = p.BytesTransferred);
        Assert.True(await client.SendAsync([f1, f2], progress));

        Assert.NotNull(seenAsk);
        Assert.Equal("Gönderen", seenAsk!.SenderComputerName);
        Assert.Equal("SenderModel", seenAsk.SenderModelName);
        Assert.Equal("0123456789ab", seenAsk.SenderId);
        Assert.Equal("com.apple.finder", seenAsk.BundleId);
        Assert.Equal(2, seenAsk.Files.Count);
        Assert.Equal("ğüşİöç belge.txt", seenAsk.Files[0].FileName);
        Assert.Equal("./ğüşİöç belge.txt", seenAsk.Files[0].FileBomPath);
        Assert.Equal("public.plain-text", seenAsk.Files[0].FileType);
        Assert.Equal("public.jpeg", seenAsk.Files[1].FileType);
        Assert.False(seenAsk.Files[1].IsDirectory);

        Assert.Equal(data1, File.ReadAllBytes(Path.Combine(dst.Path, "ğüşİöç belge.txt")));
        Assert.Equal(data2, File.ReadAllBytes(Path.Combine(dst.Path, "photo.jpg")));
        Assert.NotNull(received);
        Assert.Equal(2, received!.Paths.Count);
        Assert.Equal(data1.Length + data2.Length, received.TotalBytes);
        Assert.Equal(data1.Length + data2.Length, lastProgress);
    }

    [Fact]
    public async Task Loopback_SendFolder()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var folder = Path.Combine(src.Path, "Tatil Fotoğrafları");
        Directory.CreateDirectory(Path.Combine(folder, "alt"));
        File.WriteAllText(Path.Combine(folder, "alt", "not.txt"), "iç");
        await using var server = NewServer(dst.Path, (_, _) => Task.FromResult(true));
        await server.StartAsync();
        using var client = new AirDropClient(new IPEndPoint(IPAddress.IPv6Loopback, server.Port));
        Assert.True(await client.SendAsync([folder]));
        Assert.Equal("iç", File.ReadAllText(Path.Combine(dst.Path, "Tatil Fotoğrafları", "alt", "not.txt")));
    }

    [Fact]
    public async Task Decline_PreventsUpload()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var f = Path.Combine(src.Path, "a.txt");
        File.WriteAllText(f, "x");
        await using var server = NewServer(dst.Path, (_, _) => Task.FromResult(false));
        await server.StartAsync();
        using var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.False(await client.SendAsync([f]));
        // A direct upload without an accepted Ask must be refused too.
        var ex = await Assert.ThrowsAsync<AirDropRequestException>(() => client.UploadAsync([f]));
        Assert.Equal(403, ex.Status);
        Assert.Empty(Directory.GetFileSystemEntries(dst.Path));
    }

    [Fact]
    public async Task Mdns_PublishAndBrowse_OverLoopback()
    {
        using var dst = new TempDir();
        const int mdnsPort = 53530; // avoid clashing with the system mDNS responder on 5353
        var identity = new AirDropIdentity { ComputerName = "Mdns Test", HostName = "mdns-test.local." };
        await using var server = new AirDropServer(new AirDropServerOptions
        {
            ReceiveDirectory = dst.Path,
            Identity = identity,
            Binding = NetworkBinding.Loopback,
            Port = 0,
            Certificate = Cert,
            MdnsPort = mdnsPort,
        });
        await server.StartAsync();

        await using var browser = new AirDropBrowser(NetworkBinding.Loopback, mdnsPort);
        var peer = await browser.FindAsync(p => p.InstanceName == identity.ServiceId, TimeSpan.FromSeconds(10));
        Assert.NotNull(peer);
        Assert.Equal(server.Port, peer!.Port);
        Assert.Equal(136, (int)peer.Flags);
        Assert.Equal("mdns-test.local.", peer.HostName);

        using var client = new AirDropClient(peer.EndPoint, binding: NetworkBinding.Loopback, hostName: peer.HostName);
        var d = await client.DiscoverAsync();
        Assert.Equal("Mdns Test", d.ReceiverComputerName);
    }

    [Fact]
    public void DnsMessage_RoundTrip()
    {
        var m = new DnsMessage { IsResponse = true, Authoritative = true };
        m.Answers.Add(new DnsRecord { Name = "_airdrop._tcp.local.", Type = DnsType.PTR, Ttl = 4500, Target = "abcdef012345._airdrop._tcp.local." });
        m.Additionals.Add(new DnsRecord { Name = "abcdef012345._airdrop._tcp.local.", Type = DnsType.SRV, Ttl = 120, CacheFlush = true, Port = 8770, Target = "pc.local." });
        m.Additionals.Add(new DnsRecord { Name = "abcdef012345._airdrop._tcp.local.", Type = DnsType.TXT, Ttl = 4500, Text = ["flags=136"] });
        m.Additionals.Add(new DnsRecord { Name = "pc.local.", Type = DnsType.AAAA, Ttl = 120, Address = IPAddress.Parse("fe80::1") });
        var back = DnsMessage.Decode(m.Encode());
        Assert.True(back.IsResponse);
        Assert.Equal("abcdef012345._airdrop._tcp.local.", back.Answers[0].Target);
        var srv = back.Additionals[0];
        Assert.Equal((ushort)8770, srv.Port);
        Assert.True(srv.CacheFlush);
        Assert.Equal("pc.local.", srv.Target);
        Assert.Equal("flags=136", back.Additionals[1].Text![0]);
        Assert.Equal(IPAddress.Parse("fe80::1"), back.Additionals[2].Address);
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress(Action<TransferProgress> a) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => a(value);
    }
}
