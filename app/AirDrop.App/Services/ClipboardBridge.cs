using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AirDrop.App.Services;

/// <summary>What the Windows clipboard holds, turned into something AirDrop can carry.</summary>
public sealed record ClipboardPayload(IReadOnlyList<string> Files, Uri? Link, string Summary);

/// <summary>
/// Clipboard ⇄ AirDrop. Apple's Universal Clipboard needs iCloud keys, so instead the clipboard is
/// sent as a normal AirDrop (text → .txt, image → .png, link → URL) and incoming items are copied back.
/// Must be called on the UI (STA) thread.
/// </summary>
internal static class ClipboardBridge
{
    private const long MaxTextToClipboard = 1 << 20;
    private static string TempDir => Path.Combine(Path.GetTempPath(), "airdrop-win", "clipboard");

    public static ClipboardPayload? Read()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
                if (files.Count > 0)
                    return new(files, null, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} dosya");
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                var path = NewTemp("png");
                using (var fs = File.Create(path))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    enc.Save(fs);
                }
                return new(new[] { path }, null, "Panodaki görsel");
            }
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                var trimmed = text.Trim();
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !trimmed.Contains(' '))
                    return new(Array.Empty<string>(), uri, uri.Host);
                if (trimmed.Length == 0) return null;
                var path = NewTemp("txt");
                File.WriteAllText(path, text, new UTF8Encoding(true));   // BOM so iOS picks UTF-8
                var preview = trimmed.Length > 40 ? trimmed[..40] + "…" : trimmed;
                return new(new[] { path }, null, $"“{preview.ReplaceLineEndings(" ")}”");
            }
        }
        catch (Exception) { /* clipboard busy / locked by another app */ }
        return null;
    }

    /// <summary>Puts a received batch on the clipboard when it is a single text, image or link.</summary>
    public static string? Write(ReceivedBatch b)
    {
        try
        {
            if (b.Paths.Count == 0 && b.Links.Count > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, b.Links));
                return "Bağlantı panoya kopyalandı";
            }
            if (b.Paths.Count != 1 || !File.Exists(b.Paths[0])) return null;
            var p = b.Paths[0];
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext is ".txt" && new FileInfo(p).Length <= MaxTextToClipboard)
            {
                Clipboard.SetText(File.ReadAllText(p));
                return "Metin panoya kopyalandı";
            }
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(p);
                bi.EndInit();
                bi.Freeze();
                Clipboard.SetImage(bi);
                return "Görsel panoya kopyalandı";
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string NewTemp(string ext)
    {
        Directory.CreateDirectory(TempDir);
        foreach (var old in new DirectoryInfo(TempDir).EnumerateFiles().Where(f => f.CreationTimeUtc < DateTime.UtcNow.AddDays(-1)))
            try { old.Delete(); } catch (Exception) { }
        return Path.Combine(TempDir, $"Pano {DateTime.Now:yyyy-MM-dd HH.mm.ss}.{ext}");
    }
}
