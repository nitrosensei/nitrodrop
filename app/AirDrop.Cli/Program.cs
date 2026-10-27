using System.Net;
using AirDrop.Core;
using AirDrop.Core.Mdns;
using AirDrop.Core.Net;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

var (positional, opts) = ParseArgs(args.Skip(1).ToArray());
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Action<string>? log = opts.ContainsKey("verbose") ? msg => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}") : null;

try
{
    var binding = NetworkBinding.Resolve(opts.GetValueOrDefault("interface"));
    int mdnsPort = int.Parse(opts.GetValueOrDefault("mdns-port") ?? MdnsService.DefaultPort.ToString());
    var identity = new AirDropIdentity
    {
        ComputerName = opts.GetValueOrDefault("name") ?? Environment.MachineName,
    };

    switch (args[0])
    {
        case "serve":
            return await Serve(binding, identity, mdnsPort);
        case "browse":
            return await Browse(binding, identity, mdnsPort);
        case "send":
            return await Send(binding, identity, mdnsPort);
        default:
            PrintUsage();
            return 2;
    }
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or AirDropRequestException or HttpRequestException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"Hata: {e.Message}");
    return 1;
}

async Task<int> Serve(NetworkBinding binding, AirDropIdentity identity, int mdnsPort)
{
    var dir = opts.GetValueOrDefault("dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AirDrop");
    bool autoAccept = opts.ContainsKey("auto-accept");
    var promptLock = new SemaphoreSlim(1);
    await using var server = new AirDropServer(new AirDropServerOptions
    {
        ReceiveDirectory = dir,
        Identity = identity,
        Binding = binding,
        Port = int.Parse(opts.GetValueOrDefault("port") ?? AirDropConstants.DefaultPort.ToString()),
        EnableMdns = !opts.ContainsKey("no-mdns"),
        MdnsPort = mdnsPort,
        AskHandler = async (ask, ct) =>
        {
            Console.WriteLine($"\n{ask.SenderComputerName ?? "?"} ({ask.SenderModelName}, {ask.RemoteEndPoint.Address}) dosya göndermek istiyor:");
            foreach (var f in ask.Files) Console.WriteLine($"  - {f.FileName} [{f.FileType}]{(f.IsDirectory ? " (klasör)" : "")}");
            foreach (var u in ask.Items) Console.WriteLine($"  - {u}");
            if (autoAccept) { Console.WriteLine("  -> otomatik kabul"); return true; }
            await promptLock.WaitAsync(ct);
            try
            {
                Console.Write("Kabul edilsin mi? [e/H] ");
                var line = await Task.Run(Console.ReadLine, ct);
                return line?.Trim().ToLowerInvariant() is "e" or "y" or "evet" or "yes";
            }
            finally { promptLock.Release(); }
        },
    }) { Log = log };
    server.FilesReceived += r =>
    {
        Console.WriteLine($"Alındı ({r.TotalBytes:N0} bayt):");
        foreach (var p in r.Paths) Console.WriteLine($"  {p}");
    };
    await server.StartAsync(cts.Token);
    Console.WriteLine($"AirDrop alıcı hazır: '{identity.ComputerName}', id {identity.ServiceId}, port {server.Port}, arayüz {binding}, klasör {dir}");
    Console.WriteLine("Durdurmak için Ctrl+C.");
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
    return 0;
}

async Task<int> Browse(NetworkBinding binding, AirDropIdentity identity, int mdnsPort)
{
    var seconds = int.Parse(opts.GetValueOrDefault("timeout") ?? "10");
    await using var browser = new AirDropBrowser(binding, mdnsPort, log);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"_airdrop._tcp aranıyor ({seconds} sn, arayüz {binding})...");
    var discovers = new List<Task>();
    await browser.BrowseAsync(peer =>
    {
        Console.WriteLine($"Bulundu: {peer}");
        discovers.Add(Task.Run(async () =>
        {
            try
            {
                using var client = new AirDropClient(peer.EndPoint, identity, binding, peer.HostName) { DiscoverTimeout = TimeSpan.FromSeconds(5) };
                var d = await client.DiscoverAsync();
                Console.WriteLine($"  {peer.InstanceName}: {(d.IsDiscoverable ? $"'{d.ReceiverComputerName}' ({d.ReceiverModelName})" : "keşfedilemez (Yalnızca Kişiler?)")}");
            }
            catch (Exception e) { Console.WriteLine($"  {peer.InstanceName}: /Discover başarısız: {e.Message}"); }
        }));
    }, timeout.Token);
    await Task.WhenAll(discovers);
    return 0;
}

async Task<int> Send(NetworkBinding binding, AirDropIdentity identity, int mdnsPort)
{
    if (positional.Count < 2) { PrintUsage(); return 2; }
    var target = positional[0];
    var files = positional.Skip(1).ToList();

    IPEndPoint endpoint;
    string? host = null;
    if (TryParseEndPoint(target, out var ep)) endpoint = ep!;
    else
    {
        await using var browser = new AirDropBrowser(binding, mdnsPort, log);
        var seconds = int.Parse(opts.GetValueOrDefault("timeout") ?? "10");
        Console.WriteLine($"'{target}' aranıyor...");
        var peer = await browser.FindAsync(p =>
            p.InstanceName.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            p.HostName.TrimEnd('.').Equals(target, StringComparison.OrdinalIgnoreCase) ||
            p.HostName.TrimEnd('.').Equals(target + ".local", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(seconds), cts.Token);
        if (peer == null) { Console.Error.WriteLine("Alıcı bulunamadı."); return 1; }
        endpoint = peer.EndPoint;
        host = peer.HostName;
    }

    using var client = new AirDropClient(endpoint, identity, binding, host) { Log = log };
    var last = DateTime.MinValue;
    var progress = new Progress<TransferProgress>(p =>
    {
        if ((DateTime.UtcNow - last).TotalMilliseconds < 200 && p.BytesTransferred != p.TotalBytes) return;
        last = DateTime.UtcNow;
        Console.Write($"\r  {p.CurrentFile}: {p.BytesTransferred:N0}/{p.TotalBytes:N0} bayt   ");
    });
    Console.WriteLine($"{endpoint} adresine gönderiliyor (alıcının onayı bekleniyor)...");
    bool ok = await client.SendAsync(files, progress, cts.Token);
    Console.WriteLine();
    Console.WriteLine(ok ? "Gönderildi." : "Alıcı reddetti.");
    return ok ? 0 : 3;
}

static bool TryParseEndPoint(string s, out IPEndPoint? ep)
{
    ep = null;
    if (IPEndPoint.TryParse(s, out var parsed) && parsed.Port != 0) { ep = parsed; return true; }
    int colon = s.LastIndexOf(':');
    if (colon > 0 && !s.Contains(']') && int.TryParse(s[(colon + 1)..], out int port))
    {
        try
        {
            var addr = Dns.GetHostAddresses(s[..colon]).OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).First();
            ep = new IPEndPoint(addr, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException) { }
    }
    return false;
}

static (List<string> positional, Dictionary<string, string?> opts) ParseArgs(string[] a)
{
    var flags = new HashSet<string> { "auto-accept", "no-mdns", "verbose" };
    var pos = new List<string>();
    var o = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--"))
        {
            var key = a[i][2..];
            if (flags.Contains(key)) o[key] = null;
            else if (i + 1 < a.Length) o[key] = a[++i];
            else throw new ArgumentException($"--{key} bir değer bekliyor");
        }
        else pos.Add(a[i]);
    }
    return (pos, o);
}

static void PrintUsage()
{
    Console.WriteLine("""
        airdrop-win CLI

          serve  [--dir <klasör>] [--port 8770] [--auto-accept] [--no-mdns]
          browse [--timeout 10]
          send   <peer-id | hostname | host:port> <dosya/klasör...> [--timeout 10]

        Ortak: --interface <ad|index|loopback|any>  --name <bilgisayar adı>  --mdns-port 5353  --verbose
        """);
}
