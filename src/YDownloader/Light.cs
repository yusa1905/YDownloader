using System.Windows;
using System.Windows.Media;

namespace YDownloader;

/// <summary>Attached-Eigenschaften für einen Ampel-Knopf (Farbe, Symbol, Symbol sichtbar).</summary>
public static class Light
{
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached("Fill", typeof(Brush), typeof(Light));
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.RegisterAttached("Glyph", typeof(Geometry), typeof(Light));
    public static readonly DependencyProperty GlyphFillProperty =
        DependencyProperty.RegisterAttached("GlyphFill", typeof(Geometry), typeof(Light));
    public static readonly DependencyProperty GlyphBrushProperty =
        DependencyProperty.RegisterAttached("GlyphBrush", typeof(Brush), typeof(Light));
    public static readonly DependencyProperty GlyphVisibilityProperty =
        DependencyProperty.RegisterAttached("GlyphVisibility", typeof(Visibility), typeof(Light),
            new PropertyMetadata(Visibility.Collapsed));

    public static Brush GetFill(DependencyObject d) => (Brush)d.GetValue(FillProperty);
    public static void SetFill(DependencyObject d, Brush v) => d.SetValue(FillProperty, v);
    public static Geometry GetGlyph(DependencyObject d) => (Geometry)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, Geometry v) => d.SetValue(GlyphProperty, v);
    public static Geometry GetGlyphFill(DependencyObject d) => (Geometry)d.GetValue(GlyphFillProperty);
    public static void SetGlyphFill(DependencyObject d, Geometry v) => d.SetValue(GlyphFillProperty, v);
    public static Brush GetGlyphBrush(DependencyObject d) => (Brush)d.GetValue(GlyphBrushProperty);
    public static void SetGlyphBrush(DependencyObject d, Brush v) => d.SetValue(GlyphBrushProperty, v);
    public static Visibility GetGlyphVisibility(DependencyObject d) => (Visibility)d.GetValue(GlyphVisibilityProperty);
    public static void SetGlyphVisibility(DependencyObject d, Visibility v) => d.SetValue(GlyphVisibilityProperty, v);
}
