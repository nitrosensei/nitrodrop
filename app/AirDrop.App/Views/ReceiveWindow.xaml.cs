using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AirDrop.App.Interop;
using AirDrop.App.Services;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

/// <summary>"X wants to share N items" sheet with Decline / Accept.</summary>
public partial class ReceiveWindow : Window
{
    private const int TimeoutSeconds = 60;
    private readonly TaskCompletionSource<bool> _result = new();
    private readonly DispatcherTimer _timer;
    private int _left = TimeoutSeconds;

    private ReceiveWindow(IncomingRequest req, ContactMatch? contact, ImageSource? photo)
    {
        InitializeComponent();

        string who = contact?.Name ?? PeerViewModel.OwnerOf(req.SenderName);
        Who.Text = who;
        string model = DeviceModels.ShortDescribe(req.SenderKind, req.SenderModelName, req.SenderHost);
        Device.Text = contact is null ? model : $"{req.SenderName} · {model}";
        Device.ToolTip = DeviceModels.Describe(req.SenderKind, req.SenderModelName, req.SenderHost);
        Initials.Text = PeerViewModel.MakeInitials(who);
        Art.ArtKey = DeviceModels.ArtKey(req.SenderKind, req.SenderModelName, req.SenderHost);
        if (photo is not null)
        {
            PhotoBrush.ImageSource = photo;
            Photo.Visibility = Visibility.Visible;
        }

        int files = req.Files.Count, links = req.Links.Count;
        if (files == 0 && links > 0)
        {
            Message.Text = links == 1 ? "sizinle bir bağlantı paylaşmak istiyor." : $"sizinle {links} bağlantı paylaşmak istiyor.";
            AcceptButton.Content = "Aç";
        }
        else
        {
            string what = files == 1 ? $"“{req.Files[0].Name}”" : $"{files} öge";
            long total = req.Files.Sum(f => f.Size);
            Message.Text = $"sizinle {what} paylaşmak istiyor" + (total > 0 ? $" ({Format.Size(total)})." : ".");
        }

        Items.ItemsSource = req.Files.Select(f => new { Glyph = GlyphFor(f.Name), f.Name, SizeText = f.Size > 0 ? Format.Size(f.Size) : "" })   // /Ask carries no sizes from iOS
            .Concat(req.Links.Select(l => new { Glyph = "", Name = l.ToString(), SizeText = "" }))
            .ToList();

        // FileIcon is usually JPEG 2000; shown only if a WIC codec can decode it.
        if (ImageUtil.FromBytes(req.Thumbnail, 600) is { } thumb)
        {
            PreviewBrush.ImageSource = thumb;
            Preview.Visibility = Visibility.Visible;
        }

        SourceInitialized += (_, _) =>
        {
            WindowEffects.ApplyThemed(this, transient: true);
        };
        Closed += (_, _) => _result.TrySetResult(false);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _left--;
            TimeoutText.Text = _left <= 15 ? $"{_left} sn içinde yanıt verilmezse reddedilir" : "";
            if (_left <= 0) Finish(false);
        };
        _timer.Start();
    }

    /// <summary>Debug snapshot: the sheet without showing or timing out.</summary>
    internal static ReceiveWindow ForPreview(IncomingRequest req, ContactMatch? contact, ImageSource? photo)
    {
        var w = new ReceiveWindow(req, contact, photo);
        w._timer.Stop();
        return w;
    }

    public static Task<bool> AskAsync(Window? owner, IncomingRequest req, ContactMatch? contact, ImageSource? photo)
    {
        var w = new ReceiveWindow(req, contact, photo);
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // Bottom-right like a notification when the main window is hidden.
            var wa = SystemParameters.WorkArea;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = wa.Right - w.Width - 16;
            w.Top = wa.Bottom - 520;
        }
        // Never steal keyboard focus: a stray Enter while typing elsewhere must not accept a transfer.
        w.ShowActivated = false;
        SystemSounds.Asterisk.Play();
        w.Show();
        return w._result.Task;
    }

    private static string GlyphFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".gif" or ".webp" => "",
        ".mov" or ".mp4" or ".m4v" or ".hevc" => "",
        ".mp3" or ".m4a" or ".wav" or ".aac" => "",
        ".pdf" => "",
        ".zip" or ".rar" or ".7z" => "",
        _ => "",
    };

    private void Finish(bool ok)
    {
        _timer.Stop();
        _result.TrySetResult(ok);
        Close();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Finish(true);
    private void Decline_Click(object sender, RoutedEventArgs e) => Finish(false);
}
