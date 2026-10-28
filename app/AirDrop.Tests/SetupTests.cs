using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AirDrop.App.Setup;

namespace AirDrop.Tests;

/// <summary>
/// Setup wizard logic. Nothing here changes the system: no store, registry, bcdedit, pnputil or reboot.
/// Signing tests sign temp copies with an in-memory key only.
/// </summary>
public class SetupTests
{
    static SetupTests() => SetupLog.FilePath = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", "setup-test.log");

    private static readonly SupportedCard Ax203 = SupportedDevices.All[0];

    private static SystemSnapshot Snap(bool device = true, bool? secureBoot = true, bool testSign = false,
        BitLockerQuickState bl = BitLockerQuickState.On, string? service = "Netwtw12") => new()
    {
        Device = device ? new DeviceInstance(Ax203, @"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01\3&11583659&0&A3", service, true) : null,
        SecureBootOn = secureBoot,
        TestSigningActive = testSign,
        BitLocker = bl,
    };

    // ---------------- supported devices ----------------

    [Fact]
    public void Pci_ids_parse_and_match_the_table()
    {
        var p = SupportedDevices.Parse(@"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01\3&11583659&0&A3")!;
        Assert.Equal((ushort)0x8086, p.Vendor);
        Assert.Equal((ushort)0x51F1, p.Device);
        Assert.Equal(0x02748086u, p.Subsystem);
        Assert.Equal((byte)1, p.Revision);
        Assert.Null(SupportedDevices.Parse("USB\\VID_0E8D"));
        Assert.Null(SupportedDevices.Parse(""));

        // the hardware ID list Windows reports for this card
        Assert.Same(Ax203, SupportedDevices.Match(new[]
        {
            @"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01", @"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086",
            @"PCI\VEN_8086&DEV_51F1&CC_028000", @"PCI\VEN_8086&DEV_51F1&CC_0280",
        }));
        Assert.Same(Ax203, SupportedDevices.MatchEnumKey("VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01"));
        Assert.Same(Ax203, SupportedDevices.MatchEnumKey("ven_8086&dev_51f1&subsys_02748086"));
        // same chip, other subsystem (OEM variant not in the INF) / other chips
        Assert.Null(SupportedDevices.MatchEnumKey("VEN_8086&DEV_51F1&SUBSYS_00748086&REV_01"));
        Assert.Null(SupportedDevices.MatchEnumKey("VEN_8086&DEV_51F1&CC_0280"));
        Assert.Null(SupportedDevices.MatchEnumKey("VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A"));
        Assert.Equal("VEN_8086&DEV_51F1&SUBSYS_02748086", SupportedDevices.EnumKeyPrefix(Ax203));
    }

    [Fact]
    public void Every_supported_card_is_listed_in_the_inf()
    {
        var inf = File.ReadAllText(Path.Combine(RepoRoot(), "driver", DriverPackage.InfName));
        foreach (var card in SupportedDevices.All)
            Assert.Contains(card.HardwareId, inf, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- flow / detection parsing ----------------

    [Fact]
    public void Flow_walks_the_steps_and_skips_what_is_done()
    {
        var st = new SetupState();
        Assert.Equal(SetupStep.Compatibility, SetupFlow.Next(Snap(device: false), st));
        Assert.Equal(SetupStep.Compatibility, SetupFlow.Start(Snap(), st));   // fresh run: welcome page
        Assert.Equal(SetupStep.Consent, SetupFlow.Next(Snap(), st));

        st.ConsentGiven = true;
        Assert.Equal(SetupStep.BitLocker, SetupFlow.Next(Snap(), st));
        Assert.Equal(SetupStep.SecureBoot, SetupFlow.Next(Snap(bl: BitLockerQuickState.NotEncrypted), st));
        Assert.Equal(SetupStep.SecureBoot, SetupFlow.Next(Snap(bl: BitLockerQuickState.Off), st));

        st.BitLockerBackupConfirmed = true;
        Assert.Equal(SetupStep.SecureBoot, SetupFlow.Next(Snap(), st));
        Assert.Equal(SetupStep.SecureBoot, SetupFlow.Start(Snap(), st));     // resumed run jumps ahead
        Assert.Equal(SetupStep.TestSigning, SetupFlow.Next(Snap(secureBoot: false), st));
        Assert.Equal(SetupStep.TestSigning, SetupFlow.Next(Snap(secureBoot: null), st));   // legacy boot: nothing to turn off
        Assert.Equal(SetupStep.Install, SetupFlow.Next(Snap(secureBoot: false, testSign: true), st));
        Assert.Equal(SetupStep.Done, SetupFlow.Next(Snap(secureBoot: false, testSign: true, service: "airdropwifi"), st));
        Assert.Equal(SetupStep.Done, SetupFlow.Start(Snap(service: "AirDropWifi"), new SetupState()));
    }

    [Fact]
    public void BitLocker_step_only_matters_while_a_measured_change_is_ahead()
    {
        var st = new SetupState { ConsentGiven = true };
        Assert.True(SetupFlow.BitLockerRelevant(Snap()));
        Assert.True(SetupFlow.BitLockerRelevant(Snap(secureBoot: false)));          // test mode still to come
        Assert.False(SetupFlow.BitLockerRelevant(Snap(secureBoot: false, testSign: true)));
        Assert.Equal(SetupStep.Install, SetupFlow.Next(Snap(secureBoot: false, testSign: true), st));
    }

    [Fact]
    public void Test_signing_detection_parses_ci_options_and_start_options()
    {
        Assert.True(SetupFlow.TestSignFromCodeIntegrity(0x2));
        Assert.True(SetupFlow.TestSignFromCodeIntegrity(0x1 | 0x2 | 0x400));
        Assert.False(SetupFlow.TestSignFromCodeIntegrity(0x1 | 0x400));
        Assert.False(SetupFlow.TestSignFromStartOptions(" NOEXECUTE=OPTIN  FVEBOOT=2125824  NOVGA"));   // this machine
        Assert.True(SetupFlow.TestSignFromStartOptions(" NOEXECUTE=OPTIN  TESTSIGNING  NOVGA"));
        Assert.True(SetupFlow.TestSignFromStartOptions("testsigning"));
        Assert.False(SetupFlow.TestSignFromStartOptions("NOTESTSIGNINGX"));
        Assert.False(SetupFlow.TestSignFromStartOptions(null));

        var st = new SetupState { TestSigningEnabledByUs = true };
        Assert.True(SetupFlow.TestSigningPendingReboot(Snap(secureBoot: false), st));
        Assert.False(SetupFlow.TestSigningPendingReboot(Snap(secureBoot: false, testSign: true), st));
    }

    [Fact]
    public void BitLocker_shell_values_map_to_states()
    {
        Assert.Equal(BitLockerQuickState.NotEncrypted, SetupFlow.BitLockerFromShell(null));
        Assert.Equal(BitLockerQuickState.On, SetupFlow.BitLockerFromShell(1));
        Assert.Equal(BitLockerQuickState.Off, SetupFlow.BitLockerFromShell(2));
        Assert.Equal(BitLockerQuickState.InProgress, SetupFlow.BitLockerFromShell(3u));
        Assert.Equal(BitLockerQuickState.Suspended, SetupFlow.BitLockerFromShell(5L));
        Assert.Equal(BitLockerQuickState.On, SetupFlow.BitLockerFromShell(6));
        Assert.Equal(BitLockerQuickState.Unknown, SetupFlow.BitLockerFromShell("?"));
    }

    [Fact]
    public void Machine_name_does_not_repeat_the_vendor()
    {
        Assert.Equal("Acer Nitro V 15", new MachineInfo("Acer", "Nitro ANV15-51", "Acer Nitro V 15", null).Describe());
        Assert.Equal("LENOVO ThinkPad T14 Gen 2", new MachineInfo("LENOVO", "20W0", "ThinkPad T14 Gen 2", null).Describe());
        Assert.Equal("HP Victus 16", new MachineInfo("HP", "Victus 16", null, null).Describe());
        Assert.Equal("Bu bilgisayar", MachineInfo.Unknown.Describe());
    }

    // ---------------- vendor table ----------------

    [Theory]
    [InlineData("LENOVO", "20XW0055TX", "ThinkPad X1 Carbon Gen 9", "lenovo-thinkpad")]
    [InlineData("LENOVO", "21DH", "ThinkBook 14 G4 IAP", "lenovo-thinkpad")]
    [InlineData("LENOVO", "82XF", "IdeaPad Gaming 3 15ARH7", "lenovo")]
    [InlineData("LENOVO", "82RB", "Legion 5 15IAH7H", "lenovo")]
    [InlineData("HP", "Victus by HP Laptop 16-d0xxx", null, "hp")]
    [InlineData("Hewlett-Packard", "HP Pavilion", null, "hp")]
    [InlineData("Dell Inc.", "XPS 13 9310", null, "dell")]
    [InlineData("Alienware", "Alienware m15 R7", null, "dell")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Strix G513QM", "ROG Strix", "asus")]
    [InlineData("Acer", "Nitro ANV15-51", "Acer Nitro V 15", "acer")]
    [InlineData("Micro-Star International Co., Ltd.", "GF63 Thin 11UC", "GF", "msi")]
    [InlineData("MSI", "Katana", null, "msi")]
    [InlineData("GIGABYTE", "G5 KF", null, "gigabyte")]
    [InlineData("SAMSUNG ELECTRONICS CO., LTD.", "950XED", "Galaxy Book2 Pro", "samsung")]
    [InlineData("HUAWEI", "MRGF-XX", "MateBook D 15", "huawei")]
    [InlineData("Casper Bilgisayar Sistemleri A.S.", "EXCALIBUR G770", null, "casper")]
    [InlineData("MONSTER", "ABRA A5 V20.1", null, "monster")]
    [InlineData("Monster Notebook", "TULPAR T5", null, "monster")]
    [InlineData("Notebook", "NH5x_7xRCx", null, "clevo")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", "Surface", "surface")]
    [InlineData("Microsoft Corporation", "Virtual Machine", "Virtual Machine", "generic")]
    [InlineData("ASRock", "B550M Pro4", null, "asrock")]
    [InlineData("Shopify Hardware", "X", null, "generic")]   // contains "HP"-like letters, must not match HP
    [InlineData("Philips", "X", null, "generic")]
    [InlineData(null, null, null, "generic")]
    [InlineData("", "", "", "generic")]
    public void Vendor_guide_lookup(string? manufacturer, string? product, string? family, string expected) =>
        Assert.Equal(expected, VendorGuides.Find(manufacturer, product, family).Id);

    [Fact]
    public void Vendor_guides_are_complete()
    {
        var all = VendorGuides.All.Append(VendorGuides.Generic).ToList();
        Assert.Equal(all.Count, all.Select(g => g.Id).Distinct().Count());
        foreach (var g in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(g.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(g.BiosKeys));
            Assert.False(string.IsNullOrWhiteSpace(g.MenuPath));
            Assert.True(g.DisableSteps.Length >= 2, g.Id);
            Assert.All(g.DisableSteps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            Assert.Equal(3, g.EnableSteps.Length);
            Assert.Contains(g.MenuPath, g.EnableSteps[0]);
        }
        // Turkish brands present
        Assert.Contains(VendorGuides.All, g => g.Id == "casper");
        Assert.Contains(VendorGuides.All, g => g.Id == "monster");
    }

    // ---------------- command builders ----------------

    [Fact]
    public void Commands_are_built_exactly()
    {
        var sys = Environment.SystemDirectory;
        var ts = SetupCommands.TestSigning(true);
        Assert.Equal(Path.Combine(sys, "bcdedit.exe"), ts.FileName);
        Assert.Equal("/set {current} testsigning on", ts.Arguments);
        Assert.Equal("/set {current} testsigning off", SetupCommands.TestSigning(false).Arguments);

        var fw = SetupCommands.RebootToFirmware();
        Assert.Equal(Path.Combine(sys, "shutdown.exe"), fw.FileName);
        Assert.Equal("/r /fw /t 5", fw.Arguments);
        Assert.Equal("/r /t 5", SetupCommands.Reboot().Arguments);

        var bl = SetupCommands.SuspendBitLocker("c:");
        Assert.Equal(Path.Combine(sys, "manage-bde.exe"), bl.FileName);
        Assert.Equal("-protectors -disable C: -RebootCount 1", bl.Arguments);
        Assert.Equal("-protectors -enable C:", SetupCommands.ResumeBitLocker("C:").Arguments);
        Assert.Throws<ArgumentException>(() => SetupCommands.SuspendBitLocker("C: & calc"));
        Assert.Throws<ArgumentException>(() => SetupCommands.ResumeBitLocker("C:\\"));

        var add = SetupCommands.AddDriver(@"C:\ProgramData\NitroDrop\driver\airdropwifi.inf");
        Assert.Equal(Path.Combine(sys, "pnputil.exe"), add.FileName);
        Assert.Equal(@"/add-driver C:\ProgramData\NitroDrop\driver\airdropwifi.inf /install", add.Arguments);
        Assert.Equal("/add-driver \"C:\\Program Files\\Nitro Drop\\a.inf\" /install",
            SetupCommands.AddDriver(@"C:\Program Files\Nitro Drop\a.inf").Arguments);

        Assert.Equal("/delete-driver oem42.inf /uninstall /force", SetupCommands.DeleteDriver("oem42.inf").Arguments);
        Assert.Throws<ArgumentException>(() => SetupCommands.DeleteDriver("..\\oem1.inf"));
        Assert.Throws<ArgumentException>(() => SetupCommands.DeleteDriver("oem1.inf /x"));
        Assert.Throws<ArgumentException>(() => SetupCommands.DeleteDriver("netwtw12.inf"));
        Assert.Equal("/scan-devices", SetupCommands.ScanDevices().Arguments);
    }

    [Fact]
    public void BitLocker_query_is_an_encoded_wmi_script_for_the_drive()
    {
        var q = SetupCommands.BitLockerQuery("C:");
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", q.FileName);
        int i = q.Args.ToList().IndexOf("-EncodedCommand");
        Assert.True(i > 0);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(q.Args[i + 1]));
        Assert.Contains("Win32_EncryptableVolume", script);
        Assert.Contains("DriveLetter='C:'", script);
        Assert.Contains("GetKeyProtectorNumericalPassword", script);
        Assert.Contains("ConvertTo-Json", script);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\a b\", "\"C:\\a b\\\\\"")]
    [InlineData(@"C:\dir\file", @"C:\dir\file")]
    [InlineData("{current}", "{current}")]
    public void Command_line_quoting(string arg, string expected) => Assert.Equal(expected, CommandLine.Quote(arg));

    [Fact]
    public void Quoting_round_trips_through_CommandLineToArgvW()
    {
        var args = new[] { "--setup-helper", "a b", "x\"y", @"C:\Program Files\NitroDrop\", @"tail\\", "", "ğüşİöç" };
        var parsed = SplitWindows("prog.exe " + CommandLine.Join(args));
        Assert.Equal(args, parsed.Skip(1).ToArray());
    }

    [Fact]
    public void Bcdedit_errors_are_classified()
    {
        Assert.Equal(BcdOutcome.Ok, SetupCommands.ClassifyBcdedit(0, "The operation completed successfully.", true));
        Assert.Equal(BcdOutcome.BlockedBySecureBoot, SetupCommands.ClassifyBcdedit(1,
            "An error has occurred setting the element data.\r\nThe value is protected by Secure Boot policy and cannot be modified or deleted.", null));
        Assert.Equal(BcdOutcome.BlockedBySecureBoot, SetupCommands.ClassifyBcdedit(1,
            "Öğe verileri ayarlanırken bir hata oluştu.\r\nDeğer Güvenli Önyükleme ilkesiyle korunuyor ve değiştirilemez veya silinemez.", false));
        Assert.Equal(BcdOutcome.BlockedBySecureBoot, SetupCommands.ClassifyBcdedit(1, "???", true));   // unknown language, state says on
        Assert.Equal(BcdOutcome.AccessDenied, SetupCommands.ClassifyBcdedit(1, "The boot configuration data store could not be opened.\r\nAccess is denied.", false));
        Assert.Equal(BcdOutcome.AccessDenied, SetupCommands.ClassifyBcdedit(1, "Erişim engellendi.", false));
        Assert.Equal(BcdOutcome.Failed, SetupCommands.ClassifyBcdedit(1, "something else", false));
        Assert.Contains("Secure Boot", SetupCommands.Describe(BcdOutcome.BlockedBySecureBoot));

        Assert.Equal(PnpOutcome.Ok, SetupCommands.ClassifyPnputil(0));
        Assert.Equal(PnpOutcome.RebootRequired, SetupCommands.ClassifyPnputil(3010));
        Assert.Equal(PnpOutcome.NothingToDo, SetupCommands.ClassifyPnputil(259));
        Assert.Equal(PnpOutcome.Failed, SetupCommands.ClassifyPnputil(5));
    }

    [Fact]
    public void Dry_run_runner_records_and_executes_nothing()
    {
        var r = new DryRunCommandRunner();
        var res = r.Run(SetupCommands.TestSigning(true));
        Assert.True(r.IsDryRun);
        Assert.True(res.DryRun);
        Assert.Equal(0, res.ExitCode);
        Assert.Single(r.Commands);
        Assert.Contains("[dry-run]", File.ReadAllText(SetupLog.FilePath));
    }

    // ---------------- BitLocker JSON ----------------

    [Fact]
    public void BitLocker_json_is_parsed()
    {
        var on = BitLockerInfo.Parse("""{"present":true,"protectionStatus":1,"conversionStatus":1,"percent":100.0,"recovery":[{"id":"{AAAA-1}","password":"111111-222222"},{"id":"{BBBB-2}","password":null}]}""");
        Assert.True(on.IsProtected);
        Assert.Equal(BitLockerQuickState.On, on.ToQuick());
        Assert.Equal(2, on.Recovery.Count);
        Assert.Equal("111111-222222", on.Recovery[0].Password);
        Assert.Null(on.Recovery[1].Password);

        var single = BitLockerInfo.Parse("""{"present":true,"protectionStatus":0,"conversionStatus":1,"percent":100,"recovery":{"id":"{CCCC}","password":"9"}}""");
        Assert.Single(single.Recovery);
        Assert.True(single.IsSuspended);
        Assert.Equal(BitLockerQuickState.Suspended, single.ToQuick());

        Assert.Equal(BitLockerQuickState.NotEncrypted, BitLockerInfo.Parse("""{"present":false}""").ToQuick());
        Assert.Equal(BitLockerQuickState.NotEncrypted,
            BitLockerInfo.Parse("""{"present":true,"protectionStatus":0,"conversionStatus":0,"percent":0,"recovery":[]}""").ToQuick());

        var text = on.FormatBackup("EMRE-PC", new DateTime(2026, 10, 28, 9, 30, 0));
        Assert.Contains("AAAA-1", text);
        Assert.Contains("111111-222222", text);
        Assert.Contains("EMRE-PC", text);
        Assert.Contains("aka.ms/myrecoverykey", text);
    }

    // ---------------- helper protocol ----------------

    [Fact]
    public void Helper_request_round_trips_through_the_command_line()
    {
        var req = new HelperRequest
        {
            Action = HelperAction.InstallDriver,
            PackageDir = @"C:\Users\Emre Can Temur\AppData\Local\airdrop-win\driver-pkg",
            HardwareId = Ax203.HardwareId,
            SuspendBitLocker = true,
            Language = "de",
        };
        var cmd = HelperProtocol.BuildArguments("NitroDrop.Setup.0123abcd", req);
        var argv = SplitWindows("NitroDrop.exe " + cmd).Skip(1).ToArray();
        Assert.True(HelperProtocol.IsHelperInvocation(argv));
        Assert.True(HelperProtocol.TryParse(argv, out var pipe, out var back));
        Assert.Equal("NitroDrop.Setup.0123abcd", pipe);
        Assert.Equal(HelperAction.InstallDriver, back!.Action);
        Assert.Equal(req.PackageDir, back.PackageDir);
        Assert.Equal(req.HardwareId, back.HardwareId);
        Assert.True(back.SuspendBitLocker);
        Assert.Equal("de", back.Language);   // the elevated helper answers in the wizard's UI language
        Assert.Equal("tr", new HelperRequest().Language);

        Assert.False(HelperProtocol.TryParse(new[] { "--send", "x" }, out _, out _));
        Assert.False(HelperProtocol.TryParse(new[] { "--setup-helper", "--pipe", "evil", "--request", HelperProtocol.Encode(req) }, out _, out _));
        Assert.False(HelperProtocol.TryParse(new[] { "--setup-helper", "--pipe", "NitroDrop.Setup.x", "--request", "!!notbase64" }, out _, out _));
        Assert.False(HelperProtocol.TryParse(new[] { "--setup-helper", "--pipe", "NitroDrop.Setup.x" }, out _, out _));
    }

    // ---------------- state ----------------

    [Fact]
    public void State_persists_and_resets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "setup.json");
        try
        {
            var fresh = SetupState.Load(path);
            Assert.False(fresh.HasChanges);
            Assert.True(fresh.SuspendBitLockerOnReboot);

            var s = new SetupState { ConsentGiven = true, TestSigningEnabledByUs = true, CertThumbprint = "AB", PublishedInfs = { "oem7.inf" } };
            s.Save(path);
            var back = SetupState.Load(path);
            Assert.True(back.ConsentGiven);
            Assert.Equal("AB", back.CertThumbprint);
            Assert.Equal(new[] { "oem7.inf" }, back.PublishedInfs);
            Assert.True(back.HasChanges);
            back.ResetAfterRollback();
            Assert.False(back.HasChanges);

            File.WriteAllText(path, "{ not json");
            Assert.False(SetupState.Load(path).ConsentGiven);   // corrupt → defaults, no throw
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    // ---------------- driver package / INF ----------------

    [Fact]
    public void Our_inf_is_recognised_also_when_published()
    {
        var ours = File.ReadAllText(Path.Combine(RepoRoot(), "driver", DriverPackage.InfName));
        Assert.True(DriverPackage.IsOurInf(ours));
        Assert.False(DriverPackage.IsOurInf("[Version]\nSignature=\"$WINDOWS NT$\"\nProvider=%Intel%\nCatalogFile=netwtw12.cat\n[Strings]\nIntel=\"Intel\"\n"));
        Assert.False(DriverPackage.IsOurInf("[Version]\nProvider=\"someone\"\nCatalogFile=airdropwifi.cat\n"));   // same cat name, other provider
        Assert.True(DriverPackage.IsOurInf("[version]\r\nprovider = \"airdrop-win\" ; c\r\ncatalogfile=AIRDROPWIFI.CAT\r\n"));

        var dir = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "oem3.inf"), ours);
            File.WriteAllText(Path.Combine(dir, "oem12.inf"), ours, Encoding.Unicode);   // UTF-16 INFs exist too
            File.WriteAllText(Path.Combine(dir, "oem4.inf"), "[Version]\nProvider=Intel\nCatalogFile=x.cat\n");
            File.WriteAllText(Path.Combine(dir, "netwtw12.inf"), ours);                    // not an oem*.inf
            Assert.Equal(new[] { "oem12.inf", "oem3.inf" }, DriverPackage.FindPublishedInfs(dir));
            Assert.Equal(DriverPackage.AllFiles, DriverPackage.Missing(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- firmware ----------------

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(FirmwareSpec.UserAgent, request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Firmware_is_verified_and_reused_from_a_local_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(src);
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(Path.Combine(src, "fw.bin"), bytes);
            var spec = new FirmwareFile("x/fw.bin", "fw.bin", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            Assert.True(FirmwareFetcher.Verify(Path.Combine(src, "fw.bin"), spec));
            Assert.False(FirmwareFetcher.Verify(Path.Combine(src, "fw.bin"), spec with { Size = 1 }));
            Assert.False(FirmwareFetcher.Verify(Path.Combine(src, "fw.bin"), spec with { Sha256 = new string('0', 64) }));

            var handler = new NoNetwork();
            var fetcher = new FirmwareFetcher(new HttpClient(handler));
            var got = await fetcher.EnsureAsync(spec, dest, new[] { src });
            Assert.Equal(Path.Combine(dest, "fw.bin"), got);
            Assert.Equal(0, handler.Calls);
            await fetcher.EnsureAsync(spec, dest);   // cached now
            Assert.Equal(0, handler.Calls);

            // wrong local copy + no network → both mirrors tried, clear error
            var bad = spec with { Name = "fw2.bin", Sha256 = new string('1', 64) };
            File.WriteAllBytes(Path.Combine(src, "fw2.bin"), bytes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fetcher.EnsureAsync(bad, dest, new[] { src }));
            Assert.Equal(2, handler.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Firmware_pins_match_the_powershell_script()
    {
        var ps = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "fetch-firmware.ps1"));
        Assert.Contains($"$tag  = '{FirmwareSpec.Tag}'", ps);
        foreach (var f in FirmwareSpec.Files)
        {
            Assert.Contains(f.Sha256, ps);
            Assert.Contains(f.RepoPath, ps);
        }
        Assert.Contains(FirmwareSpec.Firmware.Size!.Value.ToString(), ps);
        Assert.Equal(SupportedDevices.All[0].FirmwareFile, FirmwareSpec.Firmware.Name);
    }

    // ---------------- signing (temp copies, in-memory key) ----------------

    [Fact]
    public void Per_install_certificate_is_a_3072_bit_code_signing_cert()
    {
        using var cert = CodeSigner.CreatePerInstallCertificate("EMRE PC/ğ");
        Assert.True(cert.HasPrivateKey);
        Assert.Equal(3072, cert.GetRSAPublicKey()!.KeySize);
        Assert.True(CodeSigner.IsOurCertificate(cert));
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.3");
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal("sha256RSA", cert.SignatureAlgorithm.FriendlyName);
        Assert.True(cert.NotAfter > DateTime.Now.AddYears(20));
    }

    [Fact]
    public void Signing_a_pe_embeds_our_signature_without_changing_its_catalog_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pe = Path.Combine(dir, "unsigned.dll");
            File.Copy(typeof(SetupTests).Assembly.Location, pe);
            Assert.Null(CodeSigner.GetPeSigner(pe));
            var before256 = CodeSigner.ComputeMemberTag(pe, "SHA256");
            var before1 = CodeSigner.ComputeMemberTag(pe, "SHA1");

            using var cert = CodeSigner.CreatePerInstallCertificate("test");
            CodeSigner.SignFile(pe, cert);

            Assert.Equal(cert.Thumbprint, CodeSigner.GetPeSigner(pe));
            Assert.Equal(before256, CodeSigner.ComputeMemberTag(pe, "SHA256"));   // Authenticode hash skips the cert table
            Assert.Equal(before1, CodeSigner.ComputeMemberTag(pe, "SHA1"));
            Assert.Equal(64, before256.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The real package: the pre-built unsigned catalog (driver\package.ps1) covers inf + sys + ucode, signing the
    /// .sys and the .cat on the "user's machine" keeps it valid. Skipped silently when the driver/firmware
    /// haven't been built/fetched in this checkout.
    /// </summary>
    [Fact]
    public void Prebuilt_catalog_stays_valid_after_per_install_signing()
    {
        var repo = RepoRoot();
        var files = new Dictionary<string, string>
        {
            [DriverPackage.InfName] = Path.Combine(repo, "driver", DriverPackage.InfName),
            [DriverPackage.SysName] = Path.Combine(repo, "driver", "out", DriverPackage.SysName),
            [DriverPackage.CatName] = Path.Combine(repo, "driver", "out", DriverPackage.CatName),
            [FirmwareSpec.Firmware.Name] = Path.Combine(repo, "driver", "firmware", FirmwareSpec.Firmware.Name),
        };
        if (files.Values.Any(f => !File.Exists(f))) return;

        var dir = Path.Combine(Path.GetTempPath(), "airdrop-win-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, src) in files) File.Copy(src, Path.Combine(dir, name));
            Assert.True(FirmwareFetcher.Verify(Path.Combine(dir, FirmwareSpec.Firmware.Name), FirmwareSpec.Firmware));
            var cat = Path.Combine(dir, DriverPackage.CatName);
            Assert.True(CodeSigner.ReadCatalogMemberTags(cat).Count >= 3);
            Assert.Null(CodeSigner.GetCatalogSigner(cat));                         // shipped unsigned
            Assert.Empty(CodeSigner.VerifyPackageAgainstCatalog(dir));

            using var cert = CodeSigner.CreatePerInstallCertificate("test");
            CodeSigner.SignFile(Path.Combine(dir, DriverPackage.SysName), cert);
            CodeSigner.SignFile(cat, cert);

            Assert.Equal(cert.Thumbprint, CodeSigner.GetPeSigner(Path.Combine(dir, DriverPackage.SysName)));
            Assert.Equal(cert.Thumbprint, CodeSigner.GetCatalogSigner(cat));
            Assert.Empty(CodeSigner.VerifyPackageAgainstCatalog(dir));             // members unchanged after signing

            // a tampered firmware is caught
            File.AppendAllText(Path.Combine(dir, FirmwareSpec.Firmware.Name), "x");
            Assert.Equal(new[] { FirmwareSpec.Firmware.Name }, CodeSigner.VerifyPackageAgainstCatalog(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------------- helpers ----------------

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "driver", DriverPackage.InfName))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (driver\\airdropwifi.inf) not found");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    private static string[] SplitWindows(string cmdLine)
    {
        var argv = CommandLineToArgvW(cmdLine, out int argc);
        try
        {
            var result = new string[argc];
            for (int i = 0; i < argc; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(argv); }
    }
}
