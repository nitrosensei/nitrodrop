using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AirDrop.App.Interop;

namespace AirDrop.App.Views;

/// <summary>Small single-line input dialog in the app's style.</summary>
internal static class TextPrompt
{
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 10, 0, 18),
        };
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(Control.BackgroundProperty, "Card");
        box.SetResourceReference(Control.BorderBrushProperty, "CardBorder");

        var ok = new Button { Content = "Kaydet", IsDefault = true, Margin = new Thickness(10, 0, 0, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentPill");
        var cancel = new Button { Content = "Vazgeç", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");

        var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(text);
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = panel,
        };
        w.SetResourceReference(Window.ForegroundProperty, "TextPrimary");
        w.SetResourceReference(Window.FontFamilyProperty, "UiFont");
        w.SourceInitialized += (_, _) =>
        {
            if (WindowEffects.Apply(w, App.IsDark, transient: true)) w.Background = Brushes.Transparent;
            else w.SetResourceReference(Window.BackgroundProperty, "WindowFallback");
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
