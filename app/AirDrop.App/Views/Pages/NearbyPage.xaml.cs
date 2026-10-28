using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AirDrop.App.Localization;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views.Pages;

/// <summary>"Yakındakiler": radar with nearby peers; drop files on a peer or click it to pick files.</summary>
public partial class NearbyPage : UserControl
{
    public NearbyPage()
    {
        InitializeComponent();
        Loaded += (_, _) => StartPulse();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private Window? Owner => Window.GetWindow(this);

    private bool _pulsing;

    private void StartPulse()
    {
        if (_pulsing) return;
        _pulsing = true;
        var sb = (Storyboard)FindResource("PulseAnimation");
        sb.Begin(Pulse1, true);
        var second = sb.Clone();
        second.BeginTime = TimeSpan.FromSeconds(1.7);
        second.Begin(Pulse2, true);
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
        if (PeerOf(sender) is not { } peer || Vm is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            await vm.SendAsync(peer, files);
    }

    private async void Peer_Click(object sender, MouseButtonEventArgs e)
    {
        if (PeerOf(sender) is not { IsBusy: false } peer || Vm is not { } vm) return;
        if (await vm.TrySendPendingAsync(peer)) return;
        await PickAndSend(peer);
    }

    private async System.Threading.Tasks.Task PickAndSend(PeerViewModel peer)
    {
        if (Vm is not { } vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = MainStrings.Nearby_PickFilesTitle(peer.DisplayName), Multiselect = true };
        if (dlg.ShowDialog(Owner) == true && dlg.FileNames.Any())
            await vm.SendAsync(peer, dlg.FileNames);
    }

    // ---- peer context menu ----

    private static PeerViewModel? MenuPeer(object sender) => (sender as MenuItem)?.DataContext as PeerViewModel;

    private async void Menu_SendFiles(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { IsBusy: false } p) await PickAndSend(p);
    }

    private async void Menu_SendClipboard(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { IsBusy: false } p && Vm is { } vm) await vm.SendClipboardAsync(p);
    }

    private void Menu_SetPhoto(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is not { } p || Vm is not { } vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = MainStrings.Nearby_PhotoTitle(p.DisplayName),
            Filter = MainStrings.Nearby_ImageFilter + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.heic;*.webp",
        };
        if (dlg.ShowDialog(Owner) == true) vm.SetPeerPhoto(p, dlg.FileName);
    }

    private void Menu_ClearPhoto(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { } p) Vm?.SetPeerPhoto(p, null);
    }

    private void Menu_Rename(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is not { } p || Vm is not { } vm || Owner is not { } owner) return;
        var name = TextPrompt.Show(owner, MainStrings.Prompt_RenameTitle, MainStrings.Prompt_RenameLabel(p.Name), p.DisplayName);
        if (name is not null) vm.SetPeerAlias(p, name);
    }

    private void Menu_ToggleTrust(object sender, RoutedEventArgs e)
    {
        if (MenuPeer(sender) is { } p) Vm?.ToggleTrusted(p);
    }
}
