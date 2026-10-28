using AirDrop.App.Localization;

namespace AirDrop.App.Services;

/// <summary>
/// User-visible strings of the Explorer integration, in the current UI language (Localization/Settings*.resx,
/// keys Explorer_*). WPF-free (linked into AirDrop.Tests).
/// The native IExplorerCommand (app/NitroDrop.ShellExt/ShellExt.cpp, GetTitle) carries its own six-language copy of
/// <see cref="ExplorerMenuVerb"/> because it cannot read managed resources; change both together.
/// </summary>
public static class UiStrings
{
    /// <summary>Explorer context menu entry (classic HKCU verb and the MSIX IExplorerCommand).</summary>
    public static string ExplorerMenuVerb => SettingsStrings.Explorer_MenuVerb;

    /// <summary>File name of the SendTo shortcut: shows as "NitroDrop" in Explorer → Send to (brand, never translated).</summary>
    public const string SendToShortcutName = "NitroDrop";

    public static string SendToShortcutDescription => SettingsStrings.Explorer_SendToDescription;

    /// <summary>Ayarlar → Bu bilgisayar row.</summary>
    public static string ExplorerMenuSetting => SettingsStrings.Explorer_Setting;

    public static string ExplorerMenuCaptionPortable => SettingsStrings.Explorer_CaptionPortable;

    public static string ExplorerMenuCaptionPackaged => SettingsStrings.Explorer_CaptionPackaged;

    /// <summary>"Couldn't add the File Explorer menu: {error}".</summary>
    public static string ExplorerMenuFailed(string error) => SettingsStrings.Explorer_Failed(error);
}
