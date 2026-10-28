using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AirDrop.App.Interop;
using AirDrop.App.Services;
using AirDrop.App.ViewModels;

namespace AirDrop.App.Views;

/// <summary>
/// "İlk kullanım" intro: six steps (welcome, send, receive from iPhone, phone, devices, privacy + ready).
/// Shown once (AppSettings.OnboardingCompleted) and again from Ayarlar → Hakkında. ← / → move, Esc skips.
/// Closing in any way (Atla, Başla, Esc, "iPhone'u eşleştir") counts as completed.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int Count = 6;
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly FrameworkElement[] _steps;
    private readonly Border[] _dots = new Border[Count];
    private int _index;

    public OnboardingWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _steps = new FrameworkElement[] { Step1, Step2, Step3, Step4, Step5, Step6 };
        for (int i = 0; i < Count; i++)
        {
            var d = new Border { Height = 7, Width = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(3, 0, 3, 0) };
            _dots[i] = d;
            Dots.Children.Add(d);
        }
        NameBox.Text = settings.DeviceName;
        StartupSwitch.IsChecked = settings.StartWithWindows;
        SourceInitialized += (_, _) => WindowEffects.ApplyThemed(this);
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            App.ThemeChanged -= OnThemeChanged;
            MarkCompleted();
        };
        PreviewKeyDown += OnKey;
        GoTo(0, animate: false);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => WindowEffects.ApplyThemed(this);

    /// <summary>Also used by the Debug snapshot.</summary>
    internal void GoTo(int index, bool animate = true)
    {
        index = Math.Clamp(index, 0, Count - 1);
        int dir = Math.Sign(index - _index);
        _index = index;
        for (int i = 0; i < Count; i++)
        {
            _steps[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            _dots[i].Width = i == index ? 22 : 7;
            _dots[i].SetResourceReference(Border.BackgroundProperty, i == index ? "Accent" : i < index ? "TextTertiary" : "Track");
        }
        StepLabel.Text = $"İLK KULLANIM · {index + 1}/{Count}";
        BackButton.Visibility = index == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = index == Count - 1 ? "Başla" : index == 0 ? "Başlayalım" : "İleri";
        SkipButton.Visibility = index == Count - 1 ? Visibility.Hidden : Visibility.Visible;

        // Short fade + slide; skipped when Windows animations are off (reduced motion).
        if (animate && SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            ((TranslateTransform)Stage.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(dir * 18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;   // arrows edit the device name
        switch (e.Key)
        {
            case Key.Right: GoTo(_index + 1); e.Handled = true; break;
            case Key.Left: GoTo(_index - 1); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_index < Count - 1) { GoTo(_index + 1); return; }
        Finish();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoTo(_index - 1);

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void PairPhone_Click(object sender, RoutedEventArgs e)
    {
        _vm.Tab = MainTab.Phone;
        Close();
    }

    /// <summary>"Başla": applies name, visibility and autostart from the last step.</summary>
    private void Finish()
    {
        var name = NameBox.Text.Trim();
        if (name.Length > 0 && name != _settings.DeviceName) _vm.Settings.DeviceName = name;
        var mode = ModeOff.IsChecked == true ? ReceiveMode.Off : ReceiveMode.Everyone;
        if (_vm.Modes.FirstOrDefault(m => m.Mode == mode && m.Enabled) is { } opt) _vm.SelectedMode = opt;
        if ((StartupSwitch.IsChecked == true) != _settings.StartWithWindows) _vm.Settings.StartWithWindows = StartupSwitch.IsChecked == true;
        _vm.Tab = MainTab.Nearby;
        Close();
    }

    private void MarkCompleted()
    {
        if (_settings.OnboardingCompleted || Left < -10000) return;   // snapshot windows never write settings
        _settings.OnboardingCompleted = true;
        try { _settings.Save(); } catch (Exception) { /* settings dir unavailable */ }
    }
}
