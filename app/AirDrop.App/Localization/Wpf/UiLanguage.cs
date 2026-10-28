using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using AirDrop.App.Services;

namespace AirDrop.App.Localization;

/// <summary>
/// App-side glue for <see cref="Loc"/>: applies the saved language at start, switches it live from Ayarlar → Dil,
/// keeps thread cultures and open windows' <see cref="FrameworkElement.Language"/> in step (WPF formats bound numbers
/// and dates with it), and publishes the choice to HKCU for the native Explorer command (NitroDrop.ShellExt), which
/// cannot read managed resources.
/// </summary>
public static class UiLanguage
{
    /// <summary>HKCU key/value read by app/NitroDrop.ShellExt/ShellExt.cpp (GetTitle).</summary>
    public const string RegistryKey = @"Software\Nitrobyte\NitroDrop";
    public const string RegistryValue = "UiLanguage";

    private static bool _metadataSet;

    /// <summary>Called once at startup (also by the elevated helper, so its messages match the app).</summary>
    public static void Initialize(string? saved)
    {
        Loc.LanguageChanged += (_, _) => ApplyToProcess();
        Loc.SetLanguage(saved);
        ApplyToProcess();
        if (!_metadataSet)
        {
            // Default for every element created later; open windows are updated in ApplyToProcess.
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag)));
            _metadataSet = true;
        }
    }

    /// <summary>Ayarlar → Dil: null/empty = follow Windows. Saves the setting.</summary>
    public static void Change(string? code, AppSettings? settings)
    {
        if (settings is not null)
        {
            settings.Language = string.IsNullOrEmpty(code) ? null : code;
            try { settings.Save(); } catch (Exception) { /* settings dir unavailable */ }
        }
        Loc.SetLanguage(code);
    }

    private static void ApplyToProcess()
    {
        var c = Loc.Culture;
        CultureInfo.DefaultThreadCurrentCulture = c;
        CultureInfo.DefaultThreadCurrentUICulture = c;
        Thread.CurrentThread.CurrentCulture = c;
        Thread.CurrentThread.CurrentUICulture = c;
        if (Application.Current is { } app)
        {
            var lang = XmlLanguage.GetLanguage(c.IetfLanguageTag);
            foreach (Window w in app.Windows) w.Language = lang;
        }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RegistryKey);
            k?.SetValue(RegistryValue, Loc.Language, RegistryValueKind.String);
        }
        catch (Exception) { /* HKCU unavailable: the shell extension falls back to the Windows UI language */ }
    }
}
