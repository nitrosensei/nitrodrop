using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using AirDrop.App.Services;
using AirDrop.Core;
using AirDrop.Core.Devices;
using AirDrop.Core.Plist;
using AirDrop.Core.Security;
using Nitro.Sync.Model;

namespace AirDrop.Tests;

/// <summary>Apple model table, PC model cleanup, chassis detection, device art resolution, model markers on the wire.</summary>
public class DeviceModelTests
{
    private static readonly System.Security.Cryptography.X509Certificates.X509Certificate2 Cert = CertificateStore.CreateEphemeral();

    private static readonly HostDevice Acer = new()
    {
        Manufacturer = "Acer", Model = "Nitro V 15", Chassis = HostChassis.Laptop, Platform = "windows",
    };

    // ---------------- Apple table ----------------

    [Theory]
    [InlineData("iPhone1,1", "iPhone", AppleFamily.iPhone, 2007, AppleForm.HomeButton)]
    [InlineData("iPhone10,3", "iPhone X", AppleFamily.iPhone, 2017, AppleForm.Notch)]
    [InlineData("iPhone12,8", "iPhone SE (2nd generation)", AppleFamily.iPhone, 2020, AppleForm.HomeButton)]
    [InlineData("iPhone14,7", "iPhone 14", AppleFamily.iPhone, 2022, AppleForm.Notch)]
    [InlineData("iPhone15,2", "iPhone 14 Pro", AppleFamily.iPhone, 2022, AppleForm.DynamicIsland)]
    [InlineData("iPhone16,1", "iPhone 15 Pro", AppleFamily.iPhone, 2023, AppleForm.DynamicIsland)]
    [InlineData("iPhone17,5", "iPhone 16e", AppleFamily.iPhone, 2025, AppleForm.Notch)]
    [InlineData("iPhone18,1", "iPhone 17 Pro", AppleFamily.iPhone, 2025, AppleForm.DynamicIsland)]
    [InlineData("iPhone18,2", "iPhone 17 Pro Max", AppleFamily.iPhone, 2025, AppleForm.DynamicIsland)]
    [InlineData("iPhone18,3", "iPhone 17", AppleFamily.iPhone, 2025, AppleForm.DynamicIsland)]
    [InlineData("iPhone18,4", "iPhone Air", AppleFamily.iPhone, 2025, AppleForm.DynamicIsland)]
    [InlineData("iPad13,18", "iPad (10th generation)", AppleFamily.iPad, 2022, AppleForm.NoHomeButton)]
    [InlineData("iPad7,5", "iPad (6th generation)", AppleFamily.iPad, 2018, AppleForm.HomeButton)]
    [InlineData("iPad16,5", "iPad Pro 13-inch (M4)", AppleFamily.iPad, 2024, AppleForm.NoHomeButton)]
    [InlineData("iPad14,1", "iPad mini (6th generation)", AppleFamily.iPad, 2021, AppleForm.NoHomeButton)]
    [InlineData("iPad11,3", "iPad Air (3rd generation)", AppleFamily.iPad, 2019, AppleForm.HomeButton)]
    [InlineData("MacBookPro18,3", "MacBook Pro (14-inch, M1 Pro, 2021)", AppleFamily.Mac, 2021, AppleForm.Laptop)]
    [InlineData("MacBookAir10,1", "MacBook Air (M1, 2020)", AppleFamily.Mac, 2020, AppleForm.Laptop)]
    [InlineData("Mac14,2", "MacBook Air (M2, 2022)", AppleFamily.Mac, 2022, AppleForm.Laptop)]
    [InlineData("Mac16,10", "Mac mini (M4, 2024)", AppleFamily.Mac, 2024, AppleForm.MacMini)]
    [InlineData("Mac13,1", "Mac Studio (M1 Max)", AppleFamily.Mac, 2022, AppleForm.MacStudio)]
    [InlineData("iMac21,1", "iMac (24-inch, M1, 2021, Four Ports)", AppleFamily.Mac, 2021, AppleForm.AllInOne)]
    [InlineData("MacPro7,1", "Mac Pro (2019)", AppleFamily.Mac, 2019, AppleForm.Tower)]
    [InlineData("MacBook8,1", "MacBook (Retina, 12-inch, Early 2015)", AppleFamily.Mac, 2015, AppleForm.Laptop)]
    [InlineData("MacBookAir6,2", "MacBook Air (13-inch, Mid 2013 / Early 2014)", AppleFamily.Mac, 2013, AppleForm.Laptop)]
    [InlineData("Watch7,5", "Apple Watch Ultra 2", AppleFamily.Watch, 2023, AppleForm.Watch)]
    [InlineData("Watch5,11", "Apple Watch SE (1st generation, 40mm)", AppleFamily.Watch, 2020, AppleForm.Watch)]
    [InlineData("RealityDevice14,1", "Apple Vision Pro", AppleFamily.VisionPro, 2024, AppleForm.Headset)]
    [InlineData("iPod9,1", "iPod touch (7th generation)", AppleFamily.iPodTouch, 2019, AppleForm.HomeButton)]
    public void Table_KnownIdentifiers(string id, string name, AppleFamily family, int year, AppleForm form)
    {
        var m = AppleModels.Find(id);
        Assert.NotNull(m);
        Assert.Equal(name, m!.Name);
        Assert.Equal(family, m.Family);
        Assert.Equal(year, m.Year);
        Assert.Equal(form, m.Form);
        Assert.False(m.IsFallback);
    }

    [Fact]
    public void Table_IsWellFormed()
    {
        var all = AppleModels.All;
        Assert.True(all.Count > 400);
        var idPattern = new Regex(@"^[A-Za-z]+\d+,\d+$");
        foreach (var m in all)
        {
            Assert.Matches(idPattern, m.Identifier);
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.DoesNotContain("Wi-Fi", m.Name);
            Assert.DoesNotContain("Unreleased", m.Name);
            Assert.NotEqual(AppleForm.Unknown, m.Form);
            Assert.Equal(AppleModels.FamilyOf(m.Identifier), m.Family);
        }
        foreach (AppleFamily f in Enum.GetValues<AppleFamily>().Where(f => f != AppleFamily.Unknown))
            Assert.Contains(all, m => m.Family == f);
        // Every iPhone 15+ and every Apple-silicon Mac identifier generation is present.
        Assert.All(new[] { "iPhone15,4", "iPhone15,5", "iPhone16,2", "iPhone17,1", "iPhone17,2", "iPhone17,3", "iPhone17,4",
            "Mac14,3", "Mac14,5", "Mac14,6", "Mac14,7", "Mac14,8", "Mac14,9", "Mac14,10", "Mac14,12", "Mac14,13", "Mac14,14", "Mac14,15",
            "Mac15,3", "Mac15,14", "Mac16,1", "Mac16,13", "Mac17,2", "MacBookPro17,1", "MacBookPro18,1", "Macmini9,1", "iMac21,2" },
            id => Assert.NotNull(AppleModels.Find(id)));
    }

    [Fact]
    public void Table_MarksSingleSourceRecentEntriesUnverified()
    {
        Assert.True(AppleModels.Find("iPhone19,2")!.Unverified);      // 2026, AppleDB only
        Assert.True(AppleModels.Find("iPhone18,5")!.Unverified);      // form factor unconfirmed
        Assert.False(AppleModels.Find("iPhone18,1")!.Unverified);
        Assert.All(AppleModels.All.Where(m => m.Year > 0 && m.Year < 2026 && m.Identifier != "iPhone18,5"), m => Assert.False(m.Unverified));
    }

    [Theory]
    [InlineData("iPhone99,3", "iPhone (iPhone99,3)", AppleFamily.iPhone, AppleForm.DynamicIsland)]
    [InlineData("iPad99,1", "iPad (iPad99,1)", AppleFamily.iPad, AppleForm.NoHomeButton)]
    [InlineData("Mac99,4", "Mac (Mac99,4)", AppleFamily.Mac, AppleForm.Unknown)]
    [InlineData("MacBookPro99,1", "MacBook Pro (MacBookPro99,1)", AppleFamily.Mac, AppleForm.Laptop)]
    [InlineData("Watch99,1", "Apple Watch (Watch99,1)", AppleFamily.Watch, AppleForm.Watch)]
    public void Unknown_Identifiers_DegradeGracefully(string id, string name, AppleFamily family, AppleForm form)
    {
        var m = AppleModels.Resolve(id)!;
        Assert.True(m.IsFallback);
        Assert.Equal(name, m.Name);
        Assert.Equal(name, m.ShortName);
        Assert.Equal(family, m.Family);
        Assert.Equal(form, m.Form);
        Assert.Equal(name, AppleModels.Describe(id));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("OpenDrop", "OpenDrop")]
    [InlineData("airdrop-win", "airdrop-win")]
    public void NonApple_Strings_AreReturnedAsIs(string? id, string expected)
    {
        Assert.Null(AppleModels.Resolve(id));
        Assert.Equal(expected, AppleModels.Describe(id));
    }

    [Theory]
    [InlineData("MacBookPro18,3", "MacBook Pro 14″ (M1 Pro)")]
    [InlineData("Mac14,2", "MacBook Air (M2)")]
    [InlineData("Mac15,13", "MacBook Air 15″ (M3)")]
    [InlineData("iMac21,1", "iMac 24″ (M1)")]
    [InlineData("Mac16,11", "Mac mini (M4 Pro)")]
    [InlineData("MacBookPro11,5", "MacBook Pro 15″ (2015)")]
    [InlineData("iPad16,3", "iPad Pro 11″ (M4)")]
    [InlineData("Watch7,1", "Apple Watch Series 9")]
    [InlineData("Watch5,9", "Apple Watch SE (1st generation)")]
    [InlineData("iPhone16,1", "iPhone 15 Pro")]
    public void ShortNames(string id, string expected) => Assert.Equal(expected, AppleModels.Find(id)!.ShortName);

    // ---------------- Windows model cleanup ----------------

    public static TheoryData<SmbiosStrings, string?, string?> PcCases => new()
    {
        // this development machine (registry values)
        { new("Acer", "Nitro ANV15-51", "Acer Nitro V 15", "V1.60", "RPL", "Sportage_RTH"), "Acer", "Nitro V 15" },
        { new("LENOVO", "21HM002QTX", "ThinkPad X1 Carbon Gen 11", "ThinkPad X1 Carbon Gen 11", "LENOVO", "21HM002QTX"), "Lenovo", "ThinkPad X1 Carbon Gen 11" },
        { new("LENOVO", "82FG", "IdeaPad 5 15ITL05", "Lenovo IdeaPad 5 15ITL05"), "Lenovo", "IdeaPad 5 15ITL05" },
        { new("LENOVO", "20XW0055GE", "ThinkPad X1 Carbon Gen 9", "None"), "Lenovo", "ThinkPad X1 Carbon Gen 9" },
        { new("HP", "HP Pavilion Laptop 15-eg0xxx", "103C_5335KV HP Pavilion", "Type1ProductConfigId"), "HP", "Pavilion Laptop 15-eg0xxx" },
        { new("Hewlett-Packard", "HP EliteBook 840 G3", "103C_5336AN G=N L=BUS B=HP S=ELI"), "HP", "EliteBook 840 G3" },
        { new("Dell Inc.", "XPS 13 9310", "XPS", null, "Dell Inc.", "0DXP1F"), "Dell", "XPS 13 9310" },
        { new("ASUSTeK COMPUTER INC.", "ROG Strix G513QM_G513QM", "ROG Strix", "1.0"), "ASUS", "ROG Strix G513QM" },
        { new("Microsoft Corporation", "Surface Pro 9", "Surface", "124I:00036T:000M:0300000D:11000:01"), "Microsoft", "Surface Pro 9" },
        { new("Micro-Star International Co., Ltd.", "Katana GF66 11UE", "GF", "REV:1.0"), "MSI", "Katana GF66 11UE" },
        { new("Micro-Star International Co., Ltd.", "MS-7C91", "To be filled by O.E.M.", "2.0", "Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK (MS-7C91)"), "MSI", "MAG B550 TOMAHAWK" },
        { new("System manufacturer", "System Product Name", "To be filled by O.E.M.", "System Version", "ASUSTeK COMPUTER INC.", "ROG STRIX B550-F GAMING"), "ASUS", "ROG STRIX B550-F GAMING" },
        { new("To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "To Be Filled By O.E.M.", "ASRock", "B450M Steel Legend"), "ASRock", "B450M Steel Legend" },
        { new("Gigabyte Technology Co., Ltd.", "B660M DS3H DDR4", "B660 MB", "Default string", "Gigabyte Technology Co., Ltd.", "B660M DS3H DDR4"), "Gigabyte", "B660M DS3H DDR4" },
        { new("Apple Inc.", "MacBookPro16,1", "Mac", "1.0"), "Apple", "MacBook Pro (16-inch, 2019)" },
        { new("Casper Bilgisayar Sistemleri", "EXCALIBUR G770", "Default string"), "Casper", "EXCALIBUR G770" },
        { new("Default string", "Default string", "Default string", "Default string", "Default string", "Default string"), null, null },
        { new(null, null), null, null },
    };

    [Theory]
    [MemberData(nameof(PcCases))]
    public void PcModel_Cleanup(SmbiosStrings raw, string? manufacturer, string? model)
    {
        var (mf, m) = PcModelNames.Clean(raw);
        Assert.Equal(manufacturer, mf);
        Assert.Equal(model, m);
    }

    [Theory]
    [InlineData("Acer", "Nitro ANV15-51", "Acer Nitro V 15", "Acer Nitro V 15")]
    [InlineData("LENOVO", "21HM002QTX", "ThinkPad X1 Carbon Gen 11", "Lenovo ThinkPad X1 Carbon Gen 11")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", "Surface", "Microsoft Surface Laptop 5")]
    public void PcModel_DisplayName(string mf, string product, string family, string expected)
    {
        var version = mf == "LENOVO" ? family : null;
        Assert.Equal(expected, PcModelNames.DisplayName(new SmbiosStrings(mf, product, family, version)));
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("System Product Name")]
    [InlineData("  Default string ")]
    [InlineData("0000000000000000")]
    [InlineData("Not Applicable")]
    public void Placeholders_AreDropped(string s) => Assert.Null(PcModelNames.Meaningful(s));

    // ---------------- chassis ----------------

    [Theory]
    [InlineData(3, HostChassis.Desktop)]
    [InlineData(7, HostChassis.Desktop)]
    [InlineData(35, HostChassis.Desktop)]
    [InlineData(9, HostChassis.Laptop)]
    [InlineData(10, HostChassis.Laptop)]       // this machine (Win32_SystemEnclosure.ChassisTypes = 10)
    [InlineData(31, HostChassis.Laptop)]
    [InlineData(0x80 | 10, HostChassis.Laptop)] // lock bit set
    [InlineData(13, HostChassis.AllInOne)]
    [InlineData(30, HostChassis.Tablet)]
    [InlineData(32, HostChassis.Tablet)]
    [InlineData(2, HostChassis.Unknown)]
    public void Chassis_FromSmbios(int type, string expected) => Assert.Equal(expected, HostChassis.FromSmbios(type));

    [Theory]
    [InlineData(1, HostChassis.Desktop)]
    [InlineData(2, HostChassis.Laptop)]
    [InlineData(8, HostChassis.Tablet)]
    [InlineData(0, HostChassis.Unknown)]
    public void Chassis_FromPowerRole(int role, string expected) => Assert.Equal(expected, HostChassis.FromPowerRole(role));

    [Fact]
    public void Chassis_FromRawSmbiosTable()
    {
        static byte[] Struct(byte type, byte[] formatted, params string[] strings)
        {
            var b = new List<byte> { type, (byte)(4 + formatted.Length), 0x00, 0x01 };
            b.AddRange(formatted);
            foreach (var s in strings) { b.AddRange(System.Text.Encoding.ASCII.GetBytes(s)); b.Add(0); }
            if (strings.Length == 0) b.Add(0);
            b.Add(0);
            return b.ToArray();
        }
        var table = new List<byte>();
        table.AddRange(Struct(0, [1, 2, 0, 0xF0, 3, 0xFF], "Insyde Corp.", "V1.60"));            // BIOS
        table.AddRange(Struct(1, [1, 2, 3, 4], "Acer", "Nitro ANV15-51", "V1.60", "SN"));          // System
        table.AddRange(Struct(3, [1, 10, 2, 3], "Acer", "V1.60", "SN"));                          // Enclosure: notebook
        table.AddRange(Struct(127, []));
        var raw = new byte[8 + table.Count];
        BitConverter.GetBytes(table.Count).CopyTo(raw, 4);
        table.CopyTo(raw, 8);
        Assert.Equal(HostChassis.Laptop, LocalHostProbe.ChassisFromTable(raw));

        Assert.Equal(HostChassis.Unknown, LocalHostProbe.ChassisFromTable(null));
        Assert.Equal(HostChassis.Unknown, LocalHostProbe.ChassisFromTable(new byte[8]));
        Assert.Equal(HostChassis.Unknown, LocalHostProbe.ChassisFromTable(raw[..(8 + 20)]));   // truncated: no type 3, no crash
    }

    [Fact]
    public void LocalHost_DetectionNeverThrows()
    {
        var local = LocalHostProbe.Detect();
        if (!OperatingSystem.IsWindows()) { Assert.Null(local); return; }
        Assert.NotNull(local);
        Assert.Equal("windows", local!.Platform);
        Assert.Contains(local.Chassis, new[] { HostChassis.Laptop, HostChassis.Desktop, HostChassis.AllInOne, HostChassis.Tablet, HostChassis.Unknown });
    }

    // ---------------- HostDevice ----------------

    [Fact]
    public void HostDevice_PlistRoundTrip_ThroughBinaryPlist()
    {
        var bytes = BinaryPlist.Write(new Dictionary<string, object> { [AirDropConstants.NitroDropDevice] = Acer.ToPlist() });
        var back = HostDevice.FromPlist(BinaryPlist.ReadDictionary(bytes)[AirDropConstants.NitroDropDevice]);
        Assert.Equal(Acer, back);
        Assert.Equal("Acer Nitro V 15", back!.DisplayName);
    }

    [Fact]
    public void HostDevice_FromPlist_RejectsGarbage_AndSanitizes()
    {
        Assert.Null(HostDevice.FromPlist(null));
        Assert.Null(HostDevice.FromPlist("laptop"));
        Assert.Null(HostDevice.FromPlist(new Dictionary<string, object>()));
        Assert.Null(HostDevice.FromPlist(new Dictionary<string, object> { ["Model"] = 42L }));
        var h = HostDevice.FromPlist(new Dictionary<string, object>
        {
            ["Manufacturer"] = "Ev\u0007il", ["Model"] = new string('x', 500), ["Chassis"] = "Notebook", ["Platform"] = "WINDOWS",
        })!;
        Assert.Equal("Evil", h.Manufacturer);
        Assert.Equal(HostDevice.MaxField, h.Model!.Length);
        Assert.Equal(HostChassis.Laptop, h.Chassis);
        Assert.Equal("windows", h.Platform);
    }

    [Theory]
    [InlineData("Lenovo", "ThinkPad X1", "Lenovo ThinkPad X1")]
    [InlineData("Lenovo", "Lenovo Legion 5", "Lenovo Legion 5")]
    [InlineData(null, "B450M Steel Legend", "B450M Steel Legend")]
    [InlineData("Dell", null, "Dell")]
    [InlineData(null, null, null)]
    public void HostDevice_DisplayName(string? mf, string? model, string? expected) =>
        Assert.Equal(expected, new HostDevice { Manufacturer = mf, Model = model }.DisplayName);

    [Theory]
    [InlineData(HostChassis.Laptop, "MacBookPro18,3")]
    [InlineData(HostChassis.Desktop, "Macmini9,1")]
    [InlineData(HostChassis.AllInOne, "iMac21,1")]
    [InlineData(HostChassis.Tablet, "MacBookPro18,3")]
    [InlineData(null, "MacBookPro18,3")]
    public void ReceiverModel_ForChassis_IsAppleIdentifier(string? chassis, string expected)
    {
        var id = AirDropIdentity.ModelNameForChassis(chassis);
        Assert.Equal(expected, id);
        Assert.Equal(AppleFamily.Mac, AppleModels.Find(id)!.Family);
    }

    // ---------------- names + art in the app ----------------

    [Theory]
    [InlineData(DeviceKind.iPhone, "iPhone16,1", DeviceArtKeys.IphoneIsland)]
    [InlineData(DeviceKind.iPhone, "iPhone14,7", DeviceArtKeys.IphoneNotch)]
    [InlineData(DeviceKind.iPhone, "iPhone12,8", DeviceArtKeys.IphoneHome)]
    [InlineData(DeviceKind.iPhone, "iPhone99,1", DeviceArtKeys.IphoneIsland)]
    [InlineData(DeviceKind.iPhone, null, DeviceArtKeys.IphoneIsland)]
    [InlineData(DeviceKind.iPad, "iPad13,18", DeviceArtKeys.IpadFull)]
    [InlineData(DeviceKind.iPad, "iPad7,5", DeviceArtKeys.IpadHome)]
    [InlineData(DeviceKind.Mac, "Mac14,2", DeviceArtKeys.MacBook)]
    [InlineData(DeviceKind.Mac, "iMac21,1", DeviceArtKeys.IMac)]
    [InlineData(DeviceKind.Mac, "Mac16,10", DeviceArtKeys.MacMini)]
    [InlineData(DeviceKind.Mac, "Mac13,2", DeviceArtKeys.MacStudio)]
    [InlineData(DeviceKind.Mac, "MacPro7,1", DeviceArtKeys.MacPro)]
    [InlineData(DeviceKind.Mac, "Mac99,1", DeviceArtKeys.MacBook)]
    [InlineData(DeviceKind.Unknown, "Watch7,5", DeviceArtKeys.Watch)]
    [InlineData(DeviceKind.Unknown, "RealityDevice17,1", DeviceArtKeys.VisionPro)]
    [InlineData(DeviceKind.Unknown, "iPod9,1", DeviceArtKeys.IphoneHome)]
    [InlineData(DeviceKind.Unknown, "OpenDrop", DeviceArtKeys.Generic)]
    [InlineData(DeviceKind.Windows, "MacBookPro18,3", DeviceArtKeys.WindowsLaptop)]   // our peer's wire id must not draw a Mac
    public void ArtKey_FromKindAndModel(DeviceKind kind, string? model, string expected) =>
        Assert.Equal(expected, DeviceModels.ArtKey(kind, model));

    [Theory]
    [InlineData(HostChassis.Laptop, null, DeviceArtKeys.WindowsLaptop)]
    [InlineData(HostChassis.Desktop, null, DeviceArtKeys.WindowsDesktop)]
    [InlineData(HostChassis.AllInOne, null, DeviceArtKeys.WindowsDesktop)]
    [InlineData(HostChassis.Tablet, null, DeviceArtKeys.Tablet)]
    [InlineData(HostChassis.Unknown, null, DeviceArtKeys.WindowsLaptop)]
    [InlineData(HostChassis.Phone, "android", DeviceArtKeys.AndroidPhone)]
    public void ArtKey_FromHost(string chassis, string? platform, string expected)
    {
        var host = new HostDevice { Manufacturer = "X", Model = "Y", Chassis = chassis, Platform = platform ?? "windows" };
        var kind = platform == "android" ? DeviceKind.Unknown : DeviceKind.Windows;
        Assert.Equal(expected, DeviceModels.ArtKey(kind, "MacBookPro18,3", host));
    }

    [Fact]
    public void Names_PreferHostThenAppleTableThenKind()
    {
        Assert.Equal("Acer Nitro V 15", DeviceModels.Describe(DeviceKind.Windows, "MacBookPro18,3", Acer));
        Assert.Equal("Acer Nitro V 15", DeviceModels.ShortDescribe(DeviceKind.Windows, "MacBookPro18,3", Acer));
        Assert.Equal(DeviceStrings.WindowsPc, DeviceModels.Describe(DeviceKind.Windows, "MacBookPro18,3"));
        Assert.Equal(DeviceStrings.WindowsDesktop, DeviceModels.Describe(DeviceKind.Windows, null, new HostDevice { Chassis = HostChassis.Desktop }));
        Assert.Equal("MacBook Pro (14-inch, M1 Pro, 2021)", DeviceModels.Describe(DeviceKind.Mac, "MacBookPro18,3"));
        Assert.Equal("MacBook Pro 14″ (M1 Pro)", DeviceModels.ShortDescribe(DeviceKind.Mac, "MacBookPro18,3"));
        Assert.Equal("iPhone (iPhone99,9)", DeviceModels.Describe(DeviceKind.iPhone, "iPhone99,9"));
        Assert.Equal(DeviceStrings.iPad, DeviceModels.Describe(DeviceKind.iPad, null));
        Assert.Equal(DeviceStrings.Generic, DeviceModels.Describe(DeviceKind.Unknown, "OpenDrop"));
        Assert.Equal("iPhone 14 Pro", DeviceModels.DescribeIdentifier("iPhone15,2"));
        Assert.Null(DeviceModels.DescribeIdentifier(null));
        Assert.Equal(DeviceArtKeys.IphoneNotch, DeviceModels.ArtKeyForIdentifier("iPhone14,5"));
    }

    /// <summary>Every key the resolver can return has a geometry in Themes/DeviceArt.xaml (and nothing there is orphaned).</summary>
    [Fact]
    public void EveryArtKey_HasGeometry()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AirDrop.App", "Themes", "DeviceArt.xaml"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "AirDrop.App", "Themes", "DeviceArt.xaml"));
        var keys = Regex.Matches(xaml, "x:Key=\"DeviceArt\\.([A-Za-z]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(DeviceArtKeys.All.OrderBy(k => k), keys.OrderBy(k => k));
        foreach (var m in AppleModels.All) Assert.Contains(DeviceModels.ArtForApple(m), keys);
        foreach (var c in new[] { HostChassis.Laptop, HostChassis.Desktop, HostChassis.AllInOne, HostChassis.Tablet, HostChassis.Phone, HostChassis.Unknown })
            Assert.Contains(DeviceModels.ArtForHost(new HostDevice { Chassis = c }), keys);
    }

    // ---------------- Nitro.Sync DeviceState ----------------

    [Fact]
    public void SyncState_Hardware_RoundTrips_AndIsSanitized()
    {
        var s = new DeviceState { DeviceId = "abc", DeviceName = "Emre-PC", Hardware = Acer };
        var json = System.Text.Encoding.UTF8.GetString(s.ToJsonBytes());
        Assert.Contains("\"hardware\":{\"manufacturer\":\"Acer\",\"model\":\"Nitro V 15\",\"chassis\":\"laptop\",\"platform\":\"windows\"}", json);
        Assert.DoesNotContain("displayName", json);
        var back = DeviceState.FromJson(s.ToJsonBytes())!;
        Assert.Equal(Acer, back.Hardware);

        // Older peers: no hardware key.
        Assert.Null(DeviceState.FromJson("""{"deviceId":"x","deviceName":"y"}"""u8)!.Hardware);
        // Hardware is part of the content tag.
        Assert.NotEqual(s.ComputeETag(), (s with { Hardware = null }).ComputeETag());

        var hostile = s with { Hardware = new HostDevice { Manufacturer = "A\u0000B", Model = new string('m', 300), Chassis = "spaceship" } };
        var clean = StateSanitizer.State(hostile).Hardware!;
        Assert.Equal("AB", clean.Manufacturer);
        Assert.Equal(HostDevice.MaxField, clean.Model!.Length);
        Assert.Equal(HostChassis.Unknown, clean.Chassis);
    }

    // ---------------- markers on the wire ----------------

    [Fact]
    public async Task Discover_And_Ask_CarryNitroDropDevice()
    {
        using var dst = new TempDir();
        using var src = new TempDir();
        var desktop = new HostDevice { Manufacturer = "MSI", Model = "MAG Infinite S3", Chassis = HostChassis.Desktop, Platform = "windows" };
        AskRequest? seen = null;
        await using var server = new AirDropServer(new AirDropServerOptions
        {
            ReceiveDirectory = dst.Path,
            Identity = new AirDropIdentity { ComputerName = "Masaüstü", ModelName = "Macmini9,1", Host = desktop },
            Port = 0, EnableMdns = false, Certificate = Cert,
            AskHandler = (a, _) => { seen = a; return Task.FromResult(true); },
        });
        await server.StartAsync();

        using var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, server.Port),
            new AirDropIdentity { ComputerName = "Laptop", ModelName = AirDropIdentity.DefaultModelName, Host = Acer });
        var d = await client.DiscoverAsync();
        Assert.True(d.IsAirDropWin);
        Assert.Equal("Macmini9,1", d.ReceiverModelName);   // Apple-compatible id stays as it was
        Assert.Equal(desktop, d.Host);

        var f = Path.Combine(src.Path, "a.txt");
        File.WriteAllText(f, "x");
        Assert.True(await client.SendAsync([f], new Progress<AirDrop.Core.TransferProgress>()));
        Assert.Equal(Acer, seen!.Host);
        Assert.Equal(AirDropIdentity.DefaultModelName, seen.SenderModelName);

        // Link Ask carries it too.
        Assert.Equal(Acer.ToPlist(), client.BuildLinkAsk([new Uri("https://nitrobyte.com.tr")])[AirDropConstants.NitroDropDevice]);
    }

    [Fact]
    public async Task NoHost_NoMarker_ButStillRecognisedAsAirDropWin()
    {
        using var dst = new TempDir();
        await using var server = new AirDropServer(new AirDropServerOptions
        {
            ReceiveDirectory = dst.Path, Identity = new AirDropIdentity { ComputerName = "Eski" },
            Port = 0, EnableMdns = false, Certificate = Cert,
        });
        await server.StartAsync();
        using var client = new AirDropClient(new IPEndPoint(IPAddress.Loopback, server.Port));
        var d = await client.DiscoverAsync();
        Assert.True(d.IsAirDropWin);
        Assert.Null(d.Host);
        Assert.False(d.Raw.ContainsKey(AirDropConstants.NitroDropDevice));
        Assert.False(client.BuildAsk([]).ContainsKey(AirDropConstants.NitroDropDevice));
    }

    [Fact]
    public async Task Service_PeerInfo_And_IncomingRequest_CarryHost()
    {
        const int mdns = 53681;
        CoreAirDropService New(HostDevice host) => new(new CoreAirDropServiceSettings
        {
            Interface = "loopback", Port = 0, MdnsPort = mdns, Certificate = Cert, PeerLostGrace = TimeSpan.FromSeconds(3), Host = host,
        });
        var desktop = new HostDevice { Manufacturer = "Lenovo", Model = "ThinkCentre M70q", Chassis = HostChassis.Desktop, Platform = "windows" };
        await using var a = New(Acer);
        await using var b = New(desktop);
        using var dirA = new TempDir();
        using var dirB = new TempDir();
        var peers = new ConcurrentDictionary<string, PeerInfo>();
        a.PeerFound += (_, p) => peers[p.Id] = p;
        IncomingRequest? seen = null;
        b.AskHandler = r => { seen = r; return Task.FromResult(true); };
        await Task.WhenAll(
            a.StartAsync(new ServiceOptions { DeviceName = "Model A", ReceiveFolder = dirA.Path }, ReceiveMode.Everyone),
            b.StartAsync(new ServiceOptions { DeviceName = "Model B", ReceiveFolder = dirB.Path }, ReceiveMode.Everyone));

        PeerInfo? peerB = null;
        for (var until = DateTime.UtcNow.AddSeconds(20); peerB is null && DateTime.UtcNow < until; await Task.Delay(50))
            peerB = peers.Values.FirstOrDefault(p => p.Name == "Model B");
        Assert.NotNull(peerB);
        Assert.Equal(DeviceKind.Windows, peerB!.Kind);
        Assert.Equal(desktop, peerB.Host);
        Assert.Equal("Lenovo ThinkCentre M70q", DeviceModels.Describe(peerB.Kind, peerB.ModelName, peerB.Host));
        Assert.Equal(DeviceArtKeys.WindowsDesktop, DeviceModels.ArtKey(peerB.Kind, peerB.ModelName, peerB.Host));

        Assert.Equal(SendOutcome.Delivered, await a.SendLinkAsync(peerB.Id, new Uri("https://nitrobyte.com.tr")));
        Assert.Equal(Acer, seen!.SenderHost);
    }
}
