using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Nitro.Sync.Model;

namespace Nitro.Sync.Windows;

/// <summary>A running app as seen locally; <see cref="FullPath"/> never leaves the PC.</summary>
public sealed record LocalApp(AppInfo Info, string FullPath);

public interface IAppsProvider
{
    IReadOnlyList<LocalApp> GetApps();
}

public sealed class AppsProviderOptions
{
    /// <summary>Executable file names never reported (case-insensitive), e.g. password managers.</summary>
    public HashSet<string> ExcludedExecutables { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "KeePass.exe", "KeePassXC.exe", "1Password.exe", "Bitwarden.exe", "LastPass.exe", "Dashlane.exe",
        "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "LockApp.exe", "SystemSettings.exe",
    };
    /// <summary>Send window titles (may contain document names). Private-browsing windows are always redacted.</summary>
    public bool IncludeWindowTitles { get; set; } = true;
    public bool IncludeIcons { get; set; } = true;
    /// <summary>Our own process is skipped by default.</summary>
    public bool ExcludeSelf { get; set; } = true;
}

/// <summary>
/// User-visible top-level windows via EnumWindows: visible, unowned, not cloaked (hidden UWP/virtual-desktop
/// windows), not tool windows, with a title. UWP apps hosted by ApplicationFrameHost are resolved to the
/// real process. No WPF/WinForms dependency.
/// </summary>
public sealed class WindowsAppsProvider(AppsProviderOptions? options = null) : IAppsProvider
{
    private readonly AppsProviderOptions _o = options ?? new AppsProviderOptions();
    private readonly Dictionary<string, (string name, string? icon)> _exeCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] PrivateMarkers =
        ["InPrivate", "Incognito", "Gizli", "Private Browsing", "Özel Gezinti", "Özel pencere", "Private Window"];

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow" };

    public IReadOnlyList<LocalApp> GetApps()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var fg = GetForegroundWindow();
        var hwnds = new List<IntPtr>();
        EnumWindows((h, _) => { hwnds.Add(h); return true; }, IntPtr.Zero);
        int self = Environment.ProcessId;
        var result = new Dictionary<string, LocalApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var h in hwnds)
        {
            if (!IsUserWindow(h)) continue;
            var title = WindowText(h);
            if (string.IsNullOrWhiteSpace(title)) continue;
            GetWindowThreadProcessId(h, out uint pid);
            var cls = ClassName(h);
            if (cls == "ApplicationFrameWindow") pid = ResolveUwpPid(h, pid);
            if (_o.ExcludeSelf && pid == self) continue;
            var path = ProcessPath(pid);
            if (path == null) continue;
            var exe = Path.GetFileName(path);
            if (_o.ExcludedExecutables.Contains(exe) || exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) continue;

            bool isPrivate = PrivateMarkers.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));
            var (name, icon) = Describe(path);
            var info = new AppInfo
            {
                AppId = AppInfo.ForExecutable(path),
                Name = name,
                Exe = exe,
                WindowTitle = _o.IncludeWindowTitles && !isPrivate ? title : null,
                Foreground = h == fg,
                IconPng = _o.IncludeIcons ? icon : null,
            };
            // One entry per app; keep the foreground window's title if any.
            if (!result.TryGetValue(info.AppId, out var existing) || (info.Foreground && !existing.Info.Foreground))
                result[info.AppId] = new LocalApp(info, path);
        }
        return result.Values.OrderByDescending(a => a.Info.Foreground).ThenBy(a => a.Info.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsUserWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !ShellClasses.Contains(ClassName(h));
    }

    private static uint ResolveUwpPid(IntPtr frame, uint framePid)
    {
        uint found = framePid;
        EnumChildWindows(frame, (c, _) =>
        {
            GetWindowThreadProcessId(c, out uint p);
            if (p != framePid) { found = p; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private (string name, string? icon) Describe(string path)
    {
        if (_exeCache.TryGetValue(path, out var c)) return c;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(vi.ProductName)) name = vi.ProductName.Trim();
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { }
        string? icon = null;
        try { icon = IconPng.SmallIconBase64(path); }
        catch (Exception e) when (e is ExternalException or IOException or ArgumentException) { }
        if (_exeCache.Count > 512) _exeCache.Clear();
        return _exeCache[path] = (name, icon);
    }

    private static string WindowText(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(Math.Min(len, 1024) + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string? ProcessPath(uint pid)
    {
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    // ---------------- Win32 ----------------
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    private const int GW_OWNER = 4, GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

/// <summary>Extracts an executable's small (16×16) icon and encodes it as PNG without System.Drawing.</summary>
internal static class IconPng
{
    public static string? SmallIconBase64(string exePath)
    {
        var small = new IntPtr[1];
        if (ExtractIconEx(exePath, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) return null;
        try
        {
            var png = ToPng(small[0]);
            return png == null ? null : Convert.ToBase64String(png);
        }
        finally { DestroyIcon(small[0]); }
    }

    private static byte[]? ToPng(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var ii)) return null;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return null;
            if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0 || w > 64 || h > 64) return null;
            var bgra = new byte[w * h * 4];
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, ii.hbmColor, 0, (uint)h, bgra, ref bi, 0) == 0) return null;
                bool hasAlpha = false;
                for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) { hasAlpha = true; break; }
                if (!hasAlpha && ii.hbmMask != IntPtr.Zero)
                {
                    // Old-style icon: alpha from the AND mask (1 = transparent).
                    var mask = new byte[w * h * 4];
                    var mi = bi;
                    if (GetDIBits(dc, ii.hbmMask, 0, (uint)h, mask, ref mi, 0) != 0)
                        for (int i = 0; i < w * h; i++) bgra[i * 4 + 3] = mask[i * 4] == 0 ? (byte)255 : (byte)0;
                    else for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
                }
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return EncodePng(w, h, bgra);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    /// <summary>Minimal RGBA PNG encoder (one IDAT, filter 0).</summary>
    internal static byte[] EncodePng(int w, int h, byte[] bgra)
    {
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 4 + 1);
            raw[o] = 0;
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = o + 1 + x * 4;
                raw[d] = bgra[s + 2]; raw[d + 1] = bgra[s + 1]; raw[d + 2] = bgra[s]; raw[d + 3] = bgra[s + 3];
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> b4 = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b4, data.Length);
        s.Write(b4);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32([.. t, .. data]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b4, crc);
        s.Write(b4);
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
}
