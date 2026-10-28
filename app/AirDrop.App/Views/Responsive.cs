using System.Windows;

namespace AirDrop.App.Views;

/// <summary>Main window layout by width (DIPs).</summary>
public enum LayoutMode
{
    /// <summary>&lt; 720: top segmented tabs, single column.</summary>
    Compact,
    /// <summary>720–1099: left sidebar navigation, single content column.</summary>
    Regular,
    /// <summary>≥ 1100: sidebar + two panes where it helps (list + detail).</summary>
    Wide,
}

/// <summary>
/// <c>Responsive.Mode</c>: inherited attached property set on the main window; pages read it in style triggers
/// (<c>&lt;Trigger Property="views:Responsive.Mode" Value="Wide"&gt;</c>) or bindings. Changes only when a breakpoint
/// is crossed (with a small hysteresis), so dragging the window edge never re-lays-out the pages on every pixel.
/// </summary>
public static class Responsive
{
    public const double RegularMin = 720;
    public const double WideMin = 1100;
    private const double Hysteresis = 12;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(LayoutMode), typeof(Responsive),
        new FrameworkPropertyMetadata(LayoutMode.Compact, FrameworkPropertyMetadataOptions.Inherits));

    public static LayoutMode GetMode(DependencyObject d) => (LayoutMode)d.GetValue(ModeProperty);
    public static void SetMode(DependencyObject d, LayoutMode v) => d.SetValue(ModeProperty, v);

    /// <summary>Mode for <paramref name="width"/>, staying in <paramref name="current"/> within the hysteresis band.</summary>
    public static LayoutMode For(double width, LayoutMode current)
    {
        LayoutMode raw = width >= WideMin ? LayoutMode.Wide : width >= RegularMin ? LayoutMode.Regular : LayoutMode.Compact;
        if (raw == current) return current;
        // Only switch once the width is clearly past the boundary in the direction of travel.
        double boundary = (raw > current ? raw : current) == LayoutMode.Wide ? WideMin : RegularMin;
        return System.Math.Abs(width - boundary) < Hysteresis && IsAdjacent(raw, current) ? current : raw;
    }

    private static bool IsAdjacent(LayoutMode a, LayoutMode b) => System.Math.Abs((int)a - (int)b) == 1;
}
