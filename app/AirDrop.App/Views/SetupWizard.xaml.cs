using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirDrop.App.Interop;
using AirDrop.App.Localization;
using AirDrop.App.Setup;

namespace AirDrop.App.Views;

/// <summary>"Advanced Setup" window. All logic is in <see cref="SetupWizardViewModel"/>.</summary>
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
        vm.PickDriverPackage = PickDriverPackage;
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
        WindowEffects.ApplyThemed(this);
    }

    private string? PickSaveFile(string suggested)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = SetupStrings.Setup_SaveDialogTitle,
            FileName = suggested,
            DefaultExt = ".txt",
            Filter = SetupStrings.Setup_SaveDialogFilter,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>NitroDrop-driver-&lt;ver&gt;.zip or the airdropwifi.inf extracted from it.</summary>
    private string? PickDriverPackage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = SetupStrings.Setup_PickDriverDialogTitle,
            Filter = SetupStrings.Setup_PickDriverFilter,
            CheckFileExists = true,
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>Small confirmation sheet in the app's style. Cancel is the default (Enter does nothing risky).</summary>
    private bool Confirm(string title, string body, string okText)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var text = new TextBlock { Text = body, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 20) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var cancel = new Button { Content = CommonStrings.Common_Cancel, IsCancel = true, IsDefault = true };
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
        w.SourceInitialized += (_, _) => WindowEffects.ApplyThemed(w, transient: true);
        ok.Click += (_, _) => w.DialogResult = true;
        return w.ShowDialog() == true;
    }
}
