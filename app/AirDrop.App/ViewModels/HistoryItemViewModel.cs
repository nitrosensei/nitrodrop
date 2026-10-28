using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using AirDrop.App.Localization;
using AirDrop.App.Services;

namespace AirDrop.App.ViewModels;

/// <summary>History row. Derives from Observable only for the language refresh (texts are computed).</summary>
public sealed class HistoryItemViewModel : Observable
{
    private readonly HistoryEntry _e;
    private ImageSource? _thumb;
    private bool _thumbLoaded;

    public HistoryItemViewModel(HistoryEntry e)
    {
        _e = e;
        Open = new RelayCommand(_ => OpenItem(), _ => CanOpen);
        Reveal = new RelayCommand(_ => RevealItem(), _ => _e.Paths.Any(File.Exists));
    }

    public bool IsReceived => _e.Direction == TransferDirection.Received;
    public string DirectionGlyph => IsReceived ? "" : "";   // download / upload
    public string DeviceName => _e.DeviceName;
    /// <summary>Device art of the other side (Themes/DeviceArt.xaml key).</summary>
    public string ArtKey => DeviceModels.ArtKey(_e.DeviceKind, _e.ModelName, _e.Host);
    /// <summary>"iPhone 15 Pro", "Acer Nitro V 15" (tooltip of the art).</summary>
    public string DeviceLabel => DeviceModels.Describe(_e.DeviceKind, _e.ModelName, _e.Host);

    public string Title
    {
        get
        {
            if (_e.Links.Count > 0 && _e.Paths.Count == 0) return _e.Links[0];
            if (_e.Paths.Count == 1) return Path.GetFileName(_e.Paths[0]);
            if (_e.Paths.Count > 1) return MainStrings.History_TitleMore_Plural(_e.Paths.Count - 1, Path.GetFileName(_e.Paths[0]));
            return HistoryNotes.Describe(_e.Note) ?? MainStrings.History_Transfer;
        }
    }

    public string Subtitle
    {
        get
        {
            string dir = IsReceived ? MainStrings.History_FromDevice(_e.DeviceName) : MainStrings.History_ToDevice(_e.DeviceName);
            string size = _e.TotalBytes > 0 ? " · " + Loc.Bytes(_e.TotalBytes) : "";
            string status = _e.Succeeded ? "" : " · " + (HistoryNotes.Describe(_e.Note) ?? MainStrings.History_NoteFailed);
            return $"{dir}{size}{status}";
        }
    }

    public string TimeText => Loc.When(_e.Time);

    public bool Failed => !_e.Succeeded;

    /// <summary>Lazy thumbnail of the first image in the batch.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbLoaded)
            {
                _thumbLoaded = true;
                var img = _e.Paths.FirstOrDefault(p => ImageUtil.IsImage(p) && File.Exists(p));
                _thumb = ImageUtil.FromFile(img, 96);
            }
            return _thumb;
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    // ---- wide layout preview pane (HistoryPage) ----

    private bool _isSelected;
    private ImageSource? _preview;
    private bool _previewLoaded;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Larger image for the preview pane, loaded on first use.</summary>
    public ImageSource? Preview
    {
        get
        {
            if (!_previewLoaded)
            {
                _previewLoaded = true;
                var img = _e.Paths.FirstOrDefault(p => ImageUtil.IsImage(p) && File.Exists(p));
                _preview = ImageUtil.FromFile(img, 640);
            }
            return _preview;
        }
    }

    public bool HasPreview => Preview is not null;

    /// <summary>File names and links of the batch, for the preview pane.</summary>
    public IReadOnlyList<string> Items => _e.Paths.Select(Path.GetFileName).OfType<string>().Concat(_e.Links).ToList();
    public string ItemsHeader => CommonStrings.Items_Plural(Items.Count);
    public bool HasItems => Items.Count > 1;

    /// <summary>Full date and time in the UI culture ("28 Ekim 2026 Çarşamba 14:05").</summary>
    public string FullTime => _e.Time.ToString("f", Loc.Culture);

    public string KindGlyph =>
        _e.Links.Count > 0 && _e.Paths.Count == 0 ? ""      // link
        : _e.Paths.Count > 1 ? ""                            // folder
        : "";                                                // document

    public RelayCommand Open { get; }
    public RelayCommand Reveal { get; }

    private bool CanOpen => _e.Links.Count > 0 || _e.Paths.Any(File.Exists);

    private void OpenItem()
    {
        var target = _e.Paths.FirstOrDefault(File.Exists) ?? _e.Links.FirstOrDefault();
        if (target is not null) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void RevealItem()
    {
        var p = _e.Paths.FirstOrDefault(File.Exists);
        if (p is not null) Process.Start("explorer.exe", $"/select,\"{p}\"");
    }
}

/// <summary>
/// <see cref="HistoryEntry.Note"/> is stored in history.json as a language-neutral code and translated when shown;
/// entries written before localization carry the old Turkish text, mapped here too.
/// </summary>
public static class HistoryNotes
{
    public const string Declined = "declined";
    public const string Failed = "failed";
    public const string Demo = "demo";

    public static string? Describe(string? note) => note switch
    {
        null => null,
        Declined or "reddedildi" => MainStrings.History_NoteDeclined,
        Failed or "ba\u015far\u0131s\u0131z" => MainStrings.History_NoteFailed,
        Demo or "demo \u2014 dosya yaz\u0131lmad\u0131" => MainStrings.History_NoteDemo,
        _ => note,
    };
}
