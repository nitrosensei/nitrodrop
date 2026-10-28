using System;
using System.Windows;
using System.Windows.Media;

namespace AirDrop.App.Controls;

/// <summary>
/// Draws one device from Themes/DeviceArt.xaml: "DeviceArt.{ArtKey}" is stroked (line style, 24×24 design box, stroke
/// scales with the size so every icon keeps the same weight), the optional "DeviceArt.{ArtKey}.Solid" is filled with the
/// same brush (Dynamic Island, camera dots). Unknown keys fall back to "DeviceArt.Generic". Bind Stroke to a theme brush.
/// </summary>
public sealed class DeviceArtIcon : FrameworkElement
{
    public const double DesignSize = 24;

    public static readonly DependencyProperty ArtKeyProperty = DependencyProperty.Register(
        nameof(ArtKey), typeof(string), typeof(DeviceArtIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(DeviceArtIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Line weight in design units (24-unit box); 1.6 matches the theme's icon font at 13–17 px.</summary>
    public static readonly DependencyProperty StrokeWeightProperty = DependencyProperty.Register(
        nameof(StrokeWeight), typeof(double), typeof(DeviceArtIcon),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? ArtKey { get => (string?)GetValue(ArtKeyProperty); set => SetValue(ArtKeyProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeWeight { get => (double)GetValue(StrokeWeightProperty); set => SetValue(StrokeWeightProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? DesignSize : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? DesignSize : availableSize.Height;
        double s = Math.Min(w, h);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var key = string.IsNullOrEmpty(ArtKey) ? "Generic" : ArtKey;
        var outline = TryFindResource("DeviceArt." + key) as Geometry ?? TryFindResource("DeviceArt.Generic") as Geometry;
        if (outline is null) return;
        var solid = TryFindResource("DeviceArt." + key + ".Solid") as Geometry;

        double scale = size / DesignSize;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var pen = new Pen(Stroke, StrokeWeight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, outline);
        if (solid is not null) dc.DrawGeometry(Stroke, null, solid);
        dc.Pop();
        dc.Pop();
    }
}
