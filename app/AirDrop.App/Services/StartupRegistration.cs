using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace AirDrop.App.Services;

/// <summary>
/// "Start with Windows" (no admin needed). Portable: HKCU Run → <c>NitroDrop.exe --background</c>.
/// MSIX: the manifest's StartupTask (HKCU Run of a WindowsApps path would point at an old version after an update
/// and start without package identity); the task passes no arguments, so App detects it via
/// <see cref="PackagedApp.LaunchedByStartupTask"/>.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nitrobyte.NitroDrop";

    /// <exception cref="InvalidOperationException">The StartupTask could not be enabled (disabled by the user in
    /// Task Manager → Startup apps, or by policy); the caller leaves the setting unchanged.</exception>
    public static void Set(bool enabled)
    {
        if (PackagedApp.IsPackaged)
        {
            SetStartupTask(enabled);
            DeleteRunValue();   // a portable build may have left one behind → would start twice
            return;
        }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static void SetStartupTask(bool enabled)
    {
        // Thread pool (MTA), not the UI thread: blocking the dispatcher on a WinRT async op can deadlock.
        var state = Task.Run(async () =>
        {
            var task = await StartupTask.GetAsync(PackagedApp.StartupTaskId);
            if (enabled) return await task.RequestEnableAsync();
            task.Disable();
            return task.State;
        }).GetAwaiter().GetResult();

        bool on = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        if (on != enabled)
            throw new InvalidOperationException($"StartupTask {PackagedApp.StartupTaskId}: {state}");
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception) { }
    }
}
