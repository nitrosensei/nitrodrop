using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirDrop.App.Interop;
using AirDrop.App.Setup;

namespace AirDrop.App.Views;

/// <summary>"Gelişmiş Kurulum" window. All logic is in <see cref="SetupWizardViewModel"/>.</summary>
public partial class SetupWizard : Window
{
    public SetupWizardViewModel ViewModel { get; }

    /// <param name="initialize">false for off-screen snapshots (data is injected with Preview).</param>
    public SetupWizard(SetupWizardViewModel vm, bool initialize = true)
    {
        InitializeComponent();
        ViewModel = vm;
        DataContext = vm;
        vm.Confirm = Confirm;
        vm.PickSaveFile = PickSaveFile;
        vm.CloseWindow = Close;
        SourceInitialized += (_, _) => ApplyBackdrop();
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) =>
        {
            App.ThemeChanged -= OnThemeChanged;
            vm.Detach();
        };
        if (initialize) Loaded += async (_, _) => await vm.InitializeAsync();
    }

    private void OnThemeChanged(object? sender, System.EventArgs e) => ApplyBackdrop();

    private void ApplyBackdrop()
    {
        if (WindowEffects.Apply(this, App.IsDark)) Background = Brushes.Transparent;
        else SetResourceReference(BackgroundProperty, "WindowFallback");
    }

    private string? PickSaveFile(string suggested)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Kurtarma anahtarını kaydet",
            FileName = suggested,
            DefaultExt = ".txt",
            Filter = "Metin dosyası (*.txt)|*.txt",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>Small confirmation sheet in the app's style. Cancel is the default (Enter does nothing risky).</summary>
    private bool Confirm(string title, string body, string okText)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var text = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var cancel = new Button { Content = "Vazgeç", IsCancel = true, IsDefault = true };
        cancel.SetResourceReference(StyleProperty, "PillButton");
        var ok = new Button { Content = okText, Margin = new Thickness(10, 0, 0, 0) };
        ok.SetResourceReference(StyleProperty, "AccentPill");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(heading);
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = title,
            Owner = this,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = panel,
        };
        w.SetResourceReference(ForegroundProperty, "TextPrimary");
        w.SetResourceReference(FontFamilyProperty, "UiFont");
        w.SetResourceReference(BackgroundProperty, "WindowFallback");
        w.SourceInitialized += (_, _) => WindowEffects.Apply(w, App.IsDark);
        ok.Click += (_, _) => w.DialogResult = true;
        return w.ShowDialog() == true;
    }
}
