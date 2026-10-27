using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Nitro.Sync.Ipc;

namespace AirDrop.App.Services;

/// <summary>Browser extension + native messaging host that ship next to NitroDrop.exe.</summary>
internal static class ExtensionHost
{
    /// <summary>Fixed id of the unpacked extension (derived from the "key" in extension/manifest.json).</summary>
    public const string ExtensionId = "kbdhonkcoaopcnmbnphldonokaooaibp";

    public static string HostExe => Path.Combine(AppContext.BaseDirectory, "NitroDrop.SyncHost.exe");
    public static string ExtensionDir => Path.Combine(AppContext.BaseDirectory, "extension");

    private static readonly string[] Keys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts\" + NativeMessaging.HostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeMessaging.HostName,
    };

    /// <summary>True when Chrome or Edge has our host registered (HKCU) and its manifest exists.</summary>
    public static bool IsRegistered()
    {
        try
        {
            return Keys.Any(k =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(k);
                return key?.GetValue(null) is string manifest && File.Exists(manifest);
            });
        }
        catch (Exception) { return false; }
    }

    /// <summary>Runs <c>NitroDrop.SyncHost.exe register --extension-id …</c> (writes HKCU only).</summary>
    public static async Task<(bool Ok, string Message)> RegisterAsync()
    {
        if (!File.Exists(HostExe)) return (false, "NitroDrop.SyncHost.exe bulunamadı");
        try
        {
            var psi = new ProcessStartInfo(HostExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("register");
            psi.ArgumentList.Add("--extension-id");
            psi.ArgumentList.Add(ExtensionId);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var text = ((await outp) + (await err)).Trim();
            return p.ExitCode == 0 ? (true, "Chrome ve Edge'e bağlandı") : (false, text.Length > 0 ? text : $"çıkış kodu {p.ExitCode}");
        }
        catch (Exception e) { return (false, e.Message); }
    }
}

/// <summary>Finds a local executable for an app another PC advertises (by file name only — paths never cross the network).</summary>
internal static class LocalApps
{
    public static string? Find(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || exe.IndexOfAny(new[] { '\\', '/', ':', '"' }) >= 0 ||
            !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.Length > 120)
            return null;
        try
        {
            // 1. App Paths (what Win+R uses): Code.exe, chrome.exe, Spotify.exe, …
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue(null) is string p && File.Exists(p = Environment.ExpandEnvironmentVariables(p.Trim('"'))))
                    return p;
            }
            // 2. Windows itself: notepad.exe, explorer.exe, mspaint.exe…
            foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
            // 3. Already running here (per-user installs like Discord / Spotify that skip App Paths).
            foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (proc)
                {
                    try { if (proc.MainModule?.FileName is { } f && File.Exists(f)) return f; }
                    catch (Exception) { /* access denied / exited */ }
                }
            }
        }
        catch (Exception) { }
        return null;
    }

    public static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Debug.WriteLine(e); }
    }
}

/// <summary>QR code as a crisp vector image (QRCoder computes the modules; WPF draws them).</summary>
internal static class QrImage
{
    public static ImageSource Create(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var m = data.ModuleMatrix;   // includes the 4-module quiet zone
        int n = m.Count;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    if (!m[y][x]) continue;
                    ctx.BeginFigure(new Point(x, y), true, true);
                    ctx.PolyLineTo(new[] { new Point(x + 1, y), new Point(x + 1, y + 1), new Point(x, y + 1) }, false, false);
                }
        }
        geo.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, n, n))));
        group.Children.Add(new GeometryDrawing(Brushes.Black, null, geo));
        group.Freeze();
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }
}
