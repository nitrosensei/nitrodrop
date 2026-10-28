#if DEBUG
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AirDrop.App.Services;
using AirDrop.App.ViewModels;
using AirDrop.App.Views;
using Nitro.Continuity.Runtime;

namespace AirDrop.App;

/// <summary>
/// Debug-only: renders the "Telefon" tab, the call banner and a notification toast off-screen (x = -20000, never
/// activated) in light and dark with demo data → phone-*.png / call-*.png / toast-*.png. Same technique as Snapshot.cs.
/// Hook (one line, in App.xaml.cs after <c>Snapshot.RunAsync</c>): <c>await ContinuitySnapshots.RenderAsync(e.Args[snap + 1]);</c>
/// </summary>
public static class ContinuitySnapshots
{
    private const string ThemeBase = "pack://application:,,,/NitroDrop;component/Themes/";

    public static async Task RenderAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = Application.Current;
        var original = app.Resources.MergedDictionaries.Count > 0 ? app.Resources.MergedDictionaries[0] : null;
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                SetTheme(dark);
                string t = dark ? "dark" : "light";
                await PanelAsync(dir, t);
                await CallAsync(dir, t);
                await ToastAsync(dir, t);
            }
        }
        finally
        {
            if (original is not null) app.Resources.MergedDictionaries[0] = original;
        }
    }

    private static void SetTheme(bool dark)
    {
        var md = Application.Current.Resources.MergedDictionaries;
        var rd = new ResourceDictionary { Source = new Uri(ThemeBase + (dark ? "Dark.xaml" : "Light.xaml"), UriKind.Absolute) };
        if (md.Count == 0) md.Add(rd); else md[0] = rd;
    }

    private static Window OffScreen(FrameworkElement content, double width, double? height = null)
    {
        var w = new Window
        {
            Content = content, Width = width, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"],
        };
        if (height is { } h) w.Height = h; else w.SizeToContent = SizeToContent.Height;
        w.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        w.SetResourceReference(Window.BackgroundProperty, "WindowFallback");
        return w;
    }

    private static async Task PanelAsync(string dir, string theme)
    {
        // Paired: messages / notifications / calls.
        var svc = new DemoContinuityService(autoplay: false);
        var vm = new PhoneViewModel(svc, new DemoPhoneContacts(), a => a());
        await vm.StartAsync();
        var panel = new PhonePanel { DataContext = vm };
        var w = OffScreen(panel, 560, 688);
        w.Show();
        vm.SelectedConversation = vm.Conversations[0];
        vm.ReplyText = "";
        await Settle();
        Save(w, Path.Combine(dir, $"phone-messages-{theme}.png"));
        vm.Section = PhoneSection.Notifications;
        await Settle();
        Save(w, Path.Combine(dir, $"phone-notifications-{theme}.png"));
        vm.Section = PhoneSection.Calls;
        await Settle();
        Save(w, Path.Combine(dir, $"phone-calls-{theme}.png"));

        // Phone Link conflict status.
        svc.SimulateStatus(new PhoneLinkStatus(PhoneLinkState.Blocked, "Phone Link bu iPhone'u kullanıyor",
            PhoneLinkConflictDetector.Guidance, DemoContinuityService.DemoPhone.Name, PhoneLinkIssue.PhoneLinkHoldsDevice,
            PhoneFeatures.Calls));
        vm.Section = PhoneSection.Messages;
        await Settle();
        Save(w, Path.Combine(dir, $"phone-blocked-{theme}.png"));
        w.Close();

        // Not paired: guide, then the code confirmation step.
        var svc2 = new DemoContinuityService(autoplay: false, paired: false);
        var vm2 = new PhoneViewModel(svc2, new DemoPhoneContacts(), a => a());
        await vm2.StartAsync();
        var panel2 = new PhonePanel { DataContext = vm2 };
        var w2 = OffScreen(panel2, 560, 688);
        w2.Show();
        await Settle();
        Save(w2, Path.Combine(dir, $"phone-setup-{theme}.png"));
        _ = ((IPairingPrompt)vm2).ConfirmAsync(PairingKind.ConfirmPinMatch, "Emre'nin iPhone'u", "482913", CancellationToken.None);
        await Settle();
        Save(w2, Path.Combine(dir, $"phone-setup-confirm-{theme}.png"));
        vm2.CancelPairing.Execute(null);
        w2.Close();
    }

    private static async Task CallAsync(string dir, string theme)
    {
        foreach (var cap in new[] { CallCapability.ControlOnly, CallCapability.FullAudio })
        {
            var svc = new DemoContinuityService(autoplay: false, capability: cap);
            var vm = new PhoneViewModel(svc, new DemoPhoneContacts(), a => a());
            await vm.StartAsync();
            CallViewModel? call = null;
            vm.IncomingCall += c => call = c;
            svc.SimulateIncomingCall("+905321112233", null);
            var win = new IncomingCallWindow(call!) { Left = -20000, Top = -20000 };
            win.Show();
            string suffix = cap == CallCapability.FullAudio ? "full" : "control";
            if (cap == CallCapability.ControlOnly)
            {
                await Settle();
                Save(win, Path.Combine(dir, $"call-ringing-{theme}.png"));
            }
            await svc.AnswerCallAsync(call!.Id);
            await Settle();
            Save(win, Path.Combine(dir, $"call-active-{suffix}-{theme}.png"));
            win.Close();
        }
    }

    private static async Task ToastAsync(string dir, string theme)
    {
        var svc = new DemoContinuityService(autoplay: false);
        var vm = new PhoneViewModel(svc, new DemoPhoneContacts(), a => a());
        await vm.StartAsync();
        NotificationItem? item = null;
        vm.ToastRequested += n => item = n;
        svc.AddNotification("net.whatsapp.WhatsApp", "WhatsApp", "Zeynep Kaya", null,
            "Sunumu yeniden gönderdim, son hali bu. Akşama kadar bakabilir misin?", DateTime.Now);
        var toast = new NotificationToast(item!, vm, lifetime: TimeSpan.FromMinutes(5)) { Left = -20000, Top = -20000 };
        toast.Show();
        await Settle();
        Save(toast, Path.Combine(dir, $"toast-{theme}.png"));
        toast.Close();

        item = null;
        _ = svc.SimulateIncomingMessageAsync("+905321112233", "Annenler 8'de geliyor, geç kalma");
        var sms = new NotificationToast(item!, vm, lifetime: TimeSpan.FromMinutes(5)) { Left = -20000, Top = -20000 };
        sms.Show();
        await Settle();
        Save(sms, Path.Combine(dir, $"toast-sms-{theme}.png"));
        sms.Close();
    }

    private static async Task Settle()
    {
        await Task.Delay(250);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window root, string path)
    {
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var content = (FrameworkElement)root.Content;
        double scale = VisualTreeHelper.GetDpi(root).DpiScaleX;
        int wpx = (int)(content.ActualWidth * scale), hpx = (int)(content.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(Math.Max(1, wpx), Math.Max(1, hpx), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            // Mica/acrylic isn't captured by RenderTargetBitmap: paint the fallback colour underneath.
            dc.DrawRectangle((Brush)Application.Current.Resources["WindowFallback"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            var brush = new VisualBrush(content)
            {
                Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, content.ActualWidth, content.ActualHeight),
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
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
