using System;
using System.Windows;
using System.Windows.Media;
using YDownloader.Downloads;

namespace YDownloader.Controls;

/// <summary>Verlauf der Gesamtgeschwindigkeit: Fläche mit Linie, neuester Wert rechts. Zeichnet nur, wenn ein Wert dazukommt.</summary>
public sealed class SpeedGraph : FrameworkElement
{
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(nameof(History),
        typeof(SpeedHistory), typeof(SpeedGraph), new PropertyMetadata(null, OnHistoryChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke),
        typeof(Brush), typeof(SpeedGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((SpeedGraph)d)._pen = null));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill),
        typeof(Brush), typeof(SpeedGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Breite je Wert in Pixeln, sobald der Verlauf die Breite füllt, danach rutschen die ältesten Werte hinaus. Bis dahin
    /// sind die vorhandenen Werte über die ganze Breite gestreckt. 0 = der ganze Verlauf (<see cref="SpeedHistory.Capacity"/>
    /// Werte) füllt die Breite, neue Werte schieben von rechts hinein (Statusleiste).
    /// </summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step),
        typeof(double), typeof(SpeedGraph), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Grundlinie am unteren Rand. Null = keine.</summary>
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline),
        typeof(Brush), typeof(SpeedGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness),
        typeof(double), typeof(SpeedGraph), new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((SpeedGraph)d)._pen = null));

    /// <summary>Faktor auf den höchsten Wert, damit der Verlauf nicht oben anstößt. 1 = kein Platz nach oben.</summary>
    public static readonly DependencyProperty HeadroomProperty = DependencyProperty.Register(nameof(Headroom),
        typeof(double), typeof(SpeedGraph), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    private Pen? _pen;

    public Brush? Baseline { get => (Brush?)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Headroom { get => (double)GetValue(HeadroomProperty); set => SetValue(HeadroomProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public SpeedHistory? History { get => (SpeedHistory?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (SpeedGraph)d;
        if (e.OldValue is SpeedHistory old) old.Changed -= graph.OnSample;
        if (e.NewValue is SpeedHistory now) now.Changed += graph.OnSample;
        graph.InvalidateVisual();
    }

    private void OnSample(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var history = History;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // Fläche für Tooltips
        if (Baseline is not null && w > 0 && h > 0) dc.DrawRectangle(Baseline, null, new Rect(0, h - 1, w, 1));
        if (history is null || history.Count < 2 || w <= 0 || h <= 0) return;

        int count = history.Count;
        double step = Step;
        double dx = w / (SpeedHistory.Capacity - 1);
        int first = 0;
        double max = Math.Max(history.Max, 1);
        if (step > 0)
        {
            // Mit fester Schrittweite passen nur die neuesten Werte hinein: Der Maßstab richtet sich nach ihnen, nicht nach älteren.
            int fit = (int)(w / step);
            dx = count - 1 > fit ? step : w / (count - 1);
            first = Math.Max(0, count - 1 - fit);
            max = 1;
            for (int i = first; i < count; i++) max = Math.Max(max, history[i]);
        }
        max *= Math.Max(1, Headroom);
        double X(int i) => w - (count - 1 - i) * dx;
        double Y(int i) => h - 1 - history[i] / max * (h - 2);

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(X(first), h), isFilled: true, isClosed: true);
            for (int i = first; i < count; i++) g.LineTo(new Point(X(i), Y(i)), false, false);
            g.LineTo(new Point(w, h), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(Fill, null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(X(first), Y(first)), isFilled: false, isClosed: false);
            for (int i = first + 1; i < count; i++) g.LineTo(new Point(X(i), Y(i)), true, true);
        }
        line.Freeze();
        if (_pen is null && Stroke is not null)
        {
            _pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
            if (_pen.CanFreeze) _pen.Freeze();
        }
        dc.DrawGeometry(null, _pen, line);
    }
}
