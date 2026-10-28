using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AirDrop.App.Interop;
using AirDrop.App.Localization;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

/// <summary>
/// iPhone notification banner (top-right, never activated). Click = continue on PC (Messages → our conversation,
/// known apps → Nitro.Sync's ContinuationMap). Auto-hides after a few seconds unless the pointer is over it.
/// </summary>
public partial class NotificationToast : Window
{
    private readonly NotificationItem _item;
    private readonly PhoneViewModel _vm;
    private readonly DispatcherTimer _timer;

    /// <param name="showPreview">false hides the message text (privacy: 2FA codes).</param>
    public NotificationToast(NotificationItem item, PhoneViewModel vm, bool showPreview = true, TimeSpan? lifetime = null)
    {
        InitializeComponent();
        _item = item;
        _vm = vm;
        DataContext = item;
        if (!showPreview) MessageText.Text = PhoneStrings.Phone_Toast_PreviewHidden;
        _timer = new DispatcherTimer { Interval = lifetime ?? TimeSpan.FromSeconds(7) };
        _timer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.Apply(this, App.IsDark, transient: true)) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "WindowFallback");
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public NotificationItem Item => _item;

    /// <summary>Main window to bring forward when "continue" lands in our own Messages view.</summary>
    public Action? ShowMain { get; set; }

    private void Body_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
        Continue_Click(sender, e);
    }

    private static bool FindButton(DependencyObject d)
    {
        for (var cur = d; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (cur is System.Windows.Controls.Button) return true;
        return false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var target = _vm.Continue(_item);
        if (target?.AppKey == "messages") ShowMain?.Invoke();
        Close();
    }

    private void Positive_Click(object sender, RoutedEventArgs e) { _vm.NotificationPositive.Execute(_item); Close(); }
    private void Negative_Click(object sender, RoutedEventArgs e) { _vm.NotificationNegative.Execute(_item); Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
