using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace AirDrop.App.Setup;

/// <summary>
/// What the elevated helper actually does. Every system change in the product lives in this class, and every
/// one of them honours dry-run (logged, not executed). Runs in-process only in dry-run mode.
/// </summary>
public sealed class HelperActions(ICommandRunner runner)
{
    private bool Dry => runner.IsDryRun;

    public HelperResult Execute(HelperRequest r)
    {
        SetupLog.Write($"eylem: {r.Action}{(Dry ? " (dry-run)" : "")}");
        try
        {
            var result = r.Action switch
            {
                HelperAction.BitLockerStatus => BitLockerStatus(r),
                HelperAction.RebootToFirmware => RebootToFirmware(r),
                HelperAction.TestSigningOn => TestSigningOn(r),
                HelperAction.InstallDriver => InstallDriver(r),
                HelperAction.UninstallDriver => UninstallDriver(r),
                _ => HelperResult.Fail("bilinmeyen eylem"),
            };
            result.DryRun = Dry;
            if (Dry && !result.Message.StartsWith("[dry-run]", StringComparison.Ordinal))
                result.Message = "[dry-run] " + result.Message;
            SetupLog.Write($"eylem sonucu: {r.Action} ok={result.Ok} {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"eylem hatası: {r.Action}: {ex}");
            return HelperResult.Fail(ex.Message);
        }
    }

    // ---------------- BitLocker ----------------

    private HelperResult BitLockerStatus(HelperRequest r)
    {
        var res = runner.Run(SetupCommands.BitLockerQuery(r.Drive), logOutput: false);   // output holds recovery passwords
        if (res.DryRun) return new HelperResult { Ok = true, Message = "[dry-run] BitLocker sorgusu çalıştırılmadı." };
        if (res.ExitCode != 0) return HelperResult.Fail("BitLocker durumu okunamadı.", res.ExitCode);
        var json = res.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) ?? "{}";
        var info = BitLockerInfo.Parse(json);
        SetupLog.Write($"BitLocker: present={info.Present} protection={info.ProtectionStatus} conversion={info.ConversionStatus} " +
                       $"anahtar kimlikleri={string.Join(",", info.Recovery.Select(k => k.Id))}");
        return new HelperResult { Ok = true, BitLocker = info };
    }

    private bool SuspendBitLockerIfAsked(HelperRequest r)
    {
        if (!r.SuspendBitLocker) return false;
        var res = runner.Run(SetupCommands.SuspendBitLocker(r.Drive, 1));
        // manage-bde fails harmlessly when the volume isn't encrypted; the reboot still goes ahead.
        return res.ExitCode == 0;
    }

    // ---------------- reboots ----------------

    private HelperResult RebootToFirmware(HelperRequest r)
    {
        bool suspended = SuspendBitLockerIfAsked(r);
        var res = runner.Run(SetupCommands.RebootToFirmware(5));
        return new HelperResult
        {
            Ok = res.ExitCode == 0,
            ExitCode = res.ExitCode,
            BitLockerSuspended = suspended,
            Message = res.ExitCode == 0
                ? "Bilgisayar 5 saniye içinde BIOS ayarlarına yeniden başlıyor."
                : "BIOS'a yeniden başlatılamadı (bu bilgisayar UEFI ile açılmamış olabilir). Elle yeniden başlatıp açılışta BIOS tuşuna basın.",
        };
    }

    // ---------------- test signing ----------------

    private HelperResult TestSigningOn(HelperRequest r)
    {
        var res = runner.Run(SetupCommands.TestSigning(true));
        var outcome = SetupCommands.ClassifyBcdedit(res.ExitCode, res.Output, SystemProbe.ReadSecureBoot());
        bool suspended = outcome == BcdOutcome.Ok && SuspendBitLockerIfAsked(r);
        return new HelperResult
        {
            Ok = outcome == BcdOutcome.Ok,
            Bcd = outcome,
            ExitCode = res.ExitCode,
            RebootRequired = outcome == BcdOutcome.Ok,
            BitLockerSuspended = suspended,
            Message = SetupCommands.Describe(outcome),
        };
    }

    // ---------------- install ----------------

    private HelperResult InstallDriver(HelperRequest r)
    {
        if (string.IsNullOrEmpty(r.PackageDir) || string.IsNullOrEmpty(r.HardwareId))
            return HelperResult.Fail("paket klasörü ya da donanım kimliği eksik");
        if (SupportedDevices.All.All(c => !c.HardwareId.Equals(r.HardwareId, StringComparison.OrdinalIgnoreCase)))
            return HelperResult.Fail($"desteklenmeyen donanım kimliği: {r.HardwareId}");

        // 1. Copy into an admin-only folder, then check everything there (no swap between check and use).
        string dir = SetupPaths.ProtectedPackageDir;
        var missing = DriverPackage.Missing(r.PackageDir);
        if (missing.Count > 0) return HelperResult.Fail("Sürücü paketi eksik: " + string.Join(", ", missing));
        if (Dry) SetupLog.Write($"[dry-run] {r.PackageDir} → {dir} kopyalanacaktı (yalnızca Yöneticiler/SYSTEM yazabilir)");
        else CopyToProtectedDir(r.PackageDir, dir);
        string src = Dry ? r.PackageDir : dir;

        if (!FirmwareFetcher.Verify(Path.Combine(src, FirmwareSpec.Firmware.Name), FirmwareSpec.Firmware))
            return HelperResult.Fail("Firmware dosyası doğrulanamadı (SHA-256).");
        var notInCatalog = CodeSigner.VerifyPackageAgainstCatalog(src);
        if (notInCatalog.Count > 0)
            return HelperResult.Fail("Sürücü kataloğu paketle uyuşmuyor: " + string.Join(", ", notInCatalog));
        SetupLog.Write("paket kataloğa göre doğrulandı (inf, sys, ucode)");

        // 2. Per-install certificate; private key only in memory, gone after this block.
        string thumb;
        if (Dry)
        {
            SetupLog.Write("[dry-run] RSA-3072 kod imzalama sertifikası oluşturulacak, .sys ve .cat SignerSignEx ile imzalanacaktı");
            SetupLog.Write(@"[dry-run] açık sertifika LocalMachine\Root ve LocalMachine\TrustedPublisher'a eklenecekti");
            thumb = "DRYRUN";
        }
        else
        {
            using (var cert = CodeSigner.CreatePerInstallCertificate(Environment.MachineName))
            {
                thumb = cert.Thumbprint;
                SetupLog.Write($"sertifika oluşturuldu: {cert.Subject} {thumb}");
                foreach (var f in new[] { DriverPackage.SysName, DriverPackage.CatName })
                {
                    var path = Path.Combine(dir, f);
                    CodeSigner.SignFile(path, cert);
                    var signer = f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase) ? CodeSigner.GetCatalogSigner(path) : CodeSigner.GetPeSigner(path);
                    if (!string.Equals(signer, thumb, StringComparison.OrdinalIgnoreCase))
                        return HelperResult.Fail($"{f} imzası doğrulanamadı.");
                    SetupLog.Write($"imzalandı: {f}");
                }
                AddPublicCert(cert);
            }
            SetupLog.Write("özel anahtar bellekten silindi (hiç diske yazılmadı)");
            // The catalog was re-signed in place; make sure its hashes still match (they must).
            if (CodeSigner.VerifyPackageAgainstCatalog(dir).Count > 0) return HelperResult.Fail("İmzadan sonra katalog doğrulanamadı.");
        }

        // 3. Stage + install. pnputil installs only where we're the best match, which the WHQL Intel driver
        //    usually is, so we then force our INF onto exactly this hardware ID.
        string inf = Path.Combine(src, DriverPackage.InfName);
        var add = runner.Run(SetupCommands.AddDriver(inf));
        var addOutcome = SetupCommands.ClassifyPnputil(add.ExitCode);
        if (addOutcome == PnpOutcome.Failed)
            return new HelperResult { Ok = false, ExitCode = add.ExitCode, CertThumbprint = thumb, Message = "Sürücü paketi Windows'a eklenemedi. Ayrıntılar kurulum günlüğünde." };

        bool reboot = addOutcome == PnpOutcome.RebootRequired;
        if (Dry)
        {
            SetupLog.Write($"[dry-run] UpdateDriverForPlugAndPlayDevices(\"{r.HardwareId}\", \"{inf}\", INSTALLFLAG_FORCE) çağrılacaktı");
        }
        else if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, r.HardwareId, inf, INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE, out bool rb))
        {
            int err = Marshal.GetLastWin32Error();
            SetupLog.Write($"UpdateDriverForPlugAndPlayDevices başarısız: {err} (0x{err:X8})");
            return new HelperResult
            {
                Ok = false, ExitCode = err, CertThumbprint = thumb,
                PublishedInfs = DriverPackage.FindPublishedInfs(InfDir).ToList(),
                Message = "Sürücü karta atanamadı. \"Kaldır ve eski haline getir\" ile geri alabilirsiniz.",
            };
        }
        else reboot |= rb;

        // 4. Verify the card now runs on our service.
        var published = DriverPackage.FindPublishedInfs(InfDir).ToList();
        var dev = SystemProbe.FindDevice();
        bool bound = Dry || (dev?.Service?.Equals(DriverPackage.ServiceName, StringComparison.OrdinalIgnoreCase) ?? false);
        uint? problem = dev is null ? null : SystemProbe.ProblemCode(dev.InstanceId);
        SetupLog.Write($"doğrulama: servis={dev?.Service} sorun kodu={problem?.ToString() ?? "-"} yayımlanan={string.Join(",", published)}");
        return new HelperResult
        {
            Ok = bound,
            DriverBound = bound,
            RebootRequired = reboot,
            CertThumbprint = thumb,
            PublishedInfs = published,
            Message = !bound ? "Sürücü kuruldu ama kart hâlâ Intel sürücüsünde görünüyor. Yeniden başlatıp tekrar kontrol edin."
                : problem is > 0 ? $"Sürücü bağlandı ama aygıt sorun kodu {problem} bildiriyor (donanımda henüz denenmedi)."
                : reboot ? "Sürücü kuruldu. Etkin olması için yeniden başlatın."
                : "Sürücü kuruldu ve Wi-Fi kartına bağlandı.",
        };
    }

    private static string InfDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    private static void CopyToProtectedDir(string from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
        var di = Directory.CreateDirectory(to);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
        foreach (var f in DriverPackage.AllFiles) File.Copy(Path.Combine(from, f), Path.Combine(to, f), overwrite: true);
        SetupLog.Write($"paket korumalı klasöre kopyalandı: {to}");
    }

    private static void AddPublicCert(X509Certificate2 cert)
    {
        using var pub = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));   // no private key
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(pub);
            SetupLog.Write($@"sertifika eklendi: LocalMachine\{name}");
        }
    }

    // ---------------- rollback ----------------

    private HelperResult UninstallDriver(HelperRequest r)
    {
        var problems = new List<string>();

        // 1. Remove our package(s); the device falls back to the Intel inbox driver on rescan.
        var infs = DriverPackage.FindPublishedInfs(InfDir);
        if (infs.Count == 0) SetupLog.Write("kaldırılacak airdropwifi paketi yok");
        foreach (var oem in infs)
        {
            var res = runner.Run(SetupCommands.DeleteDriver(oem));
            if (SetupCommands.ClassifyPnputil(res.ExitCode) == PnpOutcome.Failed) problems.Add($"{oem} kaldırılamadı");
        }
        runner.Run(SetupCommands.ScanDevices());

        // 2. Certificates (by thumbprint and by our subject, in case setup.json was lost).
        int removed = RemoveOurCerts(r.CertThumbprint);
        SetupLog.Write($"kaldırılan sertifika: {removed}");

        // 3. Test mode off.
        if (r.DisableTestSigning)
        {
            var bcd = runner.Run(SetupCommands.TestSigning(false));
            if (bcd.ExitCode != 0) problems.Add("test modu kapatılamadı");
        }

        // 4. BitLocker back on.
        if (r.ResumeBitLocker) runner.Run(SetupCommands.ResumeBitLocker(r.Drive));

        var dev = SystemProbe.FindDevice();
        bool stillOurs = !Dry && (dev?.Service?.Equals(DriverPackage.ServiceName, StringComparison.OrdinalIgnoreCase) ?? false);
        if (stillOurs) problems.Add("kart hâlâ NitroDrop sürücüsünde (yeniden başlatınca Intel sürücüsü geri gelir)");
        return new HelperResult
        {
            Ok = problems.Count == 0,
            RebootRequired = r.DisableTestSigning,
            Message = problems.Count == 0
                ? "NitroDrop sürücüsü kaldırıldı, Intel sürücüsü geri yüklendi, test modu kapatıldı."
                : "Bazı adımlar tamamlanamadı: " + string.Join("; ", problems),
        };
    }

    private int RemoveOurCerts(string? thumbprint)
    {
        int n = 0;
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            using var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(Dry ? OpenFlags.ReadOnly : OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Cast<X509Certificate2>().ToList())
            {
                bool match = (thumbprint is not null && c.Thumbprint.Equals(thumbprint, StringComparison.OrdinalIgnoreCase))
                             || CodeSigner.IsOurCertificate(c);
                if (!match) continue;
                if (Dry) SetupLog.Write($@"[dry-run] sertifika silinecekti: LocalMachine\{name} {c.Thumbprint}");
                else
                {
                    store.Remove(c);
                    SetupLog.Write($@"sertifika silindi: LocalMachine\{name} {c.Thumbprint}");
                }
                n++;
            }
        }
        return n;
    }

    // ---------------- interop ----------------

    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "UpdateDriverForPlugAndPlayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareId, string fullInfPath,
        uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
}
