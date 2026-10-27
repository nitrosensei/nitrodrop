using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AirDrop.App.Interop;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private GlobalHotkey? _hotkey;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SourceInitialized += (_, _) =>
        {
            ApplyBackdrop();
            _hotkey = new GlobalHotkey(this, 0xA1D, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x41 /* A */, OnClipboardHotkey);
            if (!_hotkey.IsRegistered) _vm.Settings.HotkeyStatus = "Ctrl + Alt + A (başka uygulama kullanıyor)";
        };
        Closed += (_, _) => _hotkey?.Dispose();
        App.ThemeChanged += (_, _) => ApplyBackdrop();
        Loaded += (_, _) => StartPulse();
        Drop += Window_Drop;
    }

    private void ApplyBackdrop()
    {
        bool mica = WindowEffects.Apply(this, App.IsDark);
        if (mica) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "WindowFallback");
    }

    private void StartPulse()
    {
        var sb = (Storyboard)FindResource("PulseAnimation");
        sb.Begin(Pulse1, true);
        var second = sb.Clone();
        second.BeginTime = TimeSpan.FromSeconds(1.3);
        second.Begin(Pulse2, true);
    }

    // Closing hides to tray; quitting is done from the tray menu.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ---- drag & drop ----

    private static PeerViewModel? PeerOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PeerViewModel;

    private static void Highlight(object sender, bool on)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.FindName("dropRing") is Ellipse ring)
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(150)));
        if (fe.RenderTransform is ScaleTransform st)
        {
            var a = new DoubleAnimation(on ? 1.08 : 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    private void Peer_DragEnter(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) && PeerOf(sender) is { IsBusy: false };
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) Highlight(sender, true);
        e.Handled = true;
    }

    private void Peer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Peer_DragLeave(object sender, DragEventArgs e) => Highlight(sender, false);

    private async void Peer_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Highlight(sender, false);
        if (PeerOf(sender) is not { } peer) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await _vm.SendAsync(peer, files);
    }

    /// <summary>Files dropped anywhere else wait in the "choose a device" banner.</summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _vm.Tab = MainTab.Nearby;
            _vm.PendingFiles = files;
        }
    }

    private async void Peer_Click(object sender, MouseButtonEventArgs e)
    {
        if (PeerOf(sender) is not { IsBusy: false } peer) return;
        if (await _vm.TrySendPendingAsync(peer)) return;
        await PickAndSend(peer);
    }

    private async System.Threading.Tasks.Task PickAndSend(PeerViewModel peer)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"{peer.DisplayName} cihazına gönder", Multiselect = true };
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Any())
            await _vm.SendAsync(peer, dlg.FileNames);
    }

    // ---- peer context menu ----

    private static PeerViewModel? MenuPeer(object sender) => (sender as MenuItem)?.DataContext as PeerViewModel;

    private async void Menu_SendFiles(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { IsBusy: false } p) await PickAndSend(p);
    }

    private async void Menu_SendClipboard(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { IsBusy: false } p) await _vm.SendClipboardAsync(p);
    }

    private async void OnClipboardHotkey()
    {
        if (!await _vm.SendClipboardToDefaultAsync())
        {
            // No obvious target: open the window so the user can pick a device.
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            _vm.Tab = MainTab.Nearby;
        }
    }

    private void Menu_SetPhoto(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is not { } p) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"{p.DisplayName} için fotoğraf",
            Filter = "Resimler|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.heic;*.webp",
        };
        if (dlg.ShowDialog(this) == true) _vm.SetPeerPhoto(p, dlg.FileName);
    }

    private void Menu_ClearPhoto(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { } p) _vm.SetPeerPhoto(p, null);
    }

    private void Menu_Rename(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is not { } p) return;
        var name = TextPrompt.Show(this, "Yeniden Adlandır", $"{p.Name} için görünen ad:", p.DisplayName);
        if (name is not null) _vm.SetPeerAlias(p, name);
    }

    private void Menu_ToggleTrust(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { } p) _vm.ToggleTrusted(p);
    }

    private void Settings_Back(object sender, RoutedEventArgs e) => _vm.Tab = MainTab.Nearby;
}
