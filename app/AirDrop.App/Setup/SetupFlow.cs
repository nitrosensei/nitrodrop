using System;
using System.Linq;

namespace AirDrop.App.Setup;

public enum SetupStep { Compatibility, Consent, BitLocker, SecureBoot, TestSigning, Install, Done }

public enum BitLockerQuickState { Unknown, NotEncrypted, Off, On, Suspended, InProgress }

/// <summary>The supported card as found on this machine.</summary>
public sealed record DeviceInstance(SupportedCard Card, string InstanceId, string? Service, bool Present);

/// <summary>Firmware/SMBIOS strings (HKLM\HARDWARE\DESCRIPTION\System\BIOS, readable without admin).</summary>
public sealed record MachineInfo(string? Manufacturer, string? Product, string? Family, string? BiosVendor)
{
    public string Describe()
    {
        var model = string.IsNullOrWhiteSpace(Family) ? Product : Family;
        if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(Manufacturer)
            && model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase))
            return model.Trim();   // "Acer Nitro V 15", not "Acer Acer Nitro V 15"
        var name = string.Join(" ", new[] { Manufacturer, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(name) ? "Bu bilgisayar" : name;
    }

    public static MachineInfo Unknown { get; } = new(null, null, null, null);
}

/// <summary>Everything the wizard detects (read-only, no admin).</summary>
public sealed record SystemSnapshot
{
    public DeviceInstance? Device { get; init; }
    /// <summary>null = not a UEFI system or state unknown.</summary>
    public bool? SecureBootOn { get; init; }
    /// <summary>Test signing active in the running boot (code integrity options).</summary>
    public bool TestSigningActive { get; init; }
    public BitLockerQuickState BitLocker { get; init; }
    public MachineInfo Machine { get; init; } = MachineInfo.Unknown;

    public bool OurDriverBound =>
        Device is { Present: true } d && string.Equals(d.Service, DriverPackage.ServiceName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which step comes next â€” pure, unit-tested.</summary>
public static class SetupFlow
{
    /// <summary>Install steps in display order (the progress dots).</summary>
    public static readonly SetupStep[] Steps =
    {
        SetupStep.Compatibility, SetupStep.Consent, SetupStep.BitLocker,
        SetupStep.SecureBoot, SetupStep.TestSigning, SetupStep.Install,
    };

    /// <summary>BitLocker matters while a reboot that changes boot measurements is still ahead.</summary>
    public static bool BitLockerRelevant(SystemSnapshot s) =>
        s.SecureBootOn == true || !s.TestSigningActive;

    public static bool IsSatisfied(SetupStep step, SystemSnapshot s, SetupState st) => step switch
    {
        SetupStep.Compatibility => s.Device is not null,
        SetupStep.Consent => st.ConsentGiven,
        SetupStep.BitLocker => !BitLockerRelevant(s) || st.BitLockerBackupConfirmed
                               || s.BitLocker is BitLockerQuickState.NotEncrypted or BitLockerQuickState.Off,
        // null = no UEFI Secure Boot state (legacy/CSM boot): nothing to turn off; bcdedit will tell otherwise.
        SetupStep.SecureBoot => s.SecureBootOn != true,
        SetupStep.TestSigning => s.TestSigningActive,
        SetupStep.Install => s.OurDriverBound,
        SetupStep.Done => s.OurDriverBound,
        _ => false,
    };

    /// <summary>First step that still needs the user. Compatibility blocks everything.</summary>
    public static SetupStep Next(SystemSnapshot s, SetupState st)
    {
        if (s.Device is null) return SetupStep.Compatibility;
        if (s.OurDriverBound) return SetupStep.Done;
        foreach (var step in Steps)
            if (!IsSatisfied(step, s, st)) return step;
        return SetupStep.Done;
    }

    /// <summary>Where an opened wizard starts: welcome page on a fresh run, else resume.</summary>
    public static SetupStep Start(SystemSnapshot s, SetupState st) =>
        !st.ConsentGiven && !s.OurDriverBound ? SetupStep.Compatibility : Next(s, st);

    /// <summary>Test mode was switched on but the machine hasn't rebooted yet.</summary>
    public static bool TestSigningPendingReboot(SystemSnapshot s, SetupState st) =>
        st.TestSigningEnabledByUs && !s.TestSigningActive;

    /// <summary>Code integrity options (NtQuerySystemInformation class 103): CODEINTEGRITY_OPTION_TESTSIGN = 0x2.</summary>
    public static bool TestSignFromCodeIntegrity(uint options) => (options & 0x2) != 0;

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\SystemStartOptions, e.g. " NOEXECUTE=OPTIN  TESTSIGNING".</summary>
    public static bool TestSignFromStartOptions(string? options)
    {
        if (string.IsNullOrEmpty(options)) return false;
        foreach (var tok in options.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            if (tok.Equals("TESTSIGNING", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Shell property System.Volume.BitLockerProtection (no admin).
    /// UNVERIFIED mapping (from observed values): 1 = on, 2 = off, 3 = encrypting, 4 = decrypting,
    /// 5 = suspended, 6 = on (locked); null/0 = not encrypted / not capable.
    /// </summary>
    public static BitLockerQuickState BitLockerFromShell(object? value) => value switch
    {
        null => BitLockerQuickState.NotEncrypted,
        int i => i switch
        {
            0 => BitLockerQuickState.NotEncrypted,
            1 or 6 => BitLockerQuickState.On,
            2 => BitLockerQuickState.Off,
            3 or 4 => BitLockerQuickState.InProgress,
            5 => BitLockerQuickState.Suspended,
            _ => BitLockerQuickState.Unknown,
        },
        uint u => BitLockerFromShell((int)u),
        long l => BitLockerFromShell((int)l),
        _ => BitLockerQuickState.Unknown,
    };
}
