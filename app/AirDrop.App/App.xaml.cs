using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using AirDrop.App.Services;
using AirDrop.App.Setup;
using AirDrop.App.ViewModels;
using AirDrop.App.Views;
using Forms = System.Windows.Forms;

namespace AirDrop.App;

/// <remarks>
/// Command line: <c>--send &lt;files…&gt;</c> (Explorer "NitroDrop ile gönder"), <c>--background</c> (autostart, tray only),
/// <c>--setup</c> / <c>--setup-resume</c> (open the "Gelişmiş Kurulum" wizard), <c>--setup-dry-run</c> (the wizard logs
/// commands instead of running them), <c>--setup-helper …</c> (the elevated helper, see Setup/ElevatedHelper.cs).
/// </remarks>
public partial class App : Application
{
    private SingleInstance? _single;
    private Forms.NotifyIcon? _tray;
    private Action? _balloonClick;
    private IAirDropService? _svc;
    private ISyncSource? _sync;
    private MainWindow? _main;
    private MainViewModel? _vm;
    private SetupWizard? _wizard;

    public static bool IsDark { get; private set; }
    public static event EventHandler? ThemeChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated setup helper: one action, then exit (never joins the single-instance group, never shows UI).
        if (HelperProtocol.IsHelperInvocation(e.Args))
        {
            Shutdown(ElevatedHelper.HostMain(e.Args));
            return;
        }
        ElevatedHelper.DryRun = e.Args.Contains(ElevatedHelper.DryRunFlag);

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            SingleInstance.Forward(e.Args.Length > 0 ? e.Args : new[] { "--show" });
            Shutdown();
            return;
        }
        _single.ArgsReceived += args => Dispatcher.Invoke(() => HandleArgs(args));

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.Invoke(ApplyTheme);
        };

        var settings = AppSettings.Load();
        var history = HistoryStore.Load();
        // Real stack (LAN until the Wi-Fi driver provides AWDL); --demo / --snapshot use fake peers.
        bool demo = e.Args.Contains("--demo") || e.Args.Contains("--snapshot");
        IContactResolver contacts;
        if (demo)
        {
            _svc = new DemoAirDropService();
            _sync = new DemoSyncSource();
            contacts = new DemoContactResolver();
        }
        else
        {
            _svc = new CoreAirDropService();
            // Nitro.Sync: tabs/apps between paired NitroDrop PCs, browser extension bridge, iOS Shortcut receiver.
            // Same TLS identity as AirDrop (default pfx); name follows Ayarlar → Görünen ad.
            _sync = new NitroSyncSource();
            contacts = ContactBookResolver.OpenDefault();
        }

        _vm = new MainViewModel(_svc, _sync, settings, history, contacts, Dispatcher);
        _main = new MainWindow(_vm);
        _vm.ShowAskSheet = (req, contact, photo) => ReceiveWindow.AskAsync(_main, req, contact, photo);
        _vm.Notify = ShowBalloon;
        _vm.Devices.ShowPairSheet = (prompt, ct) => ConfirmSheet.PairAsync(_main, prompt, ct);
        _vm.Devices.ShowLaunchSheet = (req, ct) => ConfirmSheet.LaunchAsync(_main, req, ct);
        _vm.Devices.AskIphoneName = () => TextPrompt.Show(_main, "iPhone ekle", "Bu iPhone'a bir ad verin:", "iPhone'um");
        _vm.DriverSetup.OpenWizard = ShowSetupWizard;

#if DEBUG
        int snap = Array.IndexOf(e.Args, "--snapshot");
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            await Snapshot.RunAsync(_main, _vm, e.Args[snap + 1]);
            await _sync.DisposeAsync();
            Shutdown();
            return;
        }
#endif
        CreateTray();
        if (!e.Args.Contains("--background")) _main.Show();
        HandleArgs(e.Args);
        await _vm.StartAsync();
    }

    private void HandleArgs(string[] args)
    {
        if (_vm is null) return;
        int i = Array.IndexOf(args, "--send");
        if (i >= 0)
        {
            var files = args.Skip(i + 1).Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
            if (files.Length > 0)
            {
                _vm.Tab = MainTab.Nearby;
                _vm.PendingFiles = files;
            }
        }
        if (i >= 0 || args.Contains("--show")) ShowMain();
        if (args.Contains(ResumeRegistration.ResumeFlag) || args.Contains("--setup")) ShowSetupWizard(false);
    }

    private void ShowSetupWizard(bool rollback)
    {
        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }
        // Dry-run keeps its own progress file so a test run never looks like a real install.
        var statePath = ElevatedHelper.DryRun ? Path.ChangeExtension(SetupPaths.StateFile, ".dry-run.json") : SetupPaths.StateFile;
        _wizard = new SetupWizard(new SetupWizardViewModel(new SystemProbe(), statePath, rollback));
        if (_main is { IsVisible: true }) _wizard.Owner = _main;
        else _wizard.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _wizard.Closed += (_, _) =>
        {
            _wizard = null;
            _ = _vm?.DriverSetup.RefreshAsync();
        };
        _wizard.Show();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("NitroDrop'u Aç", null, (_, _) => ShowMain());
        menu.Items.Add("Cihazlar", null, (_, _) => { ShowMain(); if (_vm is not null) _vm.Tab = MainTab.Devices; });
        menu.Items.Add("Geçmiş", null, (_, _) => { ShowMain(); if (_vm is not null) _vm.Tab = MainTab.History; });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Quit());
        _tray = new Forms.NotifyIcon
        {
            Text = "NitroDrop",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    private void ShowBalloon(string title, string body, Action? onClick)
    {
        if (_tray is null) return;
        // When the window is in front the in-window card is enough.
        if (_main is { IsVisible: true, IsActive: true }) return;
        _balloonClick = onClick;
        _tray.ShowBalloonTip(4000, title, body, Forms.ToolTipIcon.None);
    }

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private async void Quit()
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_sync is not null) await _sync.DisposeAsync();
        if (_svc is not null) await _svc.DisposeAsync();
        _single?.Dispose();
        Shutdown();
    }

    private void ApplyTheme() => ApplyTheme(ReadSystemDark());

    /// <summary>Also used by the Debug snapshot to render both themes.</summary>
    internal void ApplyTheme(bool dark)
    {
        IsDark = dark;
        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative),
        };
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool ReadSystemDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
