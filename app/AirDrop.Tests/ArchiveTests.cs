using System.IO.Compression;
using System.Text;
using AirDrop.Core.Archive;

namespace AirDrop.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airdrop-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public class ArchiveTests
{
    private static async Task<byte[]> BuildArchive(Func<CpioWriter, Task> fill)
    {
        var ms = new MemoryStream();
        await using (var w = new CpioWriter(ms, leaveOpen: true))
        {
            await fill(w);
            await w.FinishAsync();
        }
        return ms.ToArray();
    }

    private static Task AddBytes(CpioWriter w, string name, byte[] data) =>
        w.AddFileAsync(name, new MemoryStream(data), data.Length);

    [Fact]
    public async Task Cpio_RoundTrip()
    {
        var big = new byte[300_000];
        new Random(1).NextBytes(big);
        var bytes = await BuildArchive(async w =>
        {
            await w.AddDirectoryAsync("./dir");
            await AddBytes(w, "./dir/a.txt", "hello"u8.ToArray());
            await AddBytes(w, "./ğüşİöç.bin", big);
            await AddBytes(w, "./empty", []);
        });
        Assert.Equal("070707", Encoding.ASCII.GetString(bytes, 0, 6));

        var reader = new CpioReader(new MemoryStream(bytes));
        var e1 = await reader.NextAsync();
        Assert.Equal("./dir", e1!.Name);
        Assert.Equal(CpioEntryType.Directory, e1.Type);
        var e2 = await reader.NextAsync();
        Assert.Equal("./dir/a.txt", e2!.Name);
        Assert.Equal("hello", await new StreamReader(e2.Data).ReadToEndAsync());
        var e3 = await reader.NextAsync();
        Assert.Equal("./ğüşİöç.bin", e3!.Name);
        Assert.Equal(CpioEntryType.File, e3.Type);
        var ms = new MemoryStream();
        await e3.Data.CopyToAsync(ms);
        Assert.Equal(big, ms.ToArray());
        var e4 = await reader.NextAsync();
        Assert.Equal(0, e4!.Size);
        Assert.Null(await reader.NextAsync());
    }

    [Fact]
    public async Task Cpio_SkipsUnreadData()
    {
        var bytes = await BuildArchive(async w =>
        {
            await AddBytes(w, "./a", new byte[1000]);
            await AddBytes(w, "./b", "b"u8.ToArray());
        });
        var reader = new CpioReader(new MemoryStream(bytes));
        await reader.NextAsync();
        var b = await reader.NextAsync();
        Assert.Equal("./b", b!.Name);
    }

    [Fact]
    public async Task Cpio_ReadsNewc()
    {
        // Hand-built newc entry "./x" with 3 bytes, then trailer.
        static string Hdr(string name, int mode, int size) =>
            "070701" + string.Concat(new[] { 1, mode, 0, 0, 1, 0, size, 0, 0, 0, 0, name.Length + 1, 0 }.Select(v => v.ToString("x8")));
        var ms = new MemoryStream();
        void Put(string name, int mode, byte[] data)
        {
            var h = Encoding.ASCII.GetBytes(Hdr(name, mode, data.Length) + name + "\0");
            ms.Write(h);
            ms.Write(new byte[(4 - h.Length % 4) % 4]);
            ms.Write(data);
            ms.Write(new byte[(4 - data.Length % 4) % 4]);
        }
        Put("./x", CpioMode.DefaultFile, "abc"u8.ToArray());
        Put("TRAILER!!!", 0, []);
        ms.Position = 0;
        var reader = new CpioReader(ms);
        var e = await reader.NextAsync();
        Assert.Equal("./x", e!.Name);
        Assert.Equal("abc", await new StreamReader(e.Data).ReadToEndAsync());
        Assert.Null(await reader.NextAsync());
    }

    [Fact]
    public async Task Extract_GzipCpio_WithTurkishNames()
    {
        using var tmp = new TempDir();
        var cpio = await BuildArchive(async w =>
        {
            await w.AddDirectoryAsync("./Klasör");
            await AddBytes(w, "./Klasör/içerik.txt", "çay"u8.ToArray());
            await AddBytes(w, "./ğüşİöç.txt", "merhaba"u8.ToArray());
        });
        var gz = new MemoryStream();
        await using (var g = new GZipStream(gz, CompressionLevel.Fastest, true)) g.Write(cpio);
        gz.Position = 0;

        var ex = new SafeExtractor(tmp.Path);
        var top = await ex.ExtractAsync(new GZipStream(gz, CompressionMode.Decompress));
        Assert.Equal(2, top.Count);
        Assert.Equal("çay", File.ReadAllText(Path.Combine(tmp.Path, "Klasör", "içerik.txt")));
        Assert.Equal("merhaba", File.ReadAllText(Path.Combine(tmp.Path, "ğüşİöç.txt")));
    }

    [Fact]
    public async Task Extract_RenamesOnCollision()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "a.txt"), "old");
        var cpio = await BuildArchive(w => AddBytes(w, "./a.txt", "new"u8.ToArray()));
        await new SafeExtractor(tmp.Path).ExtractAsync(new MemoryStream(cpio));
        Assert.Equal("old", File.ReadAllText(Path.Combine(tmp.Path, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(tmp.Path, "a (1).txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("./../evil.txt")]
    [InlineData("dir/../../evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("c:evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("dir\\..\\..\\evil.txt")]
    [InlineData("..")]
    public async Task Extract_RejectsTraversal(string name)
    {
        using var tmp = new TempDir();
        var inner = Path.Combine(tmp.Path, "inner");
        var cpio = await BuildArchive(w => AddBytes(w, name, "x"u8.ToArray()));
        await Assert.ThrowsAsync<UnsafeArchiveException>(() => new SafeExtractor(inner).ExtractAsync(new MemoryStream(cpio)));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Extract_RejectsSymlink()
    {
        using var tmp = new TempDir();
        // Symlink entry: mode 0120777, data = link target.
        var ms = new MemoryStream();
        var target = "../../outside"u8.ToArray();
        var name = "./link";
        var hdr = $"070707{0:D6}{1:D6}{Convert.ToString(0xA000 | 0x1FF, 8).PadLeft(6, '0')}{0:D6}{0:D6}{1:D6}{0:D6}{0:D11}{Convert.ToString(name.Length + 1, 8).PadLeft(6, '0')}{Convert.ToString(target.Length, 8).PadLeft(11, '0')}";
        ms.Write(Encoding.ASCII.GetBytes(hdr + name + "\0"));
        ms.Write(target);
        ms.Position = 0;
        var ex = await Assert.ThrowsAsync<UnsafeArchiveException>(() => new SafeExtractor(tmp.Path).ExtractAsync(ms));
        Assert.Contains("Symbolic", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(tmp.Path));
    }

    [Theory]
    [InlineData("./a:b.txt", "a_b.txt")]
    [InlineData("./CON.txt", "_CON.txt")]
    [InlineData("./trail. ", "trail")]
    [InlineData("./x/./y", "x|y")]
    public void SplitSafe_Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, string.Join('|', SafeExtractor.SplitSafe(input)));
    }
}
