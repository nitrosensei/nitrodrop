using System;
using AirDrop.App.Services;
using Microsoft.Win32;

namespace AirDrop.App.Setup;

/// <summary>
/// One-shot HKCU RunOnce entry that reopens the wizard after a reboot we triggered
/// (<c>NitroDrop.exe --setup-resume</c>). Windows deletes RunOnce values before running them.
/// </summary>
public static class ResumeRegistration
{
    public const string ResumeFlag = "--setup-resume";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ValueName = "Nitrobyte.NitroDrop.Setup";

    /// <summary>MSIX: the app execution alias (stable across updates, keeps package identity); portable: this exe.</summary>
    public static string CommandLine => $"\"{PackagedApp.LauncherPath}\" {ResumeFlag}";

    public static void Register()
    {
        if (ElevatedHelper.DryRun)
        {
            SetupLog.Write($@"[dry-run] HKCU\{RunOnceKey}\{ValueName} = {CommandLine} yazılacaktı");
            return;
        }
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunOnceKey);
            k.SetValue(ValueName, CommandLine);
            SetupLog.Write("yeniden başlatma sonrası devam kaydı eklendi (RunOnce)");
        }
        catch (Exception ex) { SetupLog.Write($"RunOnce yazılamadı: {ex.Message}"); }
    }

    public static void Unregister()
    {
        if (ElevatedHelper.DryRun) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception) { }
    }
}
