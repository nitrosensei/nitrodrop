using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using Nitro.Sync.Ipc;

namespace Nitro.Sync.Host;

/// <summary>
/// NitroDrop native messaging host.
///   (no args / chrome-extension://… origin)  → relay mode, started by the browser
///   register --extension-id &lt;id&gt; [--extension-id &lt;id2&gt;] [--host-path &lt;exe&gt;]
///   unregister
/// stdout is the browser channel: nothing else may ever be written to it in relay mode.
/// </summary>
internal static class Program
{
    private const string ChromeKey = @"Software\Google\Chrome\NativeMessagingHosts\" + NativeMessaging.HostName;
    private const string EdgeKey = @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeMessaging.HostName;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "register") return Register(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "unregister") return Unregister();
        if (args.Length > 0 && (args[0] is "-h" or "--help" or "help"))
        {
            Console.Error.WriteLine("NitroDrop.SyncHost register --extension-id <id> [--extension-id <id2>] [--host-path <exe>] | unregister");
            return 0;
        }
        // Chrome passes the caller origin as the first argument (and --parent-window=… on Windows).
        var origin = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal));
        return await RelayAsync(origin);
    }

    // ---------------- relay ----------------

    private static async Task<int> RelayAsync(string? origin)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        var writeLock = new SemaphoreSlim(1, 1);

        async Task ToBrowser(byte[] json)
        {
            if (json.Length > NativeMessaging.MaxToBrowser) return;
            await writeLock.WaitAsync();
            try { await NativeMessaging.WriteAsync(stdout, json, NativeMessaging.MaxToBrowser); }
            finally { writeLock.Release(); }
        }

        await using var pipe = new NamedPipeClientStream(".", NativeMessaging.PipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); // server must run as this user
        try { await pipe.ConnectAsync(3000); }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await ToBrowser(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":\"nitrodrop_not_running\"}"));
            return 2;
        }

        var hello = new JsonObject { ["type"] = "hostHello", ["origin"] = origin };
        await NativeMessaging.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(hello), NativeMessaging.MaxFromBrowser);

        using var cts = new CancellationTokenSource();
        var browserToApp = Task.Run(async () =>
        {
            try
            {
                while (await NativeMessaging.ReadAsync(stdin, NativeMessaging.MaxFromBrowser, cts.Token) is { } frame)
                    await NativeMessaging.WriteAsync(pipe, frame, NativeMessaging.MaxFromBrowser, cts.Token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { }
        });
        var appToBrowser = Task.Run(async () =>
        {
            try
            {
                while (await NativeMessaging.ReadAsync(pipe, NativeMessaging.MaxToBrowser, cts.Token) is { } frame)
                    await ToBrowser(frame);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { }
        });
        await Task.WhenAny(browserToApp, appToBrowser); // either side closed → exit (browser restarts us on reconnect)
        cts.Cancel();
        return 0;
    }

    // ---------------- registration ----------------

    private static int Register(string[] args)
    {
        var ids = new List<string>();
        string hostPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NitroDrop.SyncHost.exe");
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--extension-id" && i + 1 < args.Length) ids.Add(args[++i]);
            else if (args[i] == "--host-path" && i + 1 < args.Length) hostPath = Path.GetFullPath(args[++i]);
        }
        if (ids.Count == 0 || ids.Any(id => id.Length != 32 || id.Any(c => c < 'a' || c > 'p')))
        {
            Console.Error.WriteLine("En az bir geçerli --extension-id gerekli (32 harf, a-p).");
            return 1;
        }
        var manifestPath = ManifestPath();
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, BuildManifest(hostPath, ids), new UTF8Encoding(false));
        foreach (var key in new[] { ChromeKey, EdgeKey })
        {
            using var k = Registry.CurrentUser.CreateSubKey(key);
            k.SetValue("", manifestPath);
        }
        Console.WriteLine($"Kaydedildi: {manifestPath}");
        return 0;
    }

    private static int Unregister()
    {
        foreach (var key in new[] { ChromeKey, EdgeKey }) Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        try { File.Delete(ManifestPath()); } catch (IOException) { }
        Console.WriteLine("Kayıt kaldırıldı.");
        return 0;
    }

    internal static string ManifestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "native-host", NativeMessaging.HostName + ".json");

    internal static string BuildManifest(string hostPath, IEnumerable<string> extensionIds)
    {
        var origins = new JsonArray();
        foreach (var id in extensionIds) origins.Add($"chrome-extension://{id}/");
        var m = new JsonObject
        {
            ["name"] = NativeMessaging.HostName,
            ["description"] = "NitroDrop sekme ve uygulama eşitleme köprüsü",
            ["path"] = hostPath,
            ["type"] = "stdio",
            ["allowed_origins"] = origins,
        };
        return m.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
