namespace AirDrop.App.Services;

/// <summary>
/// User-visible strings for the Explorer integration ("NitroDrop ile gönder"), kept in one place for the coming
/// localization pass. WPF-free (linked into AirDrop.Tests).
/// The native IExplorerCommand (app/NitroDrop.ShellExt/ShellExt.cpp, GetTitle) carries its own copy of
/// <see cref="ExplorerMenuVerb"/> because it cannot read managed resources; change both together.
/// </summary>
public static class UiStrings
{
    /// <summary>Explorer context menu entry (classic HKCU verb and the MSIX IExplorerCommand).</summary>
    public const string ExplorerMenuVerb = "NitroDrop ile gönder";

    /// <summary>Name of the "Gönder" (SendTo) shortcut: shows as "NitroDrop" in Explorer → Gönder.</summary>
    public const string SendToShortcutName = "NitroDrop";

    public const string SendToShortcutDescription = "Seçilenleri NitroDrop ile gönder";

    /// <summary>Ayarlar → Bu bilgisayar row.</summary>
    public const string ExplorerMenuSetting = "Gezgin sağ tık menüsü";

    public const string ExplorerMenuCaptionPortable =
        "Dosya ve klasörlere sağ tıklayınca “NitroDrop ile gönder” (Windows 11'de “Diğer seçenekleri göster” altında) " +
        "ve Gönder menüsünde NitroDrop.";

    public const string ExplorerMenuCaptionPackaged =
        "Windows tarafından yönetiliyor: NitroDrop kurulu olduğu sürece sağ tık menüsünde “NitroDrop ile gönder” var.";

    public const string ExplorerMenuFailed = "Gezgin menüsü kaydedilemedi: ";
}
