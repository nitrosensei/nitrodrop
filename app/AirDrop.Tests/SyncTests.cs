using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirDrop.Core.Net;
using AirDrop.Core.Security;
using Nitro.Sync;
using Nitro.Sync.Ipc;
using Nitro.Sync.Model;
using Nitro.Sync.Net;
using Nitro.Sync.Security;
using Nitro.Sync.Windows;

namespace AirDrop.Tests;

/// <summary>Nitro.Sync: model, security primitives, framing and two in-process nodes over loopback (mDNS ports 5366x).</summary>
public class SyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    // ---------------- model ----------------

    private static DeviceState SampleState() => new()
    {
        DeviceId = "abc123", DeviceName = "Ofis PC", UpdatedAt = 1,
        Tabs =
        [
            new BrowserTab { Browser = "chrome", WindowId = 1, TabId = 10, Title = "Nitrobyte", Url = "https://nitrobyte.com.tr/", Active = true, LastAccessed = 5 },
            new BrowserTab { Browser = "edge", WindowId = 2, TabId = 20, Title = "Haber", Url = "https://example.com/a?b=c", FaviconUrl = "https://example.com/f.ico" },
        ],
        Apps = [new AppInfo { AppId = "0011223344556677", Name = "Visual Studio Code", Exe = "Code.exe", WindowTitle = "x.cs", Foreground = true }],
    };

    [Fact]
    public void State_JsonRoundTrip()
    {
        var s = SampleState();
        var json = s.ToJsonBytes();
        var text = Encoding.UTF8.GetString(json);
        Assert.Contains("\"deviceName\":\"Ofis PC\"", text);
        Assert.DoesNotContain("incognito", text);
        var back = DeviceState.FromJson(json)!;
        Assert.Equal(s.DeviceId, back.DeviceId);
        Assert.Equal(s.Tabs, back.Tabs);
        Assert.Equal(s.Apps, back.Apps);
        Assert.Null(DeviceState.FromJson("{nope"u8));
    }

    [Fact]
    public void State_ETagIgnoresTimestamp_ChangesWithContent()
    {
        var s = SampleState();
        Assert.Equal(s.ComputeETag(), (s with { UpdatedAt = 999 }).ComputeETag());
        Assert.NotEqual(s.ComputeETag(), (s with { Tabs = s.Tabs.Take(1).ToList() }).ComputeETag());
    }

    [Fact]
    public void State_Diff()
    {
        var a = SampleState();
        var b = a with
        {
            Tabs =
            [
                a.Tabs[0] with { Title = "Değişti" },
                new BrowserTab { Browser = "chrome", WindowId = 1, TabId = 11, Url = "https://yeni.example/" },
            ],
            Apps = [],
        };
        var d = StateDiff.Compute(a, b);
        Assert.Single(d.AddedTabs, t => t.TabId == 11);
        Assert.Single(d.RemovedTabs, t => t.TabId == 20);
        Assert.Single(d.ChangedTabs, t => t.Title == "Değişti");
        Assert.Single(d.RemovedApps);
        Assert.Empty(d.AddedApps);
        Assert.True(StateDiff.Compute(a, a).IsEmpty);
        Assert.Equal(2, StateDiff.Compute(null, a).AddedTabs.Count);
    }

    [Fact]
    public void Sanitizer_DropsPrivateAndNonWebTabs_ClampsStrings()
    {
        var tabs = new[]
        {
            new BrowserTab { TabId = 1, Url = "https://ok.example/", Title = new string('x', 1000), FaviconUrl = "data:image/png;base64,AAAA" },
            new BrowserTab { TabId = 2, Url = "https://secret.example/", Incognito = true },
            new BrowserTab { TabId = 3, Url = "chrome://settings" },
            new BrowserTab { TabId = 4, Url = "javascript:alert(1)" },
            new BrowserTab { TabId = 5, Url = "file:///C:/Windows/win.ini" },
        };
        var clean = StateSanitizer.Tabs(tabs, "edge");
        var t = Assert.Single(clean);
        Assert.Equal(1, t.TabId);
        Assert.Equal("edge", t.Browser);
        Assert.Equal(StateLimits.MaxTitle, t.Title.Length);
        Assert.Null(t.FaviconUrl); // data: favicons are not shared
        Assert.Equal(StateLimits.MaxTabs, StateSanitizer.Tabs(Enumerable.Range(0, 900).Select(i => new BrowserTab { TabId = i, Url = "https://a.example/" })).Count);
    }

    // ---------------- URL policy ----------------

    [Theory]
    [InlineData("https://nitrobyte.com.tr/")]
    [InlineData("http://192.168.1.10:8080/a?b=c#d")]
    [InlineData("  https://example.com/path  ")]
    [InlineData("https://www.instagram.com/p/abc/")]
    public void UrlPolicy_AcceptsWeb(string url) => Assert.True(UrlPolicy.IsAllowed(url));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox")]
    [InlineData("ms-settings:")]
    [InlineData("\\\\server\\share\\x.exe")]
    [InlineData("ftp://example.com/")]
    [InlineData("example.com")]
    [InlineData("/relative")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://example.com/\nX-Injected: 1")]
    [InlineData("http:///nohost")]
    [InlineData("")]
    [InlineData(null)]
    public void UrlPolicy_RejectsEverythingElse(string? url) => Assert.False(UrlPolicy.IsAllowed(url));

    [Fact]
    public void UrlPolicy_RejectsTooLong() => Assert.False(UrlPolicy.IsAllowed("https://a.example/" + new string('a', 5000)));

    // ---------------- iPhone continuation map ----------------

    [Theory]
    [InlineData("Instagram", null, "instagram", "https://www.instagram.com/direct/inbox/", null)]
    [InlineData("com.burbn.instagram", "https://www.instagram.com/p/C0abc/", "instagram", "https://www.instagram.com/p/C0abc/", null)]
    [InlineData("WhatsApp", null, "whatsapp", "https://web.whatsapp.com/", "whatsapp://")]
    [InlineData("YouTube", null, "youtube", "https://www.youtube.com/", null)]
    [InlineData("Twitter", null, "x", "https://x.com/home", null)]
    [InlineData("Telegram", null, "telegram", "https://web.telegram.org/", "tg://")]
    [InlineData("Spotify", null, "spotify", "https://open.spotify.com/", "spotify:")]
    [InlineData("Haritalar", null, "maps", "https://www.google.com/maps", null)]
    [InlineData("Gmail", null, "gmail", "https://mail.google.com/", null)]
    [InlineData("TikTok", null, "tiktok", "https://www.tiktok.com/", null)]
    [InlineData("Safari", "https://nitrobyte.com.tr/blog", "safari", "https://nitrobyte.com.tr/blog", null)]
    [InlineData(null, "https://youtu.be/xyz", "youtube", "https://youtu.be/xyz", null)]
    [InlineData(null, "https://example.org/x", "web", "https://example.org/x", null)]
    public void Continuation_Resolves(string? app, string? url, string key, string web, string? appUri)
    {
        var t = ContinuationMap.Resolve(app, url);
        Assert.NotNull(t);
        Assert.Equal(key, t!.AppKey);
        Assert.Equal(web, t.WebUrl);
        Assert.Equal(appUri, t.AppUri);
    }

    [Theory]
    [InlineData("Safari", null)]                         // Safari without a URL: nothing to continue
    [InlineData("Bilinmeyen Uygulama", null)]
    [InlineData("Instagram", "javascript:alert(1)")]   // bad URL ignored → app home
    public void Continuation_NoneOrSafeFallback(string app, string? url)
    {
        var t = ContinuationMap.Resolve(app, url);
        if (app == "Instagram") Assert.Equal("https://www.instagram.com/direct/inbox/", t!.WebUrl);
        else Assert.Null(t);
    }

    [Fact]
    public void Continuation_AppSchemesAreFixed() =>
        Assert.All(ContinuationMap.AllowedAppSchemes, s => Assert.DoesNotContain(s, new[] { "http", "https", "file", "javascript", "ms-settings" }));

    // ---------------- native messaging framing ----------------

    [Fact]
    public async Task Framing_RoundTrip_LittleEndian()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, "{\"type\":\"tabs\"}"u8.ToArray(), NativeMessaging.MaxToBrowser);
        await NativeMessaging.WriteAsync(ms, "{}"u8.ToArray(), NativeMessaging.MaxToBrowser);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 15, 0, 0, 0 }, bytes[..4]);
        ms.Position = 0;
        Assert.Equal("{\"type\":\"tabs\"}", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(ms, 1024))!));
        Assert.Equal("{}", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(ms, 1024))!));
        Assert.Null(await NativeMessaging.ReadAsync(ms, 1024)); // clean EOF
    }

    [Fact]
    public async Task Framing_TrickleReads()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, Encoding.UTF8.GetBytes("{\"a\":\"" + new string('z', 5000) + "\"}"), NativeMessaging.MaxToBrowser);
        var frame = await NativeMessaging.ReadAsync(new OneByteStream(ms.ToArray()), 1 << 20);
        Assert.Equal(5008, frame!.Length);
    }

    [Fact]
    public async Task Framing_RejectsOversizeAndTruncation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]), NativeMessaging.MaxFromBrowser));
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(new MemoryStream([10, 0, 0, 0, (byte)'{']), 1024));
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeMessaging.ReadAsync(new MemoryStream([10, 0]), 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.WriteAsync(new MemoryStream(), new byte[NativeMessaging.MaxToBrowser + 1], NativeMessaging.MaxToBrowser));
    }

    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }

    // ---------------- request auth / limits ----------------

    [Fact]
    public void RequestAuth_VerifiesAndRejectsReplayTamperSkew()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var body = "{\"url\":\"https://a.example/\"}"u8.ToArray();
        var h = RequestAuth.Headers("dev1", key, "POST", SyncProtocol.Open, body, "target");
        var replay = new ReplayCache();
        bool V(byte[] k, string path, byte[] b, string target, ReplayCache r) =>
            RequestAuth.Verify(k, "POST", path, h[RequestAuth.HTime], h[RequestAuth.HNonce], h[RequestAuth.HSig], b, target, r);

        Assert.False(V(new byte[32], SyncProtocol.Open, body, "target", new ReplayCache()));   // wrong key
        Assert.False(V(key, SyncProtocol.Launch, body, "target", new ReplayCache()));          // other path
        Assert.False(V(key, SyncProtocol.Open, "{}"u8.ToArray(), "target", new ReplayCache())); // body tampered
        Assert.False(V(key, SyncProtocol.Open, body, "other-device", new ReplayCache()));      // reflected to another PC
        Assert.True(V(key, SyncProtocol.Open, body, "target", replay));
        Assert.False(V(key, SyncProtocol.Open, body, "target", replay));                        // replay

        var old = RequestAuth.Headers("dev1", key, "POST", SyncProtocol.Open, body, "target", DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.False(RequestAuth.Verify(key, "POST", SyncProtocol.Open, old[RequestAuth.HTime], old[RequestAuth.HNonce], old[RequestAuth.HSig], body, "target", new ReplayCache()));
    }

    [Fact]
    public void RateLimiter_Bursts_ThenRefuses()
    {
        var rl = new RateLimiter(burst: 3, perSecond: 0.001);
        Assert.True(rl.TryAcquire("ip"));
        Assert.True(rl.TryAcquire("ip"));
        Assert.True(rl.TryAcquire("ip"));
        Assert.False(rl.TryAcquire("ip"));
        Assert.True(rl.TryAcquire("other"));
    }

    [Theory]
    [InlineData("192.168.1.5", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void NetworkScope_LanOnly(string ip, bool local) => Assert.Equal(local, NetworkScope.IsLocal(IPAddress.Parse(ip)));

    [Fact]
    public void PairingCode_SameTranscriptSameCode_SixDigits()
    {
        var t1 = PairingCrypto.Transcript("a", "b", new string('1', 64), new string('2', 64), [1], [2], new byte[16], new byte[16]);
        var t2 = PairingCrypto.Transcript("a", "b", new string('1', 64), new string('3', 64), [1], [2], new byte[16], new byte[16]);
        Assert.Matches("^[0-9]{6}$", PairingCrypto.Code(t1));
        Assert.Equal(PairingCrypto.Code(t1), PairingCrypto.Code(t1));
        Assert.NotEqual(t1, t2); // a substituted certificate changes the transcript (and almost surely the code)
    }

    [Fact]
    public void PeerStore_PersistsAndProtectsKeys()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sync-peers.json");
        var store = new PeerStore(path);
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        store.AddOrReplace("peer1", "Ev PC", new string('a', 64), key);
        var reloaded = new PeerStore(path);
        Assert.Equal(store.DeviceId, reloaded.DeviceId);
        var p = reloaded.Find("PEER1")!;
        Assert.Equal(key, reloaded.KeyOf(p));
        Assert.DoesNotContain(Convert.ToBase64String(key), File.ReadAllText(path)); // DPAPI-wrapped on disk
    }

    // ---------------- Windows apps ----------------

    [Fact]
    public void AppsProvider_EnumeratesWithoutThrowing_AndRespectsExclusions()
    {
        var opts = new AppsProviderOptions { ExcludeSelf = false, IncludeIcons = false };
        var apps = new WindowsAppsProvider(opts).GetApps();
        Assert.All(apps, a =>
        {
            Assert.False(string.IsNullOrEmpty(a.Info.AppId));
            Assert.Equal(Path.GetFileName(a.FullPath), a.Info.Exe);
        });
        if (apps.Count > 0)
        {
            opts.ExcludedExecutables.Add(apps[0].Info.Exe);
            Assert.DoesNotContain(new WindowsAppsProvider(opts).GetApps(), a => a.Info.Exe == apps[0].Info.Exe);
        }
    }

    [Fact]
    public void IconPng_EncodesValidPng()
    {
        var png = IconPng.EncodePng(2, 2, new byte[16]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        Assert.Equal("IEND", Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    // ---------------- two nodes over loopback ----------------

    private sealed class FakeOpener : IUrlOpener
    {
        public readonly ConcurrentQueue<string> Opened = new();
        public bool OpenWeb(string url) { Assert.True(UrlPolicy.IsAllowed(url)); Opened.Enqueue(url); return true; }
        public bool TryOpenAppUri(string uri) { Opened.Enqueue(uri); return false; }
    }

    private sealed class Node : IAsyncDisposable
    {
        public required NitroSyncService Service;
        public required FakeOpener Opener;
        public required TempDir Dir;
        public string? LastCode;
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); Dir.Dispose(); }
    }

    private static Node NewNode(string name, int mdnsPort, bool accept = true, int iosPort = -1, string pipe = "")
    {
        var dir = new TempDir();
        var opener = new FakeOpener();
        var node = new Node
        {
            Dir = dir, Opener = opener,
            Service = new NitroSyncService(new NitroSyncOptions
            {
                DeviceName = name, Port = 0, MdnsPort = mdnsPort, Interface = "loopback",
                Certificate = CertificateStore.CreateEphemeral(name),
                StorePath = Path.Combine(dir.Path, "sync-peers.json"),
                ShareApps = false, IosPort = iosPort, PipeName = pipe, UrlOpener = opener,
            }),
        };
        node.Service.ConfirmPairing = (p, _) => { node.LastCode = p.Code; return Task.FromResult(accept); };
        return node;
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe) where T : class
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(50);
        }
        throw new TimeoutException("condition not met");
    }

    private static Task WaitUntil(Func<bool> probe) => WaitFor(() => probe() ? "ok" : null);

    [Fact]
    public async Task TwoNodes_PairPinSyncOpen()
    {
        await using var a = NewNode("PC A", 53661);
        await using var b = NewNode("PC B", 53661);
        await Task.WhenAll(a.Service.StartAsync(), b.Service.StartAsync());

        var seenB = await WaitFor(() => a.Service.GetPeers().FirstOrDefault(p => p.DeviceId == b.Service.DeviceId));
        Assert.False(seenB.IsPaired);

        // Unpaired: signed open must fail before pairing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Service.OpenOnPeerAsync(b.Service.DeviceId, "https://a.example/"));

        var result = await a.Service.PairAsync(b.Service.DeviceId);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(a.LastCode);
        Assert.Equal(a.LastCode, b.LastCode); // both users saw the same 6-digit code
        Assert.Equal(result.Code, a.LastCode);
        Assert.Equal(b.Service.CertSha256, a.Service.Store.Find(b.Service.DeviceId)!.CertSha256); // TOFU pin
        Assert.Equal(a.Service.CertSha256, b.Service.Store.Find(a.Service.DeviceId)!.CertSha256);

        // State sync: A's tabs show up at B (B polls A after discovering it).
        var changed = new ConcurrentQueue<SyncPeerInfo>();
        b.Service.PeerStateChanged += (_, p) => changed.Enqueue(p);
        a.Service.UpdateLocalTabs("chrome",
        [
            new BrowserTab { WindowId = 1, TabId = 7, Title = "Nitro", Url = "https://nitrobyte.com.tr/", Active = true },
            new BrowserTab { WindowId = 1, TabId = 8, Title = "Gizli", Url = "https://private.example/", Incognito = true },
        ]);
        var withTabs = await WaitFor(() => b.Service.GetPeers().FirstOrDefault(p => p.DeviceId == a.Service.DeviceId && p.State?.Tabs.Count > 0));
        var tab = Assert.Single(withTabs.State!.Tabs);
        Assert.Equal("https://nitrobyte.com.tr/", tab.Url);
        Assert.Contains(changed, p => p.DeviceId == a.Service.DeviceId);

        // Long-poll picks up a second change.
        a.Service.UpdateLocalTabs("chrome", [new BrowserTab { WindowId = 1, TabId = 9, Title = "İkinci", Url = "https://example.com/2" }]);
        await WaitUntil(() => b.Service.GetPeers().Any(p => p.State?.Tabs.Any(t => t.TabId == 9) == true));

        // Open on peer: http(s) only.
        Assert.True(await a.Service.OpenOnPeerAsync(b.Service.DeviceId, "https://example.com/from-a"));
        Assert.Contains("https://example.com/from-a", b.Opener.Opened);
        await Assert.ThrowsAsync<ArgumentException>(() => a.Service.OpenOnPeerAsync(b.Service.DeviceId, "javascript:alert(1)"));

        // Launch: no confirmation callback (and nothing advertised) → refused.
        Assert.False(await a.Service.LaunchOnPeerAsync(b.Service.DeviceId, "0011223344556677"));

        // Raw signed request with a forbidden scheme is rejected server-side too (400), not just by our client.
        var peerB = a.Service.Store.Find(b.Service.DeviceId)!;
        var key = a.Service.Store.KeyOf(peerB);
        using (var raw = new SyncClient(new IPEndPoint(IPAddress.Loopback, b.Service.Port), NetworkBinding.Any, peerB.CertSha256))
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new OpenRequest("file:///C:/Windows/win.ini"), SyncProtocol.Json);
            var (status, _, _) = await raw.SendSignedAsync(HttpMethod.Post, SyncProtocol.Open, body, a.Service.DeviceId, key, b.Service.DeviceId, null, Timeout, default);
            Assert.Equal(400, status);
        }

        // Pinning: a client expecting another certificate refuses to talk to B.
        using (var wrongPin = new SyncClient(new IPEndPoint(IPAddress.Loopback, b.Service.Port), NetworkBinding.Any, new string('0', 64)))
        {
            var ex = await Assert.ThrowsAsync<SyncException>(() =>
                wrongPin.SendSignedAsync(HttpMethod.Get, SyncProtocol.State, [], a.Service.DeviceId, key, b.Service.DeviceId, null, Timeout, default));
            Assert.Contains("pin", ex.Message);
        }

        // Unpair: both sides forget each other.
        await a.Service.UnpairAsync(b.Service.DeviceId);
        Assert.Null(a.Service.Store.Find(b.Service.DeviceId));
        await WaitUntil(() => b.Service.Store.Find(a.Service.DeviceId) == null);
    }

    [Fact]
    public async Task Pairing_DeclinedByResponder_StoresNothing()
    {
        await using var a = NewNode("PC A", 53662);
        await using var b = NewNode("PC B", 53662, accept: false);
        await Task.WhenAll(a.Service.StartAsync(), b.Service.StartAsync());
        await WaitFor(() => a.Service.GetPeers().FirstOrDefault(p => p.DeviceId == b.Service.DeviceId));
        var r = await a.Service.PairAsync(b.Service.DeviceId);
        Assert.False(r.Success);
        Assert.Empty(a.Service.Store.Peers);
        Assert.Empty(b.Service.Store.Peers);
    }

    [Fact]
    public async Task UnpairedClients_AreRejected()
    {
        await using var b = NewNode("PC B", 53663);
        await b.Service.StartAsync();
        var ep = new IPEndPoint(IPAddress.Loopback, b.Service.Port);
        using var client = new SyncClient(ep, NetworkBinding.Any, pinnedCertSha256: null);

        // /info is public (only id/name/version).
        var (st, info, _) = await client.SendAsync(HttpMethod.Get, SyncProtocol.Info, null, null, Timeout, default);
        Assert.Equal(200, st);
        Assert.Contains(b.Service.DeviceId, Encoding.UTF8.GetString(info));

        // No signature at all.
        (st, _, _) = await client.SendAsync(HttpMethod.Get, SyncProtocol.State, null, null, Timeout, default);
        Assert.Equal(401, st);
        var openBody = JsonSerializer.SerializeToUtf8Bytes(new OpenRequest("https://evil.example/"), SyncProtocol.Json);
        (st, _, _) = await client.SendAsync(HttpMethod.Post, SyncProtocol.Open, openBody, null, Timeout, default);
        Assert.Equal(401, st);

        // Well-formed signature from a device B never paired with.
        (st, _, _) = await client.SendSignedAsync(HttpMethod.Post, SyncProtocol.Open, openBody, "deadbeef0001", new byte[32], b.Service.DeviceId, null, Timeout, default);
        Assert.Equal(401, st);
        (st, _, _) = await client.SendSignedAsync(HttpMethod.Post, SyncProtocol.Launch, "{\"appId\":\"x\"}"u8.ToArray(), "deadbeef0001", new byte[32], b.Service.DeviceId, null, Timeout, default);
        Assert.Equal(401, st);
        Assert.Empty(b.Opener.Opened);

        // A bogus pairing finish (unknown session) is refused.
        (st, _, _) = await client.SendAsync(HttpMethod.Post, SyncProtocol.PairFinish, "{\"sessionId\":\"00\",\"confirm\":\"AA==\"}"u8.ToArray(), null, Timeout, default);
        Assert.Equal(404, st);
    }

    // ---------------- iOS Shortcut receiver ----------------

    private static async Task<(int status, string body)> PostHttp(int port, string pathAndQuery, string body, string? bearer = null, string contentType = "application/json")
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var s = tcp.GetStream();
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"POST {pathAndQuery} HTTP/1.1\r\nHost: x\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n"
                   + (bearer != null ? $"Authorization: Bearer {bearer}\r\n" : "") + "\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
        using var r = new StreamReader(s);
        var text = await r.ReadToEndAsync();
        var status = int.Parse(text.Split(' ')[1]);
        return (status, text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }

    [Fact]
    public async Task IosReceiver_TokenRequired_ResolvesActivity()
    {
        await using var pc = NewNode("PC", 53664, iosPort: 0);
        await pc.Service.StartAsync();
        var setup = pc.Service.AddIosDevice("Emre'nin iPhone'u");
        Assert.Contains("?t=" + setup.Token, setup.Url);
        Assert.DoesNotContain(setup.Token, File.ReadAllText(pc.Service.Store.FilePath)); // only the hash is stored
        var activities = new ConcurrentQueue<IphoneActivity>();
        pc.Service.IphoneActivityChanged += (_, a) => activities.Enqueue(a);
        int port = pc.Service.IosPort;

        Assert.Equal(401, (await PostHttp(port, IosReceiver.ActivityPath, "{\"app\":\"Instagram\"}")).status);
        Assert.Equal(401, (await PostHttp(port, IosReceiver.ActivityPath + "?t=wrong", "{\"app\":\"Instagram\"}")).status);
        Assert.Empty(activities);

        // "When Instagram is opened" automation.
        Assert.Equal(200, (await PostHttp(port, IosReceiver.ActivityPath, "{\"app\":\"Instagram\"}", bearer: setup.Token)).status);
        var a = Assert.Single(activities);
        Assert.Equal("instagram", a.Target!.AppKey);
        Assert.Null(a.Url);
        Assert.Empty(pc.Opener.Opened); // not opened automatically

        // Share sheet: bare URL as text/plain with the token in the query.
        Assert.Equal(200, (await PostHttp(port, IosReceiver.ActivityPath + "?t=" + setup.Token, "https://www.instagram.com/p/XYZ/", contentType: "text/plain")).status);
        Assert.Equal("https://www.instagram.com/p/XYZ/", pc.Service.IphoneActivities[0].Url);

        // Dangerous URL refused.
        Assert.Equal(400, (await PostHttp(port, IosReceiver.ActivityPath, "{\"url\":\"javascript:alert(1)\"}", bearer: setup.Token)).status);

        // "PC'de devam et".
        Assert.True(await pc.Service.ContinueOnPcAsync(pc.Service.IphoneActivities[0]));
        Assert.Contains("https://www.instagram.com/p/XYZ/", pc.Opener.Opened);

        // WhatsApp: app URI tried first, web fallback when not installed (fake opener says "not installed").
        Assert.True(await pc.Service.ContinueOnPcAsync(new IphoneActivity { DeviceId = "x", DeviceName = "x", App = "WhatsApp" }));
        Assert.Contains("whatsapp://", pc.Opener.Opened);
        Assert.Contains("https://web.whatsapp.com/", pc.Opener.Opened);

        // Revoked token stops working.
        Assert.True(pc.Service.RemoveIosDevice(setup.DeviceId));
        Assert.Equal(401, (await PostHttp(port, IosReceiver.ActivityPath, "{\"app\":\"Instagram\"}", bearer: setup.Token)).status);
    }

    [Fact]
    public void IosReceiver_ParsesJsonFormAndText()
    {
        Assert.Equal(("Safari", "https://a.example/", (string?)null), IosReceiver.ParseBody("{\"app\":\"Safari\",\"url\":\"https://a.example/\"}"u8.ToArray(), "application/json"));
        Assert.Equal(("WhatsApp", (string?)null, (string?)null), IosReceiver.ParseBody("app=WhatsApp"u8.ToArray(), "application/x-www-form-urlencoded"));
        Assert.Equal(((string?)null, "https://a.example/?q=1&b=2", (string?)null), IosReceiver.ParseBody("https://a.example/?q=1&b=2"u8.ToArray(), "text/plain"));
    }

    // ---------------- extension pipe bridge ----------------

    [Fact]
    public async Task Pipe_TabsFromExtension_UpdateLocalState()
    {
        var pipeName = "NitroDrop.Sync.test-" + Guid.NewGuid().ToString("N");
        await using var pc = NewNode("PC", 53665, pipe: pipeName);
        await pc.Service.StartAsync();

        await using var c = await Nitro.Sync.Ipc.PipeConnect.ConnectAsync(pipeName, 5000);

        async Task Send(JsonObject o) => await NativeMessaging.WriteAsync(c, JsonSerializer.SerializeToUtf8Bytes(o), NativeMessaging.MaxFromBrowser);
        async Task<JsonObject> ReceiveType(string type)
        {
            while (true)
            {
                var f = await NativeMessaging.ReadAsync(c, NativeMessaging.MaxToBrowser).WaitAsync(Timeout);
                var o = (JsonObject)JsonNode.Parse(f!)!;
                if ((string?)o["type"] == type) return o;
            }
        }

        await Send(new JsonObject { ["type"] = "hello", ["browser"] = "edge" });
        Assert.Equal(pc.Service.DeviceId, (string?)(await ReceiveType("hello"))["deviceId"]);

        await Send(new JsonObject
        {
            ["type"] = "tabs", ["reqId"] = 1,
            ["tabs"] = new JsonArray(
                new JsonObject { ["windowId"] = 1, ["tabId"] = 2, ["title"] = "Bing", ["url"] = "https://www.bing.com/", ["active"] = true },
                new JsonObject { ["windowId"] = 3, ["tabId"] = 4, ["title"] = "Gizli", ["url"] = "https://x.example/", ["incognito"] = true }),
        });
        var ack = await ReceiveType("ack");
        Assert.Equal(1, (int)ack["reqId"]!);
        var t = Assert.Single(pc.Service.LocalState.Tabs);
        Assert.Equal("edge", t.Browser);

        await Send(new JsonObject { ["type"] = "getTargets", ["reqId"] = "t" });
        Assert.Empty((JsonArray)(await ReceiveType("targets"))["targets"]!);

        await Send(new JsonObject { ["type"] = "sendLink", ["targetId"] = "nope", ["url"] = "javascript:alert(1)" });
        Assert.Equal("bad_url", (string?)(await ReceiveType("error"))["error"]);

        // Browser closes → its tabs disappear from the shared state.
        await c.DisposeAsync();
        await WaitUntil(() => pc.Service.LocalState.Tabs.Count == 0);
    }
}
