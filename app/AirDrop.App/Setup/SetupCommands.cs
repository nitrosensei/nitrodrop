using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AirDrop.App.Localization;

namespace AirDrop.App.Setup;

/// <summary>An external command the setup may run. Built here, executed by an <see cref="ICommandRunner"/>.</summary>
public sealed record CommandSpec(string FileName, IReadOnlyList<string> Args, string Purpose)
{
    public string Arguments => CommandLine.Join(Args);
    public override string ToString() => $"{Path.GetFileName(FileName)} {Arguments}";
}

/// <summary>Windows command-line quoting (the rules CommandLineToArgvW / the CRT use to split).</summary>
public static class CommandLine
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
}

public enum BcdOutcome { Ok, BlockedBySecureBoot, AccessDenied, Failed }

public enum PnpOutcome { Ok, RebootRequired, NothingToDo, Failed }

/// <summary>Builders for every system-changing command the wizard can run, plus result interpretation. WPF-free.</summary>
public static class SetupCommands
{
    private static readonly Regex OemInf = new(@"^oem\d{1,5}\.inf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DriveLetter = new(@"^[A-Za-z]:$", RegexOptions.CultureInvariant);

    public static string SystemDir { get; set; } = Environment.SystemDirectory;

    private static string Sys(string exe) => Path.Combine(SystemDir, exe);

    public static CommandSpec TestSigning(bool on) =>
        new(Sys("bcdedit.exe"), new[] { "/set", "{current}", "testsigning", on ? "on" : "off" },
            on ? "Turn test signing on" : "Turn test signing off");

    /// <summary>Restart straight into UEFI firmware setup (needs admin and a UEFI machine).</summary>
    public static CommandSpec RebootToFirmware(int delaySeconds = 5) =>
        new(Sys("shutdown.exe"), new[] { "/r", "/fw", "/t", delaySeconds.ToString() }, "Restart into BIOS/UEFI setup");

    public static CommandSpec Reboot(int delaySeconds = 5) =>
        new(Sys("shutdown.exe"), new[] { "/r", "/t", delaySeconds.ToString() }, "Restart");

    public static CommandSpec SuspendBitLocker(string drive, int rebootCount = 1) =>
        new(Sys("manage-bde.exe"), new[] { "-protectors", "-disable", CheckDrive(drive), "-RebootCount", rebootCount.ToString() },
            $"Suspend BitLocker for {rebootCount} restart(s)");

    public static CommandSpec ResumeBitLocker(string drive) =>
        new(Sys("manage-bde.exe"), new[] { "-protectors", "-enable", CheckDrive(drive) }, "Resume BitLocker protection");

    /// <summary>Stage the package in the driver store and install it where it is the best match.</summary>
    public static CommandSpec AddDriver(string infPath) =>
        new(Sys("pnputil.exe"), new[] { "/add-driver", infPath, "/install" }, "Add the driver package");

    public static CommandSpec DeleteDriver(string publishedInf)
    {
        if (!OemInf.IsMatch(publishedInf)) throw new ArgumentException($"invalid published INF name: {publishedInf}", nameof(publishedInf));
        return new(Sys("pnputil.exe"), new[] { "/delete-driver", publishedInf, "/uninstall", "/force" }, $"Remove driver {publishedInf}");
    }

    public static CommandSpec ScanDevices() =>
        new(Sys("pnputil.exe"), new[] { "/scan-devices" }, "Scan for hardware changes (lets the Intel driver bind again)");

    /// <summary>
    /// BitLocker details via WMI (Win32_EncryptableVolume, admin only) as locale-independent JSON.
    /// Includes the numerical recovery passwords so the UI can offer "save to file"; never logged.
    /// </summary>
    public static CommandSpec BitLockerQuery(string drive)
    {
        string d = CheckDrive(drive);
        string script = string.Join("\n",
            "$ErrorActionPreference='Stop'",
            $"$v=Get-CimInstance -Namespace root/cimv2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='{d}'\"",
            "if(-not $v){ '{\"present\":false}'; exit 0 }",
            "$p=Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus",
            "$c=Invoke-CimMethod -InputObject $v -MethodName GetConversionStatus",
            "$k=Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectors -Arguments @{KeyProtectorType=[uint32]3}",
            "$keys=@()",
            "foreach($id in @($k.VolumeKeyProtectorID)){ if($id){ $n=Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectorNumericalPassword -Arguments @{VolumeKeyProtectorID=$id}; $keys+=[pscustomobject]@{id=$id;password=$n.NumericalPassword} } }",
            "[pscustomobject]@{present=$true;protectionStatus=[int]$p.ProtectionStatus;conversionStatus=[int]$c.ConversionStatus;percent=[double]$c.EncryptionPercentage;recovery=@($keys)} | ConvertTo-Json -Compress -Depth 4");
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new(Path.Combine(SystemDir, @"WindowsPowerShell\v1.0\powershell.exe"),
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            "Read BitLocker status");
    }

    private static string CheckDrive(string drive)
    {
        if (!DriveLetter.IsMatch(drive)) throw new ArgumentException($"invalid drive: {drive}", nameof(drive));
        return drive.ToUpperInvariant();
    }

    /// <summary>
    /// bcdedit refuses testsigning while Secure Boot is on ("The value is protected by Secure Boot policy…",
    /// localized, exit code 1). We look for the phrase in English/Turkish and fall back to the detected state.
    /// </summary>
    public static BcdOutcome ClassifyBcdedit(int exitCode, string output, bool? secureBootOn)
    {
        if (exitCode == 0) return BcdOutcome.Ok;
        var o = output ?? "";
        if (o.Contains("Secure Boot", StringComparison.OrdinalIgnoreCase)
            || o.Contains("Güvenli Önyükleme", StringComparison.OrdinalIgnoreCase)   // l10n-allow: tool output pattern
            || o.Contains("güvenli önyükleme", StringComparison.OrdinalIgnoreCase)   // l10n-allow: tool output pattern
            || secureBootOn == true)
            return BcdOutcome.BlockedBySecureBoot;
        if (o.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || o.Contains("Erişim engellendi", StringComparison.OrdinalIgnoreCase)   // l10n-allow: tool output pattern
            || exitCode == 5)
            return BcdOutcome.AccessDenied;
        return BcdOutcome.Failed;
    }

    public static string Describe(BcdOutcome o) => o switch
    {
        BcdOutcome.Ok => SetupStrings.Helper_Bcd_Ok,
        BcdOutcome.BlockedBySecureBoot => SetupStrings.Helper_Bcd_SecureBoot,
        BcdOutcome.AccessDenied => SetupStrings.Helper_Bcd_AccessDenied,
        _ => SetupStrings.Helper_Bcd_Failed,
    };

    /// <summary>pnputil: 0 ok, 3010 reboot required, 259 (ERROR_NO_MORE_ITEMS) = nothing was installed/updated.</summary>
    public static PnpOutcome ClassifyPnputil(int exitCode) => exitCode switch
    {
        0 => PnpOutcome.Ok,
        3010 => PnpOutcome.RebootRequired,
        259 => PnpOutcome.NothingToDo,
        _ => PnpOutcome.Failed,
    };
}
