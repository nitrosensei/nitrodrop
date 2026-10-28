#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirDrop.App.Localization;
using AirDrop.App.Setup;
using AirDrop.App.ViewModels;
using AirDrop.App.Views;

namespace AirDrop.App;

/// <summary>
/// Debug-only localization/responsive review: <c>NitroDrop.exe --snapshot &lt;dir&gt; --matrix [--langs tr,en,de,ru]</c>
/// renders the main window at each breakpoint (compact 560, regular 920, wide 1280 DIPs) × light/dark × language for
/// Yakındakiler, Cihazlar, Telefon, Geçmiş, Ayarlar, plus the first onboarding step, a setup-wizard step and the
/// receive sheet, all off-screen (−20000), switching the language live (the same path as Ayarlar → Dil). Writes
/// <c>matrix\*.png</c>, <c>board.png</c> (a contact sheet) and <c>fonts.txt</c> (which typeface each UI font resolves
/// to per language).
/// </summary>
internal static class SnapshotMatrix
{
    public static readonly (string Name, double W, double H)[] Breakpoints =
    {
        ("compact", 560, 780),
        ("regular", 920, 740),
        ("wide", 1280, 800),
    };

    private static readonly (string Name, MainTab Tab)[] Tabs =
    {
        ("nearby", MainTab.Nearby), ("devices", MainTab.Devices), ("phone", MainTab.Phone),
        ("history", MainTab.History), ("settings", MainTab.Settings),
    };

    public static bool Requested => Environment.GetCommandLineArgs().Contains("--matrix");

    private static string[] Langs()
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--langs");
        return i >= 0 && i + 1 < args.Length ? args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries) : new[] { "tr", "en", "de", "ru" };
    }

    public static async Task RunAsync(MainWindow w, MainViewModel vm, string dir)
    {
        var outDir = Path.Combine(dir, "matrix");
        Directory.CreateDirectory(outDir);
        var app = (App)Application.Current;
        bool wasDark = App.IsDark;
        var initialLang = Loc.Language;
        WriteFontReport(Path.Combine(dir, "fonts.txt"));
        if (Environment.GetCommandLineArgs().Contains("--fonts-only")) return;

        vm.PreviewHistory(Snapshot.SampleHistory());
        foreach (var lang in Langs())
        {
            Loc.SetLanguage(lang);
            foreach (bool dark in new[] { false, true })
            {
                string t = dark ? "dark" : "light";
                app.ApplyTheme(dark);
                foreach (var (bp, width, height) in Breakpoints)
                {
                    w.WindowState = WindowState.Normal;
                    w.Width = width;
                    w.Height = height;
                    await Task.Delay(300);
                    foreach (var (name, tab) in Tabs)
                    {
                        vm.Tab = tab;
                        await Task.Delay(name == "nearby" ? 450 : 260);
                        Snapshot.SaveWindow(w, Path.Combine(outDir, $"{bp}-{name}-{t}-{lang}.png"));
                    }
                }
                vm.Tab = MainTab.Nearby;
                await SheetsAsync(vm, outDir, t, lang);
            }
        }
        vm.PreviewHistory(null);
        app.ApplyTheme(wasDark);
        Loc.SetLanguage(initialLang);
        w.Width = Breakpoints[1].W;
        w.Height = Breakpoints[1].H;
        ComposeBoard(outDir, Path.Combine(dir, "board.png"));
    }

    /// <summary>Onboarding step 1, the Secure Boot wizard page (vendor guide) and the receive sheet.</summary>
    private static async Task SheetsAsync(MainViewModel vm, string outDir, string t, string lang)
    {
        var settings = new Services.AppSettings { DeviceName = vm.ComputerName, OnboardingCompleted = true };
        var ob = new OnboardingWindow(vm, settings)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000,
        };
        ob.Show();
        ob.GoTo(0, animate: false);
        await Task.Delay(300);
        Snapshot.SaveWindow(ob, Path.Combine(outDir, $"onboarding1-{t}-{lang}.png"));
        ob.GoTo(3, animate: false);
        await Task.Delay(250);
        Snapshot.SaveWindow(ob, Path.Combine(outDir, $"onboarding4-{t}-{lang}.png"));
        ob.Close();

        var baseSnap = new SystemSnapshot
        {
            Device = new DeviceInstance(SupportedDevices.All[0], @"PCI\VEN_8086&DEV_51F1&SUBSYS_02748086&REV_01\3&11583659&0&A3", "Netwtw12", true),
            SecureBootOn = true,
            BitLocker = BitLockerQuickState.On,
            Machine = new MachineInfo("Acer", "Nitro ANV15-51", "Acer Nitro V 15", "INSYDE Corp."),
        };
        var wvm = new SetupWizardViewModel(new Snapshot.FixedProbe(baseSnap), statePath: null, rollbackMode: false);
        var wiz = new SetupWizard(wvm, initialize: false)
        {
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000,
        };
        wiz.Show();
        wvm.Preview(WizardPage.SecureBoot, baseSnap, null, false, "");
        await Task.Delay(300);
        Snapshot.SaveWindow(wiz, Path.Combine(outDir, $"setup-secureboot-{t}-{lang}.png"));
        wvm.Preview(WizardPage.Consent, baseSnap, null, false, "");
        await Task.Delay(250);
        Snapshot.SaveWindow(wiz, Path.Combine(outDir, $"setup-consent-{t}-{lang}.png"));
        wiz.Close();

        var req = new Services.IncomingRequest("Emre'nin iPhone'u", Services.DeviceKind.iPhone, "iPhone16,1",
            new[] { new Services.IncomingFile("IMG_4821.HEIC", 3_481_000), new Services.IncomingFile("IMG_4822.HEIC", 2_910_000),
                    new Services.IncomingFile("Sunum.pdf", 1_204_000) },
            Array.Empty<Uri>(), null);
        var rw = ReceiveWindow.ForPreview(req, new Services.ContactMatch("Emre Can Temur", null), null);
        rw.ShowActivated = false;
        rw.ShowInTaskbar = false;
        rw.Topmost = false;
        rw.WindowStartupLocation = WindowStartupLocation.Manual;
        rw.Left = -20000;
        rw.Top = -20000;
        rw.Show();
        await Task.Delay(300);
        Snapshot.SaveWindow(rw, Path.Combine(outDir, $"receive-{t}-{lang}.png"));
        rw.Close();
    }

    /// <summary>
    /// Which embedded face each UI font family uses for Russian and Latin text: Latin must measure as Schibsted
    /// Grotesk; Cyrillic must not measure as Schibsted's own fallback (Segoe UI), i.e. it comes from Golos Text.
    /// Compared by advance width against the explicit families (FormattedText resolves the family maps).
    /// </summary>
    private static void WriteFontReport(string path)
    {
        var res = Application.Current.Resources;
        var ui = (FontFamily)res["UiFont"];
        var display = (FontFamily)res["DisplayFont"];
        var baseUri = new Uri("pack://application:,,,/");
        var golos = new FontFamily(baseUri, "/Assets/Fonts/#Golos Text");
        var schib = new FontFamily(baseUri, "/Assets/Fonts/#Schibsted Grotesk");
        double W(FontFamily f, string text, string culture, FontWeight weight) =>
            new FormattedText(text, CultureInfo.GetCultureInfo(culture), FlowDirection.LeftToRight,
                new Typeface(f, FontStyles.Normal, weight, FontStretches.Normal), 20, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        var lines = new List<string>();
        foreach (var (label, text) in new[] { ("ru", "Отправить файлы на iPhone 15"), ("latin", "Send files to iPhone 15 · ğüşİ ąęł ß œ") })
            foreach (var culture in new[] { "ru-RU", "de-DE", "tr-TR" })
                foreach (var weight in new[] { FontWeights.Normal, FontWeights.SemiBold })
                {
                    double u = W(ui, text, culture, weight), d = W(display, text, culture, weight);
                    double g = W(golos, text, culture, weight), s = W(schib, text, culture, weight);
                    string Face(double x) => Math.Abs(x - g) < 0.05 ? "Golos Text" : Math.Abs(x - s) < 0.05 ? "Schibsted Grotesk" : "mixed (per-character fallback)";
                    lines.Add($"{label,-5} {culture} {weight,-8}: UiFont={u:0.0} ({Face(u)})  DisplayFont={d:0.0} ({Face(d)})  golos={g:0.0} schibsted={s:0.0}");
                }
        File.WriteAllLines(path, lines);
    }

    /// <summary>Contact sheet: one row per language, a fixed selection of screens as columns.</summary>
    private static void ComposeBoard(string matrixDir, string boardPath)
    {
        var columns = new[]
        {
            "compact-nearby-light", "regular-nearby-dark", "wide-nearby-light", "compact-devices-light", "wide-devices-dark",
            "wide-phone-light", "regular-history-dark", "wide-settings-light", "compact-settings-dark",
            "onboarding1-light", "setup-secureboot-dark", "receive-light",
        };
        const double cellW = 300, cellH = 250, labelH = 22, rowLabelW = 46, pad = 10;
        var langs = Langs();
        double width = rowLabelW + columns.Length * (cellW + pad) + pad, height = labelH + langs.Length * (cellH + pad) + pad;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x30)), null, new Rect(0, 0, width, height));
            var tf = new Typeface("Segoe UI");
            void Text(string s, double x, double y, double size) =>
                dc.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, Brushes.WhiteSmoke, 1.0), new Point(x, y));
            for (int c = 0; c < columns.Length; c++) Text(columns[c], rowLabelW + pad + c * (cellW + pad), 4, 11);
            for (int r = 0; r < langs.Length; r++)
            {
                double y = labelH + r * (cellH + pad);
                Text(langs[r].ToUpperInvariant(), 10, y + cellH / 2 - 10, 16);
                for (int c = 0; c < columns.Length; c++)
                {
                    var file = Path.Combine(matrixDir, $"{columns[c]}-{langs[r]}.png");
                    if (!File.Exists(file)) continue;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    double scale = Math.Min(cellW / bmp.Width, cellH / bmp.Height);
                    double iw = bmp.Width * scale, ih = bmp.Height * scale;
                    dc.DrawImage(bmp, new Rect(rowLabelW + pad + c * (cellW + pad) + (cellW - iw) / 2, y + (cellH - ih) / 2, iw, ih));
                }
            }
        }
        var rtb = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(boardPath)!);
        using var fs = File.Create(boardPath);
        enc.Save(fs);
    }
}
#endif
