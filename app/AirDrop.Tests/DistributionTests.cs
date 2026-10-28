using System.IO.Compression;
using System.Security.Cryptography;
using AirDrop.App.Setup;
using Nitro.Awdl.Ip;

namespace AirDrop.Tests;

/// <summary>
/// What is no longer inside the signed packages: wintun.dll (downloaded on demand, verified) and the Wi-Fi driver
/// (NitroDrop-driver-&lt;ver&gt;.zip, imported by the setup wizard).
/// </summary>
public class DistributionTests
{
    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    // ---------------- Wintun ----------------

    [Fact]
    public void Wintun_Pins_MatchOfficialRelease()
    {
        Assert.Equal("https://www.wintun.net/builds/wintun-0.14.1.zip", WintunInstall.ZipUrl.ToString());
        Assert.Equal("07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51", WintunInstall.ZipSha256);
        Assert.Equal("WireGuard LLC", WintunInstall.Signer);
        Assert.Contains("amd64", WintunInstall.DllSha256.Keys);
        Assert.Contains("arm64", WintunInstall.DllSha256.Keys);
        Assert.EndsWith(Path.Combine("airdrop-win", "wintun"), WintunInstall.DefaultDirectory);
    }

    [Fact]
    public void Wintun_InstallFromZip_ExtractsOnlyThisArch_WhenHashesMatch()
    {
        var dll = "fake wintun"u8.ToArray();
        var zip = Zip(($"wintun/bin/{WintunInstall.Arch}/wintun.dll", dll), ("wintun/bin/other/wintun.dll", [1, 2]),
                      ("wintun/LICENSE.txt", "Prebuilt Binaries License"u8.ToArray()), ("../evil.dll", [6, 6, 6]));
        using var dir = new TempDir();
        var pins = new Dictionary<string, string> { [WintunInstall.Arch] = Sha(dll) };
        WintunInstall.InstallFromZip(zip, dir.Path, Sha(zip), pins, checkSignature: false);

        Assert.Equal(dll, File.ReadAllBytes(WintunInstall.DllPath(dir.Path)));
        Assert.True(File.Exists(Path.Combine(dir.Path, "LICENSE.txt")));
        Assert.Equal(2, Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).Length);   // dll + licence only
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "evil.dll")));
    }

    [Fact]
    public void Wintun_InstallFromZip_RejectsWrongZipOrDllHash_LeavesNothing()
    {
        var dll = "fake wintun"u8.ToArray();
        var zip = Zip(($"wintun/bin/{WintunInstall.Arch}/wintun.dll", dll));
        using var dir = new TempDir();
        var good = new Dictionary<string, string> { [WintunInstall.Arch] = Sha(dll) };
        var bad = new Dictionary<string, string> { [WintunInstall.Arch] = new string('0', 64) };

        Assert.Throws<InvalidDataException>(() => WintunInstall.InstallFromZip(zip, dir.Path, WintunInstall.ZipSha256, good, false));
        Assert.Throws<InvalidDataException>(() => WintunInstall.InstallFromZip(zip, dir.Path, Sha(zip), bad, false));
        Assert.False(File.Exists(WintunInstall.DllPath(dir.Path)));
        Assert.False(WintunInstall.IsInstalled(dir.Path));
    }

    [Fact]
    public void Wintun_Verify_RejectsUnpinnedFile_AndUnsignedFileHasNoSigner()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "wintun.dll");
        File.WriteAllBytes(path, [0x4d, 0x5a, 0, 0]);
        Assert.StartsWith("SHA-256 mismatch", WintunInstall.Verify(path));
        Assert.Null(Authenticode.SignerOf(path));
        Assert.Null(Authenticode.SignerOf(Path.Combine(dir.Path, "missing.dll")));
    }

    /// <summary>With tools/fetch-wintun.ps1's copy present (developer machines): the real DLL passes every check.</summary>
    [Fact]
    public void Wintun_Verify_AcceptsOfficialDll_WhenAvailable()
    {
        var real = Path.Combine(SetupTestsRepo.Root(), "vendor", "wintun", "bin", WintunInstall.Arch, "wintun.dll");
        if (!OperatingSystem.IsWindows() || !File.Exists(real)) return;
        Assert.Equal("WireGuard LLC", Authenticode.SignerOf(real));
        Assert.Null(WintunInstall.Verify(real));
    }

    // ---------------- driver package ----------------

    private static readonly (string Name, byte[] Data)[] DriverFiles =
    [
        (DriverPackage.InfName, "[Version]"u8.ToArray()),
        (DriverPackage.SysName, [0x4d, 0x5a]),
        (DriverPackage.CatName, [0x30, 0x82]),
    ];

    [Fact]
    public void DriverSource_ImportsZip_ByFileNameOnly()
    {
        using var dir = new TempDir();
        var zipPath = Path.Combine(dir.Path, "NitroDrop-driver-0.1.0.0.zip");
        File.WriteAllBytes(zipPath, Zip(DriverFiles.Select(f => ($"NitroDrop-driver/driver/{f.Name}", f.Data))
            .Append(("NitroDrop-driver/tools/adwifi-cli.exe", new byte[] { 1 })).Append(("../../x.inf", new byte[] { 2 })).ToArray()));
        var target = Path.Combine(dir.Path, "imported");

        Assert.Empty(DriverSource.Import(zipPath, target));
        Assert.True(DriverSource.IsComplete(target));
        Assert.Equal(3, Directory.GetFiles(target).Length);
        Assert.Equal(target, DriverSource.Find([Path.Combine(dir.Path, "nope"), target]));
    }

    [Fact]
    public void DriverSource_ImportsExtractedFolder_ViaInf_AndReportsMissing()
    {
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "extracted");
        Directory.CreateDirectory(src);
        foreach (var (name, data) in DriverFiles.Take(2)) File.WriteAllBytes(Path.Combine(src, name), data);
        var target = Path.Combine(dir.Path, "imported");

        Assert.Equal(new[] { DriverPackage.CatName }, DriverSource.Import(Path.Combine(src, DriverPackage.InfName), target));
        Assert.False(Directory.Exists(target));                         // incomplete: nothing copied
        Assert.Null(DriverSource.Find([src, target]));

        File.WriteAllBytes(Path.Combine(src, DriverPackage.CatName), DriverFiles[2].Data);
        Assert.Empty(DriverSource.Import(src, target));
        Assert.True(DriverSource.IsComplete(target));
    }
}

internal static class SetupTestsRepo
{
    public static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "driver", DriverPackage.InfName))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
