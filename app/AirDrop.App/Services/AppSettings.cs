using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AirDrop.App.Services;

/// <summary>Per-device preferences, keyed by PeerInfo.StableKey.</summary>
public sealed class DevicePrefs
{
    public string? PhotoPath { get; set; }
    public bool Trusted { get; set; }
    public string? DisplayName { get; set; }
    public string? LastSeenName { get; set; }
    public DeviceKind Kind { get; set; }
}

public sealed class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "airdrop-win");

    private static string FilePath => Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string DeviceName { get; set; } = Environment.MachineName;
    public string ReceiveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool PreferCompatibleFormats { get; set; } = true;
    public bool OpenLinksAutomatically { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>First-run intro ("İlk kullanım") was finished or skipped.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Main window bounds (DIPs), restored and clamped to the work area on start.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>Incoming text / links / single images are also put on the Windows clipboard.</summary>
    public bool CopyIncomingToClipboard { get; set; } = true;

    /// <summary>Device that Ctrl+Alt+A sends the clipboard to (last used).</summary>
    public string? ClipboardTargetKey { get; set; }
    public string DefaultRegion { get; set; } = "TR";

    /// <summary>Senkron: show my open browser tabs to paired PCs (via the Chrome/Edge extension).</summary>
    public bool SyncShareTabs { get; set; } = true;
    /// <summary>Senkron: show my running apps to paired PCs.</summary>
    public bool SyncShareApps { get; set; } = true;
    /// <summary>Executable file names never shared (in addition to the built-in list).</summary>
    public List<string> SyncExcludedApps { get; set; } = new();

    public Dictionary<string, DevicePrefs> Devices { get; set; } = new();

    public DevicePrefs For(string stableKey)
    {
        if (!Devices.TryGetValue(stableKey, out var p))
            Devices[stableKey] = p = new DevicePrefs();
        return p;
    }

    public ServiceOptions ToServiceOptions() => new()
    {
        DeviceName = DeviceName,
        ReceiveFolder = ReceiveFolder,
        PreferCompatibleFormats = PreferCompatibleFormats,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* corrupt file → defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Copies a user-picked photo into our data dir so it survives the original being moved.</summary>
    public static string ImportPhoto(string source)
    {
        var dir = Path.Combine(DataDir, "avatars");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, dest);
        return dest;
    }
}
