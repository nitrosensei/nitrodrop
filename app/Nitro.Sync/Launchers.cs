using System.Diagnostics;
using System.Runtime.InteropServices;
using Nitro.Sync.Model;
using Nitro.Sync.Security;

namespace Nitro.Sync;

/// <summary>Opens validated URLs locally. Swappable for tests.</summary>
public interface IUrlOpener
{
    /// <summary>Opens an http/https URL in the default browser. Implementations must re-validate.</summary>
    bool OpenWeb(string url);
    /// <summary>Opens an app protocol URI from <see cref="ContinuationMap.AllowedAppSchemes"/> if a handler is registered.</summary>
    bool TryOpenAppUri(string uri);
}

public interface IAppLauncher
{
    /// <summary>Starts a local executable (from our own window enumeration — never a remote-supplied path), no arguments.</summary>
    bool Launch(string exeFullPath);
}

/// <summary>Default browser / protocol handler via the shell. No arguments are ever passed to a process.</summary>
public sealed class ShellUrlOpener : IUrlOpener
{
    public bool OpenWeb(string url)
    {
        if (!UrlPolicy.TryNormalize(url, out var safe)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public bool TryOpenAppUri(string uri)
    {
        int colon = uri.IndexOf(':');
        if (colon <= 0) return false;
        var scheme = uri[..colon].ToLowerInvariant();
        if (!ContinuationMap.AllowedAppSchemes.Contains(scheme) || !IsProtocolRegistered(scheme)) return false;
        if (uri.Any(c => char.IsControl(c) || c == ' ' || c == '"')) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>HKEY_CLASSES_ROOT\&lt;scheme&gt; with a "URL Protocol" value (e.g. WhatsApp Desktop registers whatsapp:).</summary>
    public static bool IsProtocolRegistered(string scheme)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
        return key?.GetValue("URL Protocol") != null;
    }
}

public sealed class ProcessAppLauncher : IAppLauncher
{
    public bool Launch(string exeFullPath)
    {
        if (!Path.IsPathFullyQualified(exeFullPath) || !File.Exists(exeFullPath)
            || !exeFullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exeFullPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exeFullPath)! });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ExternalException) { return false; }
    }
}
