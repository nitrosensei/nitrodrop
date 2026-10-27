using AirDrop.Core.Plist;

namespace AirDrop.Tests;

public class PlistTests
{
    [Fact]
    public void RoundTrip_AllTypes()
    {
        var date = new DateTime(2026, 10, 28, 12, 34, 56, DateTimeKind.Utc);
        var root = new Dictionary<string, object>
        {
            ["ascii"] = "hello",
            ["unicode"] = "ğüşİöç Çay ☕",
            ["empty"] = "",
            ["int0"] = 0,
            ["int8"] = 200,
            ["int16"] = 60000,
            ["int32"] = 4_000_000_000L,
            ["int64"] = long.MaxValue,
            ["neg"] = -42,
            ["real"] = 3.14159,
            ["true"] = true,
            ["false"] = false,
            ["data"] = new byte[] { 0, 1, 2, 255 },
            ["bigdata"] = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray(),
            ["date"] = date,
            ["array"] = new List<object> { 1, "two", new List<object> { 3.0 }, new Dictionary<string, object> { ["k"] = "v" } },
            ["longstring"] = new string('x', 300),
        };
        var bytes = BinaryPlist.Write(root);
        Assert.True(BinaryPlist.IsBinaryPlist(bytes));

        var back = BinaryPlist.ReadDictionary(bytes);
        Assert.Equal("hello", back["ascii"]);
        Assert.Equal("ğüşİöç Çay ☕", back["unicode"]);
        Assert.Equal("", back["empty"]);
        Assert.Equal(0L, back["int0"]);
        Assert.Equal(200L, back["int8"]);
        Assert.Equal(60000L, back["int16"]);
        Assert.Equal(4_000_000_000L, back["int32"]);
        Assert.Equal(long.MaxValue, back["int64"]);
        Assert.Equal(-42L, back["neg"]);
        Assert.Equal(3.14159, (double)back["real"], 10);
        Assert.Equal(true, back["true"]);
        Assert.Equal(false, back["false"]);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, back["data"]);
        Assert.Equal(1000, ((byte[])back["bigdata"]).Length);
        Assert.Equal(date, ((DateTime)back["date"]).ToUniversalTime(), TimeSpan.FromMilliseconds(1));
        var arr = (List<object>)back["array"];
        Assert.Equal(1L, arr[0]);
        Assert.Equal("two", arr[1]);
        Assert.Equal(3.0, ((List<object>)arr[2])[0]);
        Assert.Equal("v", ((Dictionary<string, object>)arr[3])["k"]);
        Assert.Equal(300, ((string)back["longstring"]).Length);
    }

    [Fact]
    public void Reads_KnownAppleEncoding()
    {
        // CPython 3.14: plistlib.dumps({'a':1,'b':'x','u':'ğüş','arr':[True,2.5,b'\x01\x02']}, fmt=FMT_BINARY)
        var bytes = Convert.FromHexString(
            "62706c6973743030d40102030405060a0b516153617272516251751001a307080909234004000000000000420102517863011f00fc015f08111317191b1d21222b2e300000000000000101000000000000000c00000000000000000000000000000037");
        var d = BinaryPlist.ReadDictionary(bytes);
        Assert.Equal(1L, d["a"]);
        Assert.Equal("x", d["b"]);
        Assert.Equal("ğüş", d["u"]);
        var arr = (List<object>)d["arr"];
        Assert.Equal(true, arr[0]);
        Assert.Equal(2.5, arr[1]);
        Assert.Equal(new byte[] { 1, 2 }, arr[2]);
        // And CPython must be able to read ours: same structure written by us parses back identically.
        var ours = BinaryPlist.ReadDictionary(BinaryPlist.Write(d));
        Assert.Equal("ğüş", ours["u"]);
    }

    [Fact]
    public void ManyObjects_UsesWiderRefs()
    {
        var list = Enumerable.Range(0, 70000).Select(i => (object)("s" + i)).ToList();
        var back = (List<object>)BinaryPlist.Read(BinaryPlist.Write(list));
        Assert.Equal(70000, back.Count);
        Assert.Equal("s69999", back[^1]);
    }

    [Fact]
    public void Rejects_Garbage()
    {
        Assert.Throws<PlistException>(() => BinaryPlist.Read("not a plist at all, definitely not"u8));
        var bytes = BinaryPlist.Write(new Dictionary<string, object> { ["a"] = 1 });
        bytes[^1] = 0xFF; // corrupt offset table pointer
        Assert.Throws<PlistException>(() => BinaryPlist.Read(bytes));
    }
}
