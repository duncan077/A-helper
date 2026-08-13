// SPDX-License-Identifier: GPL-3.0-or-later
//
// Draggable fan curve editor.
//
// A custom Control rather than a chart library: Avalonia has no built-in chart,
// and every third-party one either pulls in reflection-heavy binding machinery
// that fights Native AOT or is far more than six draggable points needs.
//
// Rendering and hit-testing are index-based - points sit at fixed temperature
// bands, so only the duty axis is draggable. That keeps the interaction
// unambiguous and means a curve can never end up with its temperatures out of
// order.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AcerHelper.App.Controls;

public sealed class FanCurveEditor : Control
{
    private const double HandleRadius = 5.5;
    private const double PaddingLeft = 34;
    private const double PaddingRight = 10;
    private const double PaddingTop = 10;
    private const double PaddingBottom = 20;

    public static readonly StyledProperty<IList<FanCurveRow>?> PointsProperty =
        AvaloniaProperty.Register<FanCurveEditor, IList<FanCurveRow>?>(nameof(Points));

    public static readonly StyledProperty<IBrush> CurveBrushProperty =
        AvaloniaProperty.Register<FanCurveEditor, IBrush>(
            nameof(CurveBrush), new SolidColorBrush(Color.FromRgb(0xD6, 0x30, 0x31)));

    /// <summary>Live temperature marker, or null to hide it.</summary>
    public static readonly StyledProperty<int?> CurrentTemperatureProperty =
        AvaloniaProperty.Register<FanCurveEditor, int?>(nameof(CurrentTemperature));

    public IList<FanCurveRow>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush CurveBrush
    {
        get => GetValue(CurveBrushProperty);
        set => SetValue(CurveBrushProperty, value);
    }

    public int? CurrentTemperature
    {
        get => GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    private int _dragIndex = -1;

    static FanCurveEditor()
    {
        AffectsRender<FanCurveEditor>(PointsProperty, CurveBrushProperty, CurrentTemperatureProperty);
    }

    public FanCurveEditor()
    {
        // Without this the control is transparent to hit-testing and never
        // receives pointer events.
        Background = Brushes.Transparent;
        MinHeight = 150;
        ClipToBounds = true;
    }

    /// <summary>Background is not a Control property; painting it keeps hit-testing alive.</summary>
    public IBrush? Background { get; set; }

    private Rect PlotArea => new(
        PaddingLeft, PaddingTop,
        Math.Max(1, Bounds.Width - PaddingLeft - PaddingRight),
        Math.Max(1, Bounds.Height - PaddingTop - PaddingBottom));

    private double XFor(int index, int count)
    {
        var plot = PlotArea;
        return count <= 1 ? plot.X : plot.X + (plot.Width * index / (count - 1.0));
    }

    private double YFor(double duty)
    {
        var plot = PlotArea;
        return plot.Y + (plot.Height * (1 - (Math.Clamp(duty, 0, 100) / 100.0)));
    }

    private double DutyFor(double y)
    {
        var plot = PlotArea;
        return Math.Clamp((1 - ((y - plot.Y) / plot.Height)) * 100, 0, 100);
    }

    public override void Render(DrawingContext context)
    {
        var plot = PlotArea;
        if (Background is { } bg) context.FillRectangle(bg, Bounds);

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        var axis = new SolidColorBrush(Color.FromArgb(150, 128, 128, 128));
        var typeface = new Typeface(FontFamily.Default);

        // Horizontal grid every 25%.
        for (var duty = 0; duty <= 100; duty += 25)
        {
            var y = YFor(duty);
            context.DrawLine(grid, new Point(plot.X, y), new Point(plot.Right, y));

            var text = new FormattedText($"{duty}%", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, axis);
            context.DrawText(text, new Point(2, y - (text.Height / 2)));
        }

        var points = Points;
        if (points is not { Count: > 1 }) return;

        // Vertical grid plus temperature labels.
        for (var i = 0; i < points.Count; i++)
        {
            var x = XFor(i, points.Count);
            context.DrawLine(grid, new Point(x, plot.Y), new Point(x, plot.Bottom));

            var text = new FormattedText($"{points[i].TemperatureC}", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 10, axis);
            context.DrawText(text, new Point(x - (text.Width / 2), plot.Bottom + 4));
        }

        // Live temperature marker, positioned by interpolating between bands.
        if (CurrentTemperature is { } temp)
        {
            var markerX = MarkerX(points, temp);
            if (markerX is { } mx)
            {
                var markerPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1,
                    new DashStyle([3, 3], 0));
                context.DrawLine(markerPen, new Point(mx, plot.Y), new Point(mx, plot.Bottom));
            }
        }

        var curvePen = new Pen(CurveBrush, 2, lineJoin: PenLineJoin.Round);

        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(curvePen,
                new Point(XFor(i - 1, points.Count), YFor(points[i - 1].Duty)),
                new Point(XFor(i, points.Count), YFor(points[i].Duty)));
        }

        for (var i = 0; i < points.Count; i++)
        {
            var centre = new Point(XFor(i, points.Count), YFor(points[i].Duty));
            var fill = i == _dragIndex ? Brushes.White : CurveBrush;
            context.DrawEllipse(fill, curvePen, centre, HandleRadius, HandleRadius);
        }
    }

    private double? MarkerX(IList<FanCurveRow> points, int temperature)
    {
        if (temperature <= points[0].TemperatureC) return XFor(0, points.Count);
        if (temperature >= points[^1].TemperatureC) return XFor(points.Count - 1, points.Count);

        for (var i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].TemperatureC) continue;

            var span = points[i].TemperatureC - points[i - 1].TemperatureC;
            if (span <= 0) return XFor(i, points.Count);

            var t = (temperature - points[i - 1].TemperatureC) / (double)span;
            return XFor(i - 1, points.Count) + (t * (XFor(i, points.Count) - XFor(i - 1, points.Count)));
        }

        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Points is not { Count: > 0 } points) return;

        var position = e.GetPosition(this);

        // Nearest by X only: temperatures are fixed, so horizontal distance is
        // the only thing that identifies which point is being grabbed.
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < points.Count; i++)
        {
            var distance = Math.Abs(XFor(i, points.Count) - position.X);
            if (distance >= bestDistance) continue;

            bestDistance = distance;
            best = i;
        }

        if (best < 0) return;

        _dragIndex = best;
        points[best].Duty = Snap(DutyFor(position.Y));

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragIndex < 0 || Points is not { Count: > 0 } points) return;
        if (_dragIndex >= points.Count) return;

        points[_dragIndex].Duty = Snap(DutyFor(e.GetPosition(this).Y));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _dragIndex = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// <summary>
    /// Snaps to whole percent, and away from the stall band: 1-29 cannot be
    /// programmed, so dragging there resolves to off or to the floor rather
    /// than to a value the guard would silently change.
    /// </summary>
    private static double Snap(double duty)
    {
        var rounded = Math.Round(duty);
        if (rounded <= 0) return 0;
        return rounded < 30 ? (rounded < 15 ? 0 : 30) : rounded;
    }
}
