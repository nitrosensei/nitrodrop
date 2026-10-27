using System.Diagnostics;
using System.IO.Compression;
using AirDrop.Core.Archive;
using AirDrop.Core.Plist;

namespace AirDrop.Tests;

/// <summary>Cross-checks against independent implementations when available (bsdtar/libarchive, CPython plistlib).</summary>
public class InteropTests
{
    private static string? Find(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static (int code, string output) Run(string exe, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, output);
    }

    [Fact]
    public async Task OurGzipCpio_IsReadableByLibarchive()
    {
        var tar = Find(@"C:\Windows\System32\tar.exe");
        if (tar == null) return; // bsdtar not present
        using var tmp = new TempDir();
        var archive = Path.Combine(tmp.Path, "upload.cpio.gz");
        await using (var fs = File.Create(archive))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        await using (var w = new CpioWriter(gz, leaveOpen: true))
        {
            await w.AddDirectoryAsync("./dir");
            var data = "libarchive ok"u8.ToArray();
            await w.AddFileAsync("./dir/file.txt", new MemoryStream(data), data.Length);
            await w.FinishAsync();
        }
        var outDir = Path.Combine(tmp.Path, "out");
        Directory.CreateDirectory(outDir);
        var (code, output) = Run(tar, $"-xzf \"{archive}\" -C \"{outDir}\"");
        Assert.True(code == 0, output);
        Assert.Equal("libarchive ok", File.ReadAllText(Path.Combine(outDir, "dir", "file.txt")));
        var (_, listing) = Run(tar, $"-tvf \"{archive}\"");
        Assert.Contains("dir/file.txt", listing);
    }

    [Fact]
    public void OurBplist_IsReadableByCPython()
    {
        var root = AppContext.BaseDirectory;
        string? py = null;
        for (var d = new DirectoryInfo(root); d != null && py == null; d = d.Parent)
            py = Find(Path.Combine(d.FullName, ".venv", "Scripts", "python.exe"));
        if (py == null) return; // no Python available
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "x.plist");
        File.WriteAllBytes(file, BinaryPlist.Write(new Dictionary<string, object>
        {
            ["SenderComputerName"] = "Gönderen ğüşİöç",
            ["ConvertMediaFormats"] = false,
            ["Files"] = new List<object> { new Dictionary<string, object> { ["FileName"] = "a.jpg", ["FileIsDirectory"] = false } },
            ["n"] = -5,
            ["d"] = new byte[] { 1, 2, 3 },
            ["t"] = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        }));
        var script = "import plistlib,sys,datetime;d=plistlib.load(open(sys.argv[1],'rb'));" +
                     "assert d['SenderComputerName']=='G\\u00f6nderen \\u011f\\u00fc\\u015f\\u0130\\u00f6\\u00e7';" +
                     "assert d['ConvertMediaFormats'] is False and d['n']==-5 and d['d']==b'\\x01\\x02\\x03';" +
                     "assert d['Files'][0]['FileName']=='a.jpg';" +
                     "assert d['t'].year==2020;print('OK')";
        var (code, output) = Run(py, $"-I -c \"{script}\" \"{file}\"");
        Assert.True(code == 0 && output.Contains("OK"), output);
    }
}
