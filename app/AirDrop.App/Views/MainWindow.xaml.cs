using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AirDrop.App.Localization;
using AirDrop.App.Interop;
using AirDrop.App.Services;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings? _settings;
    private GlobalHotkey? _hotkey;

    public MainWindow(MainViewModel vm, AppSettings? settings = null)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        RestoreSavedBounds(settings);
        SourceInitialized += (_, _) =>
        {
            ApplyBackdrop();
            _hotkey = new GlobalHotkey(this, 0xA1D, GlobalHotkey.MOD_CONTROL | GlobalHotkey.MOD_ALT, 0x41 /* A */, OnClipboardHotkey);
            if (!_hotkey.IsRegistered) _vm.Settings.HotkeyStatus = MainStrings.Main_HotkeyBusy;
        };
        Closed += (_, _) => _hotkey?.Dispose();
        App.ThemeChanged += (_, _) => ApplyBackdrop();
        Drop += Window_Drop;
        SizeChanged += (_, _) => ApplyLayout();
        StateChanged += (_, _) => ApplyMaximizedInset();
        ApplyLayout();
    }

    // ---- responsive shell (Views/Responsive.cs) ----

    /// <summary>Below this width the segmented tabs leave the title bar for their own row.</summary>
    private const double NarrowMax = 600;

    private LayoutMode _mode = (LayoutMode)(-1);
    private bool _narrow;

    public LayoutMode LayoutMode => _mode;

    /// <summary>
    /// Re-arranges the shell only when a breakpoint is crossed (Responsive.For has a small hysteresis), so a live
    /// resize does nothing but the normal measure/arrange in between.
    /// </summary>
    private void ApplyLayout()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        var mode = Responsive.For(width, _mode < 0 ? LayoutMode.Compact : _mode);
        bool narrow = mode == LayoutMode.Compact && width < NarrowMax;
        if (mode == _mode && narrow == _narrow) return;
        _mode = mode;
        _narrow = narrow;

        Responsive.SetMode(this, mode);
        bool sidebar = mode != LayoutMode.Compact;
        Sidebar.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.Visibility = sidebar ? Visibility.Collapsed : Visibility.Visible;

        // the segmented control lives in exactly one host
        if (narrow && Segments.Parent != NarrowTabsHost)
        {
            TitleTabsHost.Child = null;
            NarrowTabsHost.Child = Segments;
        }
        else if (!narrow && Segments.Parent != TitleTabsHost)
        {
            NarrowTabsHost.Child = null;
            TitleTabsHost.Child = Segments;
        }
        NarrowTabsHost.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// With WindowChrome a maximized window extends past the monitor by the resize frame; inset the content by that
    /// much so nothing is cut off at the screen edges.
    /// </summary>
    private void ApplyMaximizedInset()
    {
        if (WindowState == WindowState.Maximized)
        {
            var f = SystemParameters.WindowResizeBorderThickness;
            double pad = SystemParameters.WindowNonClientFrameThickness.Left > 0 ? 4 : 0;   // SM_CXPADDEDBORDER
            Shell.Margin = new Thickness(f.Left + pad, f.Top + pad, f.Right + pad, f.Bottom + pad);
        }
        else Shell.Margin = new Thickness(0);
    }

    private void ApplyBackdrop()
    {
        WindowEffects.ApplyThemed(this);
    }

    // Closing hides to tray; quitting is done from the tray menu.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        SaveBounds();
        Hide();
    }

    // ---- window size / position (per user, AppSettings) ----

    private void RestoreSavedBounds(AppSettings? s)
    {
        var wa = SystemParameters.WorkArea;
        if (s?.WindowWidth is not { } w || s.WindowHeight is not { } h)
        {
            // First run: the XAML default (1040×720) unless the screen is smaller; keep a margin around it.
            Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, wa.Width - 48));
            Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, wa.Height - 48));
            return;
        }
        // Clamp to the virtual desktop's work area so a removed monitor never leaves the window off-screen.
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, wa.Width));
        Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, wa.Height));
        if (s.WindowLeft is { } l && s.WindowTop is { } t &&
            l + 80 < vl + vw && l + Width - 80 > vl && t >= vt && t + 40 < vt + vh)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = Math.Min(t, wa.Bottom - 60);
        }
    }

    /// <summary>Persists the normal-state bounds (called on hide and on quit).</summary>
    internal void SaveBounds()
    {
        if (_settings is null || !IsLoaded || Left < -10000) return;   // off-screen snapshot windows never save
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (r.IsEmpty || r.Width < 100) return;
        _settings.WindowLeft = r.Left;
        _settings.WindowTop = r.Top;
        _settings.WindowWidth = r.Width;
        _settings.WindowHeight = r.Height;
        try { _settings.Save(); } catch (Exception) { /* settings dir unavailable */ }
    }

    // ---- page access (Debug snapshot) ----

    internal FrameworkElement SettingsPage => SettingsView;
    internal ScrollViewer SettingsScroll => SettingsView.ContentScroll;
    internal FrameworkElement AboutSection => SettingsView.AboutSection;
    internal FrameworkElement SyncSettings => SettingsView.SyncSettings;
    internal ScrollViewer DevicesScroll => DevicesView.DevicesScroll;

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

}
