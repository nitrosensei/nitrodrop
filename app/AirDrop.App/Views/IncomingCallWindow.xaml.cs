using System;
using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AirDrop.App.Interop;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

/// <summary>
/// Top-right call banner (never takes focus: ShowActivated=false, no taskbar button). Ringing → Reddet / Yanıtla;
/// after answering the same window becomes the in-call mini window (timer, Sessiz, Sesi telefona aktar, Kapat).
/// Closes itself shortly after the call ends.
/// </summary>
public partial class IncomingCallWindow : Window
{
    private readonly CallViewModel _vm;
    private readonly DispatcherTimer _timer;

    public IncomingCallWindow(CallViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _vm.Tick();
        _timer.Start();
        _vm.Ended += OnEnded;
        _vm.PropertyChanged += OnVmChanged;
        SourceInitialized += (_, _) =>
        {
            if (WindowEffects.Apply(this, App.IsDark, transient: true)) Background = Brushes.Transparent;
            else SetResourceReference(BackgroundProperty, "WindowFallback");
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _vm.Ended -= OnEnded;
            _vm.PropertyChanged -= OnVmChanged;
        };
    }

    public CallViewModel ViewModel => _vm;

    /// <summary>Shows the banner at the top-right of the work area without activating it.</summary>
    public static IncomingCallWindow ShowFor(CallViewModel vm, bool playSound = true)
    {
        var w = new IncomingCallWindow(vm);
        var wa = SystemParameters.WorkArea;
        w.Left = wa.Right - w.Width - 12;
        w.Top = wa.Top + 12;
        if (playSound && vm.IsRinging) SystemSounds.Exclamation.Play();
        w.Show();
        return w;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Ringing → active changes the height (SizeToContent); keep the top-right anchor.
        if (e.PropertyName is "" or nameof(CallViewModel.IsActive))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 12;
            }));
    }

    private void OnEnded()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        t.Tick += (_, _) => { t.Stop(); Close(); };
        t.Start();
    }

    private void End_Click(object sender, RoutedEventArgs e)
    {
        _vm.End.Execute(null);
        if (!_vm.CanEnd) Close();   // ANCS: we only hide our view, the call goes on on the iPhone
    }
}
