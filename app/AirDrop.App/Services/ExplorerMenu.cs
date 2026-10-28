using System;
using System.IO;

namespace AirDrop.App.Services;

/// <summary>HKCU, relative paths (<c>Software\Classes\…</c>). Real: <c>Win32UserRegistry</c>; tests: an in-memory fake.</summary>
public interface IUserRegistry
{
    string? GetString(string subKey, string? name);
    void SetString(string subKey, string? name, string value);
    /// <summary>Deletes the key and everything under it; no error when missing.</summary>
    void DeleteTree(string subKey);
}

/// <summary>.lnk files in the user's SendTo folder. Real: <c>ShellLinkStore</c> (IShellLinkW); tests: a fake.</summary>
public interface IShortcutStore
{
    /// <summary>%APPDATA%\Microsoft\Windows\SendTo.</summary>
    string SendToFolder { get; }
    bool Exists(string path);
    void Create(string path, string target, string arguments, string description);
    void Delete(string path);
}

public enum ExplorerMenuState
{
    Off,
    /// <summary>Registered for this exe.</summary>
    On,
    /// <summary>Registered, but for another NitroDrop.exe (portable folder moved, or a second copy).</summary>
    OtherExe,
}

/// <summary>
/// Portable (unpackaged) Explorer integration, per user, no admin:
/// <list type="bullet">
/// <item><c>HKCU\Software\Classes\*\shell\NitroDrop</c> and <c>…\Directory\shell\NitroDrop</c>: MUIVerb
///   <see cref="UiStrings.ExplorerMenuVerb"/> (UI language at registration time), Icon, <c>MultiSelectModel=Player</c> (no 15-item limit; Explorer then runs the
///   command once per selected item, <see cref="SendBatcher"/> merges them), <c>command</c> =
///   <c>"…\NitroDrop.exe" --send "%1"</c>. On Windows 11 it is under "Show more options" (classic menu);
///   only packaged apps get into the new menu (IExplorerCommand, see app/NitroDrop.ShellExt).</item>
/// <item><c>%APPDATA%\Microsoft\Windows\SendTo\NitroDrop.lnk</c> → <c>NitroDrop.exe --send</c>: Send to menu,
///   all selected items in one command line.</item>
/// </list>
/// WPF-free (linked into AirDrop.Tests).
/// </summary>
public static class ExplorerMenu
{
    public const string VerbKeyName = "NitroDrop";
    public static readonly string[] ShellKeys =
    {
        @"Software\Classes\*\shell\" + VerbKeyName,
        @"Software\Classes\Directory\shell\" + VerbKeyName,
    };

    /// <summary><c>"exe" --send "%1"</c>. The exe is always quoted (paths with spaces; it cannot end in a backslash).</summary>
    public static string BuildCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || exePath.Contains('"'))
            throw new ArgumentException("invalid exe path", nameof(exePath));
        return $"\"{exePath}\" {LaunchArgs.Send} \"%1\"";
    }

    public static string BuildIcon(string exePath) => exePath + ",0";

    public static string SendToShortcutPath(IShortcutStore shortcuts) =>
        Path.Combine(shortcuts.SendToFolder, UiStrings.SendToShortcutName + ".lnk");

    /// <summary>The exe in a command written by <see cref="BuildCommand"/> (first quoted token), or null.</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.TrimStart();
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c;
    }

    public static ExplorerMenuState GetState(IUserRegistry reg, string exePath)
    {
        var cmd = reg.GetString(ShellKeys[0] + @"\command", null);
        if (cmd is null) return ExplorerMenuState.Off;
        return string.Equals(cmd, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase)
            ? ExplorerMenuState.On : ExplorerMenuState.OtherExe;
    }

    public static void Register(IUserRegistry reg, IShortcutStore shortcuts, string exePath)
    {
        var command = BuildCommand(exePath);
        foreach (var key in ShellKeys)
        {
            reg.SetString(key, "MUIVerb", UiStrings.ExplorerMenuVerb);
            reg.SetString(key, "Icon", BuildIcon(exePath));
            reg.SetString(key, "MultiSelectModel", "Player");
            reg.SetString(key + @"\command", null, command);
        }
        var lnk = SendToShortcutPath(shortcuts);
        if (shortcuts.Exists(lnk)) shortcuts.Delete(lnk);
        shortcuts.Create(lnk, exePath, LaunchArgs.Send, UiStrings.SendToShortcutDescription);
    }

    public static void Unregister(IUserRegistry reg, IShortcutStore shortcuts)
    {
        foreach (var key in ShellKeys) reg.DeleteTree(key);
        var lnk = SendToShortcutPath(shortcuts);
        if (shortcuts.Exists(lnk)) shortcuts.Delete(lnk);
    }
}

/// <summary>Ayarlar → Bu bilgisayar → File Explorer context menu switch and the startup check. WPF-free.</summary>
public sealed class ExplorerMenuService(IUserRegistry registry, IShortcutStore shortcuts, string exePath, bool packaged,
                                        Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    /// <summary>MSIX: the manifest's IExplorerCommand, present while the package is installed; nothing to toggle.</summary>
    public bool ManagedByWindows => packaged;

    public bool IsEnabled => packaged || ExplorerMenu.GetState(registry, exePath) == ExplorerMenuState.On;

    /// <exception cref="Exception">Registry / shortcut write failed (the caller shows <see cref="UiStrings.ExplorerMenuFailed"/>).</exception>
    public void SetEnabled(bool enabled)
    {
        if (packaged) return;
        if (enabled) ExplorerMenu.Register(registry, shortcuts, exePath);
        else ExplorerMenu.Unregister(registry, shortcuts);
    }

    /// <summary>
    /// After a UI language switch: rewrites the classic verb (MUIVerb) and the SendTo description in the new language when
    /// the entry is registered for this exe. Packaged builds have nothing to rewrite (the native command reads the
    /// language from HKCU itself). Returns true when something was written.
    /// </summary>
    /// <exception cref="Exception">Registry / shortcut write failed.</exception>
    public bool RefreshLanguage()
    {
        if (packaged || ExplorerMenu.GetState(registry, exePath) != ExplorerMenuState.On) return false;
        ExplorerMenu.Register(registry, shortcuts, exePath);
        return true;
    }

    /// <summary>
    /// Once per start. Packaged: removes a classic entry a portable build left behind (it would show twice, like the
    /// HKCU Run value <c>StartupRegistration</c> removes). Portable: when the entry points at an exe that no longer
    /// exists (folder moved), re-points it here. Another existing copy's entry is left alone.
    /// Returns true when something was written. Never throws.
    /// </summary>
    public bool StartupCheck()
    {
        try
        {
            var state = ExplorerMenu.GetState(registry, exePath);
            if (packaged)
            {
                if (state == ExplorerMenuState.Off) return false;
                ExplorerMenu.Unregister(registry, shortcuts);
                return true;
            }
            if (state == ExplorerMenuState.On)
            {
                // Registered in another UI language (changed while we were not running): follow the current one.
                if (registry.GetString(ExplorerMenu.ShellKeys[0], "MUIVerb") == UiStrings.ExplorerMenuVerb) return false;
                ExplorerMenu.Register(registry, shortcuts, exePath);
                return true;
            }
            if (state != ExplorerMenuState.OtherExe) return false;
            var old = ExplorerMenu.ExeFromCommand(registry.GetString(ExplorerMenu.ShellKeys[0] + @"\command", null));
            if (old is not null && _fileExists(old)) return false;
            ExplorerMenu.Register(registry, shortcuts, exePath);
            return true;
        }
        catch (Exception) { return false; }
    }
}
