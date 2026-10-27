using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using AirDrop.App.Services;

namespace AirDrop.App.ViewModels;

public sealed class HistoryItemViewModel
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

    public string Title
    {
        get
        {
            if (_e.Links.Count > 0 && _e.Paths.Count == 0) return _e.Links[0];
            if (_e.Paths.Count == 1) return Path.GetFileName(_e.Paths[0]);
            if (_e.Paths.Count > 1) return $"{Path.GetFileName(_e.Paths[0])} ve {_e.Paths.Count - 1} öge daha";
            return _e.Note ?? "Aktarım";
        }
    }

    public string Subtitle
    {
        get
        {
            string dir = IsReceived ? $"{_e.DeviceName} cihazından" : $"{_e.DeviceName} cihazına";
            string size = _e.TotalBytes > 0 ? " · " + Format.Size(_e.TotalBytes) : "";
            string status = _e.Succeeded ? "" : " · " + (_e.Note ?? "başarısız");
            return $"{dir}{size}{status}";
        }
    }

    public string TimeText
    {
        get
        {
            var t = _e.Time;
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "Dün";
            return t.ToString("d MMM", new System.Globalization.CultureInfo("tr-TR"));
        }
    }

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
