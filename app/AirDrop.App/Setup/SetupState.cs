using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AirDrop.App.Setup;

/// <summary>
/// Wizard progress persisted in %LOCALAPPDATA%\airdrop-win\setup.json so the wizard resumes after the
/// BIOS and test-mode reboots. Only facts the system can't tell us are stored here; everything else
/// (Secure Boot, test mode, driver bound) is re-detected on every open.
/// </summary>
public sealed class SetupState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public int Version { get; set; } = 1;

    public bool ConsentGiven { get; set; }
    public DateTime? ConsentAtUtc { get; set; }

    /// <summary>User confirmed the BitLocker recovery key is backed up.</summary>
    public bool BitLockerBackupConfirmed { get; set; }

    /// <summary>Suspend BitLocker for one reboot before each reboot we trigger (checkbox, on by default).</summary>
    public bool SuspendBitLockerOnReboot { get; set; } = true;

    /// <summary>We ran manage-bde -protectors -disable at least once (rollback resumes it).</summary>
    public bool BitLockerSuspendedByUs { get; set; }

    /// <summary>We sent the user to firmware setup to turn Secure Boot off.</summary>
    public bool FirmwareRebootRequested { get; set; }

    /// <summary>bcdedit testsigning on succeeded; active after the next reboot.</summary>
    public bool TestSigningEnabledByUs { get; set; }
    public DateTime? TestSigningSetUtc { get; set; }

    public bool FirmwareLicenseAccepted { get; set; }

    /// <summary>Per-install certificate (public part in LocalMachine\Root + TrustedPublisher).</summary>
    public string? CertThumbprint { get; set; }

    /// <summary>oemNN.inf names pnputil published for our package.</summary>
    public List<string> PublishedInfs { get; set; } = new();

    public bool DriverInstalled { get; set; }
    public DateTime? InstalledUtc { get; set; }

    /// <summary>Anything the rollback should undo.</summary>
    public bool HasChanges =>
        DriverInstalled || CertThumbprint is not null || TestSigningEnabledByUs || BitLockerSuspendedByUs || PublishedInfs.Count > 0;

    public static SetupState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<SetupState>(File.ReadAllText(path)) ?? new SetupState();
        }
        catch (Exception ex)
        {
            SetupLog.Write($"couldn't read setup.json, using defaults: {ex.Message}");
        }
        return new SetupState();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>After a successful rollback: keep nothing but the user's preferences.</summary>
    public void ResetAfterRollback()
    {
        BitLockerSuspendedByUs = false;
        FirmwareRebootRequested = false;
        TestSigningEnabledByUs = false;
        TestSigningSetUtc = null;
        CertThumbprint = null;
        PublishedInfs.Clear();
        DriverInstalled = false;
        InstalledUtc = null;
    }
}
