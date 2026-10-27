#if DEBUG
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        vm.Tab = MainTab.Nearby;
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
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
