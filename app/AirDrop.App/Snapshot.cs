#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirDrop.App.Setup;
using AirDrop.App.ViewModels;
using AirDrop.App.Views;

namespace AirDrop.App;

/// <summary>
/// Debug-only: renders each tab off-screen to PNGs without stealing focus
/// (<c>AirDrop.exe --snapshot &lt;dir&gt;</c>). Used for UI review.
/// </summary>
internal static class Snapshot
{
    public static async Task RunAsync(MainWindow w, MainViewModel vm, string dir)
    {
        Directory.CreateDirectory(dir);
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.Width = 680;     // default size, whatever the user's saved bounds are
        w.Height = 760;
        w.Show();
        await vm.StartAsync();

        await Task.Delay(6500);                 // demo peers appear
        if (SnapshotMatrix.Requested)
        {
            // --matrix: breakpoints × themes × languages only (SnapshotMatrix.cs).
            await SnapshotMatrix.RunAsync(w, vm, dir);
            return;
        }
        await BrandAsync(w, vm, dir);
        Save(w, Path.Combine(dir, "nearby.png"));
        vm.Tab = MainTab.History;
        await Task.Delay(400);
        Save(w, Path.Combine(dir, "history.png"));
        vm.Tab = MainTab.Settings;
        await Task.Delay(400);
        Save(w, Path.Combine(dir, "settings.png"));
        if (w.SettingsScroll is { } sv)
        {
            sv.ScrollToEnd();
            await Task.Delay(400);
            Save(w, Path.Combine(dir, "settings-bottom.png"));
            sv.ScrollToHome();
        }
        await SyncAsync(w, vm, dir);
        vm.Tab = MainTab.Nearby;
        await SetupWizardAsync(dir);
        if (ElevatedHelper.DryRun) await SetupDryRunAsync(dir);
    }

    /// <summary>
    /// Main pages in both themes (nearby-light/dark with the radar and peers, history, settings top, settings-about),
    /// the receive sheet, the text prompt and a sample context menu + tooltip (popups rendered in a host window).
    /// </summary>
    private static async Task BrandAsync(MainWindow w, MainViewModel vm, string dir)
    {
        var app = (App)Application.Current;
        bool wasDark = App.IsDark;
        foreach (bool dark in new[] { false, true })
        {
            string t = dark ? "dark" : "light";
            app.ApplyTheme(dark);
            vm.Tab = MainTab.Nearby;
            await Task.Delay(500);
            Save(w, Path.Combine(dir, $"nearby-{t}.png"));
            vm.Tab = MainTab.History;
            await Task.Delay(300);
            Save(w, Path.Combine(dir, $"history-{t}.png"));
            vm.PreviewHistory(SampleHistory());
            await Task.Delay(300);
            Save(w, Path.Combine(dir, $"history-models-{t}.png"));
            vm.PreviewHistory(null);
            vm.Tab = MainTab.Phone;
            await Task.Delay(400);
            Save(w, Path.Combine(dir, $"phone-main-{t}.png"));
            vm.Tab = MainTab.Settings;
            await Task.Delay(300);
            if (w.SettingsScroll is { } sv) sv.ScrollToHome();
            await Task.Delay(200);
            Save(w, Path.Combine(dir, $"settings-{t}.png"));
            await ScrollToAsync(w, w.AboutSection);
            Save(w, Path.Combine(dir, $"settings-about-{t}.png"));

            var settings = new Services.AppSettings { DeviceName = vm.ComputerName, OnboardingCompleted = true };
            var ob = new OnboardingWindow(vm, settings)
            {
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000,
            };
            ob.Show();
            for (int i = 0; i < 6; i++)
            {
                ob.GoTo(i, animate: false);
                await Task.Delay(250);
                Save(ob, Path.Combine(dir, $"onboarding-{i + 1}-{t}.png"));
            }
            ob.Close();

            var req = new Services.IncomingRequest("Emre'nin iPhone'u", Services.DeviceKind.iPhone, "iPhone16,1",
                new[] { new Services.IncomingFile("IMG_4821.HEIC", 3_481_000), new Services.IncomingFile("IMG_4822.HEIC", 2_910_000),
                        new Services.IncomingFile("Sunum.pdf", 1_204_000) },
                Array.Empty<Uri>(), null);
            var rw = ReceiveWindow.ForPreview(req, new Services.ContactMatch("Emre Can Temur", null), null);
            await ShowOffscreenAsync(rw, Path.Combine(dir, $"sheet-receive-{t}.png"));
            var pcReq = new Services.IncomingRequest("Mert'in PC'si", Services.DeviceKind.Windows, "MacBookPro18,3",
                new[] { new Services.IncomingFile("Rapor.xlsx", 0) }, Array.Empty<Uri>(), null, null, Services.DemoAirDropService.DemoPc);
            await ShowOffscreenAsync(ReceiveWindow.ForPreview(pcReq, null, null), Path.Combine(dir, $"sheet-receive-pc-{t}.png"));
            RenderLoose(new FrameworkElement[] { DeviceArtGallery() }, Path.Combine(dir, $"deviceart-{t}.png"));

            // ContextMenu / ToolTip can't live in a window's tree: measure them loose and render directly.
            var menu = new ContextMenu();
            foreach (var h in new object[] { "Dosya gönder…", "Panoyu gönder   Ctrl+Alt+A", new Separator(),
                                             "Fotoğraf ata…", "Yeniden adlandır…", new Separator() })
                menu.Items.Add(h is string s1 ? new MenuItem { Header = s1 } : h);
            menu.Items.Add(new MenuItem { Header = "Güvenilir cihaz (sormadan kabul et)", IsChecked = true });
            var tip = new ToolTip { Content = "Güvenilir cihaz: sormadan kabul edilir" };
            RenderLoose(new FrameworkElement[] { menu, tip }, Path.Combine(dir, $"menu-{t}.png"));
        }
        app.ApplyTheme(wasDark);
        vm.Tab = MainTab.Nearby;
    }

    /// <summary>Every device art key at badge (17 px) and large (40 px) size, with its key — for reviewing Themes/DeviceArt.xaml.</summary>
    private static FrameworkElement DeviceArtGallery()
    {
        var panel = new WrapPanel { Width = 380 };
        foreach (var key in Services.DeviceArtKeys.All)
        {
            var cell = new StackPanel { Width = 92, Margin = new Thickness(0, 0, 0, 12) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var big = new Controls.DeviceArtIcon { ArtKey = key, Width = 40, Height = 40 };
            big.SetResourceReference(Controls.DeviceArtIcon.StrokeProperty, "TextPrimary");
            var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            badge.SetResourceReference(Border.BackgroundProperty, "Badge");
            badge.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var small = new Controls.DeviceArtIcon { ArtKey = key, Width = 17, Height = 17 };
            small.SetResourceReference(Controls.DeviceArtIcon.StrokeProperty, "TextSecondary");
            badge.Child = small;
            row.Children.Add(big);
            row.Children.Add(badge);
            cell.Children.Add(row);
            var label = new TextBlock { Text = key, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            cell.Children.Add(label);
            panel.Children.Add(cell);
        }
        return panel;
    }

    private static void RenderLoose(FrameworkElement[] items, string path)
    {
        double y = 0, wmax = 0;
        var placed = new System.Collections.Generic.List<(FrameworkElement, Rect)>();
        foreach (var fe in items)
        {
            fe.ApplyTemplate();
            fe.Measure(new Size(400, 600));
            var r = new Rect(0, y, fe.DesiredSize.Width, fe.DesiredSize.Height);
            fe.Arrange(r);
            fe.UpdateLayout();
            placed.Add((fe, r));
            y += r.Height;
            wmax = Math.Max(wmax, r.Width);
        }
        const double pad = 16, scale = 1.5;
        var rtb = new RenderTargetBitmap((int)((wmax + 2 * pad) * scale), (int)((y + 2 * pad) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["WindowFallback"], null, new Rect(0, 0, wmax + 2 * pad, y + 2 * pad));
            foreach (var (fe, r) in placed)
                dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, r.Width, r.Height), AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(pad, pad + r.Y, r.Width, r.Height));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static async Task ShowOffscreenAsync(Window win, string path)
    {
        win.ShowActivated = false;
        win.ShowInTaskbar = false;
        win.Topmost = false;
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;
        win.Top = -20000;
        win.Show();
        await Task.Delay(350);
        Save(win, path);
        win.Close();
    }

    /// <summary>
    /// <c>--snapshot &lt;dir&gt; --setup-dry-run</c>: runs every elevated action through ElevatedHelper in dry-run
    /// (in-process, no UAC, nothing executed) and writes the log to &lt;dir&gt;\setup-dry-run.log.
    /// </summary>
    private static async Task SetupDryRunAsync(string dir)
    {
        var log = Path.Combine(dir, "setup-dry-run.log");
        File.Delete(log);
        var savedLog = SetupLog.FilePath;
        SetupLog.FilePath = log;
        try
        {
            // Package as the wizard assembles it: bundled inf/sys/cat + the firmware (from the repo when present).
            var pkg = Path.Combine(dir, "dry-run-pkg");
            Directory.CreateDirectory(pkg);
            var driverDir = DriverSource.Find() ?? SetupPaths.BundledDriverDir;
            foreach (var f in DriverPackage.BundledFiles)
            {
                var src = Path.Combine(driverDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(pkg, f), overwrite: true);
            }
            var fwDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (fwDir is not null && !Directory.Exists(Path.Combine(fwDir.FullName, "driver", "firmware"))) fwDir = fwDir.Parent;
            if (fwDir is not null)
            {
                var fw = Path.Combine(fwDir.FullName, "driver", "firmware", FirmwareSpec.Firmware.Name);
                if (File.Exists(fw)) File.Copy(fw, Path.Combine(pkg, FirmwareSpec.Firmware.Name), overwrite: true);
            }

            var hw = SupportedDevices.All[0].HardwareId;
            var requests = new[]
            {
                new HelperRequest { Action = HelperAction.BitLockerStatus },
                new HelperRequest { Action = HelperAction.RebootToFirmware, SuspendBitLocker = true },
                new HelperRequest { Action = HelperAction.TestSigningOn, SuspendBitLocker = true },
                new HelperRequest { Action = HelperAction.InstallDriver, PackageDir = pkg, HardwareId = hw },
                new HelperRequest { Action = HelperAction.UninstallDriver, CertThumbprint = "DRYRUN", DisableTestSigning = true, ResumeBitLocker = true },
            };
            foreach (var r in requests)
            {
                var res = await ElevatedHelper.RunAsync(r);
                SetupLog.Write($"== {r.Action}: ok={res.Ok} dryRun={res.DryRun} message={res.Message}");
            }
            SetupLog.Write($"== resume command: {ResumeRegistration.CommandLine}");
            ResumeRegistration.Register();   // dry-run: logged only
        }
        finally { SetupLog.FilePath = savedLog; }
    }

    /// <summary>"Cihazlar" tab, Ayarlar → Senkron and the pairing / launch sheets, light and dark (demo sync data).</summary>
    private static async Task SyncAsync(MainWindow w, MainViewModel vm, string dir)
    {
        var app = (App)Application.Current;
        bool wasDark = App.IsDark;
        var d = vm.Devices;
        foreach (bool dark in new[] { false, true })
        {
            string t = dark ? "dark" : "light";
            app.ApplyTheme(dark);
            vm.Tab = MainTab.Devices;
            await Task.Delay(500);
            Save(w, Path.Combine(dir, $"devices-{t}.png"));
            w.DevicesScroll.ScrollToEnd();
            await Task.Delay(300);
            Save(w, Path.Combine(dir, $"devices-bottom-{t}.png"));
            w.DevicesScroll.ScrollToHome();
            d.IsAddingPc = true;
            await Task.Delay(300);
            Save(w, Path.Combine(dir, $"devices-add-{t}.png"));
            d.IsAddingPc = false;

            vm.Tab = MainTab.Settings;
            await Task.Delay(300);
            await ScrollToAsync(w, w.SyncSettings);
            Save(w, Path.Combine(dir, $"settings-sync-{t}.png"));
            d.ShowExtensionHelp = true;
            await Task.Delay(300);
            await ScrollToAsync(w, w.SyncSettings, 300);
            Save(w, Path.Combine(dir, $"settings-extension-{t}.png"));
            d.ShowExtensionHelp = false;
            if (!d.HasIosSetup) d.ShowIosSetup("İş iPhone'u");
            await Task.Delay(300);
            await ScrollToAsync(w, w.SyncSettings, 820);
            Save(w, Path.Combine(dir, $"settings-iphone-{t}.png"));

            foreach (var (name, sheet) in new[]
            {
                ("pair", ConfirmSheet.ForPairing(new Nitro.Sync.Security.PairingPrompt("77aa88bb99cc", "Ayşe-PC", "482913", true))),
                ("launch", ConfirmSheet.ForLaunch(Services.DemoSyncSource.SampleLaunch)),
            })
            {
                sheet.ShowActivated = false;
                sheet.ShowInTaskbar = false;
                sheet.Topmost = false;
                sheet.WindowStartupLocation = WindowStartupLocation.Manual;
                sheet.Left = -20000;
                sheet.Top = -20000;
                sheet.Show();
                await Task.Delay(300);
                Save(sheet, Path.Combine(dir, $"sheet-{name}-{t}.png"));
                sheet.Close();
            }
        }
        d.IosSetup = null;

        // Empty state: forget the demo PCs (demo data only) and dismiss the iPhone card.
        app.ApplyTheme(false);
        foreach (var pc in d.Paired.ToList()) pc.Unpair.Execute(null);
        d.DismissIphone.Execute(null);
        vm.Tab = MainTab.Devices;
        await Task.Delay(500);
        Save(w, Path.Combine(dir, "devices-empty-light.png"));
        app.ApplyTheme(wasDark);
    }

    /// <summary>Scrolls the settings page so <paramref name="target"/> (plus an offset) is at the top.</summary>
    private static async Task ScrollToAsync(MainWindow w, FrameworkElement target, double extra = 0)
    {
        if (w.SettingsScroll is not { } sv) return;
        sv.ScrollToHome();
        await Task.Delay(100);
        var y = target.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        sv.ScrollToVerticalOffset(Math.Max(0, y - 8 + extra));
        await Task.Delay(300);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    internal sealed class FixedProbe(SystemSnapshot s) : ISystemProbe
    {
        public SystemSnapshot Read(bool includeBitLocker = true) => s;
    }

    /// <summary>Every wizard page with sample data, light and dark (setup-&lt;page&gt;-&lt;theme&gt;.png).</summary>
    private static async Task SetupWizardAsync(string dir)
    {
        var app = (App)Application.Current;
        bool wasDark = App.IsDark;
        var card = SupportedDevices.All[0];
        var machine = new MachineInfo("Acer", "Nitro ANV15-51", "Acer Nitro V 15", "INSYDE Corp.");
        var baseSnap = new SystemSnapshot
        {
            Device = new DeviceInstance(card, @"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01\3&11583659&0&A3", "Netwtw12", true),
            SecureBootOn = true,
            BitLocker = BitLockerQuickState.On,
            Machine = machine,
        };
        var keys = new BitLockerInfo
        {
            Present = true, ProtectionStatus = 1, ConversionStatus = 1,
            Recovery = { new RecoveryKey { Id = "{1A2B3C4D-5E6F-4711-8899-AABBCCDDEEFF}", Password = "000000-000000-000000-000000-000000-000000-000000-000000" } },
        };
        const string license = "Copyright (c) 2006-2021, Intel Corporation.\nAll rights reserved.\n\nRedistribution. Redistribution and use in binary form, without\nmodification, are permitted provided that the following conditions are met:\n\n* Redistributions must reproduce the above copyright notice and the\n  following disclaimer in the documentation and/or other materials\n  provided with the distribution.\n* Neither the name of Intel Corporation nor the names of its suppliers\n  may be used to endorse or promote products derived from this software\n  without specific prior written permission.\n* No reverse engineering, decompilation, or disassembly of this software\n  is permitted.";

        var pages = new (string Name, WizardPage Page, SystemSnapshot Snap, BitLockerInfo? Keys, bool Fw)[]
        {
            ("1-compat", WizardPage.Compatibility, baseSnap, null, false),
            ("1b-unsupported", WizardPage.Compatibility, baseSnap with { Device = null }, null, false),
            ("2-consent", WizardPage.Consent, baseSnap, null, false),
            ("3-bitlocker", WizardPage.BitLocker, baseSnap, keys, false),
            ("4-secureboot", WizardPage.SecureBoot, baseSnap, null, false),
            ("5-testsigning", WizardPage.TestSigning, baseSnap with { SecureBootOn = false }, null, false),
            ("6a-driver-package", WizardPage.Install, baseSnap with { SecureBootOn = false, TestSigningActive = true }, null, false),
            ("6-install", WizardPage.Install, baseSnap with { SecureBootOn = false, TestSigningActive = true }, null, true),
            ("7-done", WizardPage.Done, baseSnap with { SecureBootOn = false, TestSigningActive = true,
                Device = baseSnap.Device! with { Service = "airdropwifi" } }, null, false),
            ("8-rollback", WizardPage.Rollback, baseSnap with { SecureBootOn = false, TestSigningActive = true }, null, false),
            ("9-rollback-done", WizardPage.RollbackDone, baseSnap with { SecureBootOn = false }, null, false),
        };

        foreach (bool dark in new[] { false, true })
        {
            app.ApplyTheme(dark);
            var vm = new SetupWizardViewModel(new FixedProbe(baseSnap), statePath: null, rollbackMode: false);
            var wiz = new SetupWizard(vm, initialize: false)
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
            };
            wiz.Show();
            foreach (var p in pages)
            {
                // "6a": release build without the driver next to the exe → the "Sürücü paketini seç…" card.
                vm.Preview(p.Page, p.Snap, p.Keys, p.Fw, p.Fw ? license : "", driverDir: p.Name.StartsWith("6a") ? "" : null);
                await Task.Delay(300);
                Save(wiz, Path.Combine(dir, $"setup-{p.Name}-{(dark ? "dark" : "light")}.png"));
            }
            wiz.Close();
        }
        app.ApplyTheme(wasDark);
    }

    /// <summary>Three sample transfers (link, PDF, two photos) for the history screens.</summary>
    internal static Services.HistoryEntry[] SampleHistory() => new[]
        {
            new Services.HistoryEntry { Direction = Services.TransferDirection.Received, DeviceName = "Mert", DeviceKind = Services.DeviceKind.Windows,
                ModelName = "MacBookPro18,3", Host = Services.DemoAirDropService.DemoPc, Links = new() { "https://nitrobyte.com.tr" } },
            new Services.HistoryEntry { Direction = Services.TransferDirection.Sent, DeviceName = "Ayşe", DeviceKind = Services.DeviceKind.Mac,
                ModelName = "Mac14,2", Paths = new() { @"C:\yok\Sunum.pdf" }, TotalBytes = 2_400_000 },
            new Services.HistoryEntry { Direction = Services.TransferDirection.Received, DeviceName = "Emre", DeviceKind = Services.DeviceKind.iPhone,
                ModelName = "iPhone16,1", Paths = new() { @"C:\yok\IMG_4821.HEIC", @"C:\yok\IMG_4822.HEIC" }, TotalBytes = 6_391_000 },
        };

    internal static void SaveWindow(Window root, string path) => Save(root, path);

    private static void Save(FrameworkElement root, string path)
    {
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var content = (FrameworkElement)((Window)root).Content;
        double scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
        int wpx = (int)(content.ActualWidth * scale), hpx = (int)(content.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(wpx, hpx, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // Mica isn't captured by RenderTargetBitmap, so paint the fallback colour underneath.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["WindowFallback"], null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            // Absolute viewbox: otherwise the brush stretches the content's *drawn* bounds to the rectangle.
            var brush = new VisualBrush(content)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
