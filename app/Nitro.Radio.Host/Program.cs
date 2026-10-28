using System.Diagnostics;
using System.Text;
using Nitro.Awdl.Ipc;

namespace Nitro.Radio.Host;

/// <summary>
/// NitroDrop.Radio.exe --user-sid &lt;SID&gt; --parent-pid &lt;pid&gt; [--wintun-dir &lt;dir&gt;]
/// Started elevated by NitroDrop.exe (ShellExecute "runas"). Serves one app connection on
/// "NitroDrop.Radio.&lt;SID&gt;" and exits when it ends; the radio is stopped (driver STOP) on the way out. If this process
/// is killed, closing the device handle stops the radio too and Wintun removes the adapter it created.
/// Log: %LOCALAPPDATA%\airdrop-win\logs\radio.log (of the elevated account) and forwarded to the app.
/// </summary>
internal static class Program
{
    private static readonly object LogLock = new();
    private static string? _logPath;

    private static async Task<int> Main(string[] args)
    {
        string? sid = Arg(args, "--user-sid");
        if (!RadioPipe.IsValidSid(sid) || !int.TryParse(Arg(args, "--parent-pid"), out int parentPid))
        {
            Console.Error.WriteLine("NitroDrop.Radio --user-sid <SID> --parent-pid <pid>   (started by NitroDrop.exe)");
            return 2;
        }
        OpenLog();
        Log($"start: pid {Environment.ProcessId}, user {sid}, parent {parentPid}");
        // The app's %LOCALAPPDATA%irdrop-win\wintun (differs from ours when another admin account approved UAC).
        // Untrusted input: the resolver loads only the pinned, WireGuard-signed build from there.
        if (Arg(args, "--wintun-dir") is { Length: > 0 } wintunDir && Path.IsPathFullyQualified(wintunDir))
            Nitro.Awdl.Ip.WintunInstall.Directory = wintunDir;

        Task parentExited;
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parentExited = parent.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            Log("parent process is already gone");
            return 5;
        }

        await using var stack = new AwdlRadioStack();
        var server = new RadioHostServer(stack, new RadioHostServerOptions
        {
            PipeName = RadioPipe.NameFor(sid!),
            UserSid = sid!,
            ExpectedClientPid = parentPid,
            ParentExited = parentExited,
            Log = Log,
        });
        var exit = await server.RunAsync();
        Log($"exit: {exit}");
        return (int)exit;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void OpenLog()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win", "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "radio.log");
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 4 * 1024 * 1024)
                File.Move(_logPath, _logPath + ".1", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _logPath = null; }
    }

    private static void Log(string line)
    {
        if (_logPath is null) return;
        lock (LogLock)
        {
            try { File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}", Encoding.UTF8); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
