#if DEBUG
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
        w.Show();
        await vm.StartAsync();

        await Task.Delay(6500);                 // demo peers appear
        Save(w, Path.Combine(dir, "nearby.png"));
        vm.Tab = MainTab.History;
        await Task.Delay(400);
        Save(w, Path.Combine(dir, "history.png"));
        vm.Tab = MainTab.Settings;
        await Task.Delay(400);
        Save(w, Path.Combine(dir, "settings.png"));
        if (FindChild<System.Windows.Controls.ScrollViewer>(w.SettingsPage) is { } sv)
        {
            sv.ScrollToEnd();
            await Task.Delay(400);
            Save(w, Path.Combine(dir, "settings-bottom.png"));
            sv.ScrollToHome();
        }
        vm.Tab = MainTab.Nearby;
        await SetupWizardAsync(dir);
        if (ElevatedHelper.DryRun) await SetupDryRunAsync(dir);
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
            foreach (var f in DriverPackage.BundledFiles)
            {
                var src = Path.Combine(SetupPaths.BundledDriverDir, f);
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

    private sealed class FixedProbe(SystemSnapshot s) : ISystemProbe
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
                vm.Preview(p.Page, p.Snap, p.Keys, p.Fw, p.Fw ? license : "");
                await Task.Delay(300);
                Save(wiz, Path.Combine(dir, $"setup-{p.Name}-{(dark ? "dark" : "light")}.png"));
            }
            wiz.Close();
        }
        app.ApplyTheme(wasDark);
    }

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
