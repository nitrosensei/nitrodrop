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
/// Command line: <c>--send &lt;files…&gt;</c> (Explorer "NitroDrop ile gönder", Gönder menu), <c>--send-list &lt;file&gt;</c>
/// (MSIX IExplorerCommand with a long selection, see <see cref="LaunchArgs"/>), <c>--background</c> (autostart, tray only),
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
    private ContinuityHost? _phone;
    private AppSettings? _settings;
    private readonly SendBatcher _sendBatch = new();
    private readonly System.Collections.Generic.List<string> _deferredSend = new();

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

        // Off-screen snapshots (Debug) run beside a normal instance: no single-instance group, no tray, no network.
        bool snapshotRun = e.Args.Contains("--snapshot");
        _single = snapshotRun ? null : new SingleInstance();
        if (_single is { IsFirst: false })
        {
            SingleInstance.Forward(e.Args.Length > 0 ? e.Args : new[] { "--show" });
            Shutdown();
            return;
        }
        // Forwarded --send calls arrive in bursts (one process per selected file): batch them (SendBatcher), deliver on
        // the UI thread; until the window exists they wait in _deferredSend.
        _sendBatch.Flushed += (files, continues) => Dispatcher.BeginInvoke(() => DeliverSend(files, continues));
        QueueSend(e.Args);
        if (_single is not null) _single.ArgsReceived += args => Dispatcher.Invoke(() => { QueueSend(args); HandleArgs(args); });

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.Invoke(() => { ApplyTheme(); UpdateTrayIcon(); });
        };

        var settings = _settings = AppSettings.Load();
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
            // LAN node always; with our Wi-Fi driver bound, also the elevated radio host (NitroDrop.Radio.exe, one UAC
            // prompt) and an AWDL node on the "NitroDrop AWDL" Wintun interface (docs/awdl-port.md, Çalışma zamanı).
            _svc = new CoreAirDropService(new CoreAirDropServiceSettings
            {
                Log = AirDropLog,
                Radio = new RadioIntegration
                {
                    Launcher = new ElevatedRadioHostLauncher(),
                    DriverInstalled = () => SystemProbe.FindDevice() is { Present: true } d
                        && string.Equals(d.Service, DriverPackage.ServiceName, StringComparison.OrdinalIgnoreCase),
                    Enabled = settings.DirectIphoneConnection,
                    CreateBle = () => new Nitro.Awdl.Ble.BleAirDropAdvertiser(),
                },
            });
            // Nitro.Sync: tabs/apps between paired NitroDrop PCs, browser extension bridge, iOS Shortcut receiver.
            // Same TLS identity as AirDrop (default pfx); name follows Ayarlar → Görünen ad.
            _sync = new NitroSyncSource();
            contacts = ContactBookResolver.OpenDefault();
            // Explorer menu: re-point a moved portable folder / drop a portable leftover under MSIX (Services/ExplorerMenu.cs).
            ExplorerMenuIntegration.Default.StartupCheck();
        }

        _vm = new MainViewModel(_svc, _sync, settings, history, contacts, Dispatcher);
        // iPhone continuity ("Telefon" tab): real runtime or demo (docs/ui-integration-continuity.md).
        _phone = ContinuityHost.Create(demo, (contacts as ContactBookResolver)?.Book, Dispatcher,
                                       autoplay: e.Args.Contains("--demo"));
        _vm.Phone = _phone.ViewModel;
        _main = new MainWindow(_vm, settings);
        _phone.ShowPhoneTab = () => { if (_vm is not null) _vm.Tab = MainTab.Phone; ShowMain(); };
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
            await ContinuitySnapshots.RenderAsync(e.Args[snap + 1]);
            await _sync.DisposeAsync();
            Shutdown();
            return;
        }
#endif
        CreateTray();
        // Portable autostart passes --background (HKCU Run); the MSIX StartupTask passes nothing.
        bool background = e.Args.Contains("--background") || PackagedApp.LaunchedByStartupTask();
        if (!background) _main.Show();
        HandleArgs(e.Args);
        if (_deferredSend.Count > 0)
        {
            var early = _deferredSend.ToArray();
            _deferredSend.Clear();
            DeliverSend(early, continues: false);
        }
        if (!settings.OnboardingCompleted && !background) ShowOnboarding();
        await _vm.StartAsync();
        await _phone.StartAsync();
    }

    private static readonly object AirDropLogGate = new();

    /// <summary>AirDrop stack + radio host log: %LOCALAPPDATA%\airdrop-win\logs\airdrop.log (rolled at 4 MB). Never throws.</summary>
    private static void AirDropLog(string line)
    {
        lock (AirDropLogGate)
        {
            try
            {
                var dir = Path.Combine(AppSettings.DataDir, "logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "airdrop.log");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }

    /// <summary>--send / --send-list paths into the batcher (works before the window exists).</summary>
    private void QueueSend(string[] args)
    {
        var req = LaunchArgs.ParseSend(args);
        if (req.Requested) _sendBatch.Add(req.Paths);
    }

    /// <summary>One debounced batch: replaces the pending files, or extends them when it continues the previous batch.</summary>
    private void DeliverSend(System.Collections.Generic.IReadOnlyList<string> files, bool continues)
    {
        if (_vm is null)
        {
            _deferredSend.AddRange(files);
            return;
        }
        var merged = continues
            ? _vm.PendingFiles.Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : files.ToArray();
        if (merged.Length > 0)
        {
            _vm.Tab = MainTab.Nearby;
            _vm.PendingFiles = merged;
        }
        ShowMain();
    }

    private void HandleArgs(string[] args)
    {
        if (_vm is null) return;
        if (args.Contains("--show")) ShowMain();
        if (args.Contains(ResumeRegistration.ResumeFlag) || args.Contains("--setup")) ShowSetupWizard(false);
    }

    private OnboardingWindow? _onboarding;

    /// <summary>"İlk kullanım" intro: once on first launch, again from Ayarlar → Hakkında.</summary>
    internal void ShowOnboarding()
    {
        if (_vm is null || _settings is null || _main is null) return;
        if (_onboarding is not null) { _onboarding.Activate(); return; }
        _onboarding = new OnboardingWindow(_vm, _settings);
        if (_main.IsVisible) _onboarding.Owner = _main;
        else _onboarding.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _onboarding.Closed += (_, _) => _onboarding = null;
        _onboarding.Show();
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
            ContextMenuStrip = menu,
        };
        UpdateTrayIcon();
        _tray.Visible = true;
        _tray.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => _balloonClick?.Invoke();
    }

    private System.Drawing.Icon? _trayIcon;

    /// <summary>
    /// Monochrome tray mark that follows the taskbar (not the app) theme: dark drawing on a light taskbar
    /// (tray-light.ico), white drawing on a dark one (tray-dark.ico). Called again on every theme change.
    /// </summary>
    private void UpdateTrayIcon()
    {
        if (_tray is null) return;
        var name = TaskbarUsesLightTheme() ? "tray-light.ico" : "tray-dark.ico";
        try
        {
            var res = GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
            if (res is null) return;
            using var s = res.Stream;
            var icon = new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
            _tray.Icon = icon;
            _trayIcon?.Dispose();
            _trayIcon = icon;
        }
        catch (Exception) { _tray.Icon ??= System.Drawing.SystemIcons.Application; }
    }

    internal static bool TaskbarUsesLightTheme()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
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
        _main?.SaveBounds();
        if (_sync is not null) await _sync.DisposeAsync();
        if (_phone is not null) await _phone.DisposeAsync();
        if (_svc is not null) await _svc.DisposeAsync();
        _single?.Dispose();
        _sendBatch.Dispose();
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
