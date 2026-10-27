using System;
using Microsoft.Win32;

namespace AirDrop.App.Services;

/// <summary>"Start with Windows" via HKCU Run (no admin needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nitrobyte.NitroDrop";

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
