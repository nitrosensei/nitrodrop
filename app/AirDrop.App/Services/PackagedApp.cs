using System;
using System.IO;
using Nitro.Continuity.Platform;

namespace AirDrop.App.Services;

/// <summary>
/// Differences between the MSIX build (packaging/AppxManifest.xml) and the portable folder.
/// The MSIX disables file-system and registry write virtualization (desktop6 + rescap:unvirtualizedResources), so
/// %LOCALAPPDATA%\airdrop-win and HKCU behave exactly like the portable build (the elevated setup helper, the browser
/// and the portable build all see the same files/keys). What still differs: the install folder is read-only and
/// versioned (C:\Program Files\WindowsApps\Nitrobyte.NitroDrop_&lt;ver&gt;_x64__&lt;hash&gt;), "start with Windows" is a
/// StartupTask, and launching the exe by path gives no package identity — the app execution aliases do.
/// </summary>
internal static class PackagedApp
{
    /// <summary>Must match &lt;desktop:StartupTask TaskId&gt; in packaging/AppxManifest.xml.</summary>
    public const string StartupTaskId = "NitroDropStartup";

    public static bool IsPackaged => PackageIdentity.HasIdentity;

    /// <summary>
    /// %LOCALAPPDATA%\Microsoft\WindowsApps\&lt;exe&gt; (uap3:AppExecutionAlias) when packaged and present, else null.
    /// The alias path survives updates and starts the target with package identity; arguments are passed through.
    /// </summary>
    public static string? Alias(string exeName)
    {
        if (!IsPackaged) return null;
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "Microsoft", "WindowsApps", exeName);
            return File.Exists(p) ? p : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Command to start NitroDrop again later (RunOnce etc.): the alias when packaged, else this exe.</summary>
    public static string LauncherPath => Alias("NitroDrop.exe") ?? Environment.ProcessPath!;

    /// <summary>True when Windows started us through the MSIX StartupTask (it passes no command line).</summary>
    public static bool LaunchedByStartupTask()
    {
        if (!IsPackaged) return false;
        try
        {
            return Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind ==
                   Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Copies a folder that ships next to the exe into %LOCALAPPDATA%\airdrop-win\&lt;name&gt; and returns that path.
    /// Used for the unpacked browser extension: the WindowsApps folder is versioned (an update would break
    /// "Load unpacked") and its parent cannot be browsed in the folder picker. Falls back to the bundled folder.
    /// </summary>
    public static string StableCopy(string name)
    {
        var src = Path.Combine(AppContext.BaseDirectory, name);
        try
        {
            if (!Directory.Exists(src)) return src;
            var dest = Path.Combine(AppSettings.DataDir, name);
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dest, Path.GetRelativePath(src, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var fi = new FileInfo(file);
                var ti = new FileInfo(target);
                if (!ti.Exists || ti.Length != fi.Length || ti.LastWriteTimeUtc != fi.LastWriteTimeUtc)
                {
                    File.Copy(file, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
                }
            }
            return dest;
        }
        catch (Exception) { return src; }
    }
}
