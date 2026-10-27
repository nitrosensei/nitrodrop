using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace AirDrop.Core.Plist;

/// <summary>
/// Minimal XML property list reader (dict, array, string, integer, real, true/false, data, date) producing the
/// same object model as <see cref="BinaryPlist"/>. DTDs are ignored, never resolved.
/// </summary>
public static class XmlPlist
{
    public static bool LooksLikeXml(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i < data.Length && data[i] == (byte)'<';
    }

    public static object Read(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
        using var ms = new MemoryStream(data);
        using var reader = XmlReader.Create(ms, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("Empty XML plist");
        var value = root.Name.LocalName == "plist" ? root.Elements().FirstOrDefault() : root;
        if (value == null) throw new InvalidDataException("XML plist has no value");
        return Parse(value);
    }

    public static Dictionary<string, object> ReadDictionary(byte[] data) =>
        Read(data) as Dictionary<string, object> ?? throw new InvalidDataException("XML plist root is not a dict");

    private static object Parse(XElement e) => e.Name.LocalName switch
    {
        "dict" => ParseDict(e),
        "array" => e.Elements().Select(Parse).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "real" => double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        "data" => Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c)))),
        "date" => DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => throw new InvalidDataException($"Unsupported XML plist element <{e.Name.LocalName}>"),
    };

    private static Dictionary<string, object> ParseDict(XElement e)
    {
        var d = new Dictionary<string, object>();
        string? key = null;
        foreach (var child in e.Elements())
        {
            if (child.Name.LocalName == "key") key = child.Value;
            else if (key != null)
            {
                d[key] = Parse(child);
                key = null;
            }
        }
        return d;
    }
}
