using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AirDrop.App.Setup;

public interface ISystemProbe
{
    /// <summary>Read-only detection without admin. <paramref name="includeBitLocker"/> uses the shell (slower, STA thread).</summary>
    SystemSnapshot Read(bool includeBitLocker = true);
}

/// <summary>Real detection: registry, cfgmgr32, code integrity info. Never changes anything.</summary>
public sealed class SystemProbe : ISystemProbe
{
    public SystemSnapshot Read(bool includeBitLocker = true)
    {
        var s = new SystemSnapshot
        {
            Device = FindDevice(),
            SecureBootOn = ReadSecureBoot(),
            TestSigningActive = ReadTestSigning(),
            Machine = ReadMachine(),
            BitLocker = includeBitLocker ? ReadBitLockerQuick(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") : BitLockerQuickState.Unknown,
        };
        return s;
    }

    /// <summary>Supported card via HKLM\SYSTEM\CurrentControlSet\Enum\PCI; present = CM_Locate_DevNode succeeds.</summary>
    public static DeviceInstance? FindDevice()
    {
        try
        {
            using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci is null) return null;
            foreach (var keyName in pci.GetSubKeyNames())
            {
                var card = SupportedDevices.MatchEnumKey(keyName);
                if (card is null) continue;
                using var devKey = pci.OpenSubKey(keyName);
                if (devKey is null) continue;
                foreach (var inst in devKey.GetSubKeyNames())
                {
                    using var ik = devKey.OpenSubKey(inst);
                    string instanceId = $@"PCI\{keyName}\{inst}";
                    var d = new DeviceInstance(card, instanceId, ik?.GetValue("Service") as string, IsPresent(instanceId));
                    if (d.Present) return d;
                }
            }
        }
        catch (Exception ex) { SetupLog.Write($"PCI aygıtları okunamadı: {ex.Message}"); }
        return null;   // only present hardware counts (Enum also remembers removed cards)
    }

    public static bool? ReadSecureBoot()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return k?.GetValue("UEFISecureBootEnabled") is int v ? v != 0 : null;
        }
        catch (Exception) { return null; }
    }

    public static bool ReadTestSigning()
    {
        try
        {
            var info = new SYSTEM_CODEINTEGRITY_INFORMATION { Length = (uint)Marshal.SizeOf<SYSTEM_CODEINTEGRITY_INFORMATION>() };
            if (NtQuerySystemInformation(SystemCodeIntegrityInformation, ref info, info.Length, out _) == 0)
                return SetupFlow.TestSignFromCodeIntegrity(info.CodeIntegrityOptions);
        }
        catch (Exception) { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return SetupFlow.TestSignFromStartOptions(k?.GetValue("SystemStartOptions") as string);
        }
        catch (Exception) { return false; }
    }

    public static MachineInfo ReadMachine()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (k is null) return MachineInfo.Unknown;
            string? S(string n) => (k.GetValue(n) as string)?.Trim();
            return new MachineInfo(S("SystemManufacturer"), S("SystemProductName"), S("SystemFamily"), S("BIOSVendor"));
        }
        catch (Exception) { return MachineInfo.Unknown; }
    }

    /// <summary>Shell property System.Volume.BitLockerProtection — works without admin (on an STA thread).</summary>
    public static BitLockerQuickState ReadBitLockerQuick(string drive)
    {
        BitLockerQuickState result = BitLockerQuickState.Unknown;
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return;
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic? folder = shell.NameSpace(drive.TrimEnd('\\') + "\\");
                object? v = folder?.Self.ExtendedProperty("System.Volume.BitLockerProtection");
                result = SetupFlow.BitLockerFromShell(v);
            }
            catch (Exception ex) { SetupLog.Write($"BitLocker hızlı durum okunamadı: {ex.Message}"); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(10));
        return result;
    }

    /// <summary>Problem code of a present device (0 = working), or null.</summary>
    public static uint? ProblemCode(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var inst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != 0) return null;
        return CM_Get_DevNode_Status(out _, out var problem, inst, 0) == 0 ? problem : null;
    }

    public static bool IsPresent(string instanceId) => CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == 0;

    private const int SystemCodeIntegrityInformation = 103;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_CODEINTEGRITY_INFORMATION { public uint Length; public uint CodeIntegrityOptions; }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_CODEINTEGRITY_INFORMATION info, uint length, out uint returnLength);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);
}
