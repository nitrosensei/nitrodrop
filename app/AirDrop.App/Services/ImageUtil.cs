using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AirDrop.App.Services;

internal static class ImageUtil
{
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp" };

    public static bool IsImage(string path) =>
        Array.IndexOf(ImageExt, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Decodes to a small frozen bitmap; returns null if WIC can't decode (e.g. JPEG 2000 without codec).</summary>
    public static ImageSource? FromBytes(byte[]? data, int px = 192)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(data);
            return Decode(ms, px);
        }
        catch (Exception) { return null; }
    }

    public static ImageSource? FromFile(string? path, int px = 192)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return Decode(fs, px);
        }
        catch (Exception) { return null; }
    }

    private static ImageSource Decode(Stream s, int px)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.DecodePixelWidth = px;
        bi.StreamSource = s;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
