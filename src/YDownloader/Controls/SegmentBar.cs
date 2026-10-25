using System;
using System.Windows;
using System.Windows.Media;
using YDownloader.Engine;

namespace YDownloader.Controls;

/// <summary>
/// Dünner Fortschrittsbalken. Mit Segmenten zeigt er jedes Segment an seiner Stelle in der Datei,
/// sonst den Anteil <see cref="Fraction"/>. Gezeichnet in OnRender, ohne Vorlage und Animation.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress),
        typeof(DownloadProgress), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction),
        typeof(double), typeof(SegmentBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill),
        typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track),
        typeof(Brush), typeof(SegmentBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DownloadProgress? Progress { get => (DownloadProgress?)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = h / 2;
        var bounds = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, bounds, r, r);

        var p = Progress;
        if (p?.SegmentLengths is { Length: > 1 } lengths && p.TotalBytes > 0 && lengths.Length == p.SegmentBytes.Length)
        {
            dc.PushClip(new RectangleGeometry(bounds, r, r));
            double scale = w / p.TotalBytes;
            long start = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                double width = Math.Min(p.SegmentBytes[i], lengths[i]) * scale;
                if (width > 0) dc.DrawRectangle(Fill, null, new Rect(start * scale, 0, width, h));
                start += lengths[i];
            }
            dc.Pop();
            return;
        }

        double fill = w * Math.Clamp(Fraction, 0, 1);
        if (fill <= 0) return;
        dc.PushClip(new RectangleGeometry(bounds, r, r));
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fill, h), h), r, r);
        dc.Pop();
    }
}
