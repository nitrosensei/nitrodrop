using System.Windows;
using System.Windows.Controls;
using AirDrop.App.Localization;
using AirDrop.App.Interop;

namespace AirDrop.App.Views;

/// <summary>Small single-line input dialog in the app's style.</summary>
internal static class TextPrompt
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 20) };
        box.SetResourceReference(FrameworkElement.StyleProperty, "InputBox");

        var ok = new Button { Content = MainStrings.Prompt_Save, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentPill");
        var cancel = new Button { Content = CommonStrings.Common_Cancel, IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");

        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
        heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(heading);
        panel.Children.Add(text);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = panel,
        };
        w.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SourceInitialized += (_, _) => WindowEffects.ApplyThemed(w, transient: true);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
