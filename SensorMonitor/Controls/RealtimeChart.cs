using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SensorMonitor.Models;
using SensorMonitor.Processing;

namespace SensorMonitor.Controls;

/// <summary>What the chart needs from a series. Implemented by SensorViewModel.</summary>
public interface IChartSeries
{
    string Name { get; }
    string Unit { get; }
    Color Color { get; }
    bool IsVisible { get; }
    double NominalMin { get; }
    double NominalMax { get; }
    TimeSeriesBuffer Buffer { get; }
}

/// <summary>
/// Scrolling real-time line chart drawn straight onto a DrawingContext once per
/// rendered frame. Supports one combined plot or one lane per series, smooth
/// auto-scaling, wall-clock time axis, and min/max decimation so dense data
/// stays fast (never more than ~2 points per pixel column are drawn).
///
/// Per frame, each series goes through:
///   raw buffer -> resample to common rate (optional) -> normalize -> decimate -> draw
/// </summary>
public sealed class RealtimeChart : FrameworkElement
{
    #region Dependency properties

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IEnumerable), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimeWindowSecondsProperty = DependencyProperty.Register(
        nameof(TimeWindowSeconds), typeof(double), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsSplitViewProperty = DependencyProperty.Register(
        nameof(IsSplitView), typeof(bool), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NormalizationModeProperty = DependencyProperty.Register(
        nameof(NormalizationMode), typeof(NormalizationMode), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(NormalizationMode.Raw, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((RealtimeChart)d)._ranges.Clear())); // new scale: drop the smoothed Y range

    public static readonly DependencyProperty ResampleMethodProperty = DependencyProperty.Register(
        nameof(ResampleMethod), typeof(ResampleMethod), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(ResampleMethod.Off, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResampleRateHzProperty = DependencyProperty.Register(
        nameof(ResampleRateHz), typeof(double), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSamplePointsProperty = DependencyProperty.Register(
        nameof(ShowSamplePoints), typeof(bool), typeof(RealtimeChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Any enumerable of <see cref="IChartSeries"/>.</summary>
    public IEnumerable? Series
    {
        get => (IEnumerable?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double TimeWindowSeconds
    {
        get => (double)GetValue(TimeWindowSecondsProperty);
        set => SetValue(TimeWindowSecondsProperty, value);
    }

    /// <summary>When false the time axis freezes (paused / stopped).</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public bool IsSplitView
    {
        get => (bool)GetValue(IsSplitViewProperty);
        set => SetValue(IsSplitViewProperty, value);
    }

    public NormalizationMode NormalizationMode
    {
        get => (NormalizationMode)GetValue(NormalizationModeProperty);
        set => SetValue(NormalizationModeProperty, value);
    }

    public ResampleMethod ResampleMethod
    {
        get => (ResampleMethod)GetValue(ResampleMethodProperty);
        set => SetValue(ResampleMethodProperty, value);
    }

    public double ResampleRateHz
    {
        get => (double)GetValue(ResampleRateHzProperty);
        set => SetValue(ResampleRateHzProperty, value);
    }

    /// <summary>Draws a dot on each plotted sample when they are far enough apart to see.</summary>
    public bool ShowSamplePoints
    {
        get => (bool)GetValue(ShowSamplePointsProperty);
        set => SetValue(ShowSamplePointsProperty, value);
    }

    #endregion

    private const double MarginLeft = 58, MarginRight = 16, MarginTop = 12, MarginBottom = 30;
    private static readonly object CombinedKey = new();
    private static readonly double[] TimeStepsSeconds = { 1, 2, 5, 10, 15, 30, 60, 120 };

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1A)));
    private static readonly Brush PlotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x23)));
    private static readonly Brush AxisTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA5)));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x2C, 0x36))), 1));
    private static readonly Pen BorderPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x35, 0x40))), 1));

    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _titleTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private readonly Dictionary<object, (double Min, double Max)> _ranges = new();
    private readonly Dictionary<Color, (Pen Pen, Brush Brush)> _styles = new();
    private readonly List<List<DataPoint>> _pool = new();
    private readonly List<DataPoint> _raw = new(4096);
    private readonly List<Point> _points = new();

    private DateTime _frozenNow = DateTime.UtcNow;
    private TimeSpan _lastRenderingTime;
    private double _pixelsPerDip = 1.0;

    public RealtimeChart()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; redraw once per frame.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = args.RenderingTime;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(size));
        if (size.Width < 140 || size.Height < 90) return;

        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (IsLive) _frozenNow = DateTime.UtcNow;
        long windowTicks = TimeSpan.FromSeconds(Math.Max(1, TimeWindowSeconds)).Ticks;
        long fromTicks = _frozenNow.Ticks - windowTicks;

        var visible = (Series?.OfType<IChartSeries>() ?? Enumerable.Empty<IChartSeries>())
            .Where(s => s.IsVisible)
            .ToList();

        var area = new Rect(MarginLeft, MarginTop,
            size.Width - MarginLeft - MarginRight,
            size.Height - MarginTop - MarginBottom);

        if (visible.Count == 0)
        {
            dc.DrawRectangle(PlotBrush, BorderPen, area);
            DrawCenteredText(dc, area, "No sensors selected");
            return;
        }

        if (IsSplitView && visible.Count > 1)
        {
            const double gap = 10;
            double laneHeight = (area.Height - gap * (visible.Count - 1)) / visible.Count;
            if (laneHeight >= 40)
            {
                for (int i = 0; i < visible.Count; i++)
                {
                    var s = visible[i];
                    var lane = new Rect(area.Left, area.Top + i * (laneHeight + gap), area.Width, laneHeight);
                    DrawPlot(dc, lane, new[] { s }, s, fromTicks, windowTicks,
                        showTimeLabels: i == visible.Count - 1,
                        title: LaneTitle(s));
                }
                return;
            }
            // Too many lanes for the available height: fall through to combined.
        }

        DrawPlot(dc, area, visible, CombinedKey, fromTicks, windowTicks, showTimeLabels: true, title: null);
    }

    private void DrawPlot(DrawingContext dc, Rect rect, IReadOnlyList<IChartSeries> series, object rangeKey,
        long fromTicks, long windowTicks, bool showTimeLabels, string? title)
    {
        while (_pool.Count < series.Count) _pool.Add(new List<DataPoint>(4096));

        var mode = NormalizationMode;
        long toTicks = fromTicks + windowTicks;

        // 1) Snapshot -> resample -> normalize, and find the value range.
        double dataMin = double.MaxValue, dataMax = double.MinValue;
        bool anyData = false;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            var pts = _pool[i];
            s.Buffer.CopySince(fromTicks, _raw, includePrevious: true);
            SignalProcessor.Resample(_raw, pts, fromTicks, toTicks, ResampleRateHz, ResampleMethod);
            SignalProcessor.Normalize(pts, mode, s.NominalMin, s.NominalMax);
            foreach (var p in pts)
            {
                if (p.Value < dataMin) dataMin = p.Value;
                if (p.Value > dataMax) dataMax = p.Value;
            }
            anyData |= pts.Count > 0;
        }

        if (!anyData) { dataMin = 0; dataMax = 1; }

        double yMin, yMax;
        switch (mode)
        {
            case NormalizationMode.MinMax:
                // Fixed scale - that's the point of min-max.
                (yMin, yMax) = (-0.08, 1.08);
                break;
            case NormalizationMode.NominalRange:
                // Always show the full 0-100 % band; grow if a sensor goes out of range.
                (yMin, yMax) = UpdateRange(rangeKey, Math.Min(dataMin, 0), Math.Max(dataMax, 100));
                break;
            case NormalizationMode.ZScore:
                (yMin, yMax) = UpdateRange(rangeKey, Math.Min(dataMin, -2), Math.Max(dataMax, 2));
                break;
            default:
                (yMin, yMax) = UpdateRange(rangeKey, dataMin, dataMax);
                break;
        }

        // 2) Frame, grid, axes.
        dc.DrawRectangle(PlotBrush, BorderPen, rect);
        DrawValueAxis(dc, rect, yMin, yMax, SignalProcessor.AxisSuffix(mode));
        DrawTimeAxis(dc, rect, fromTicks, windowTicks, showTimeLabels);

        if (!anyData)
        {
            DrawCenteredText(dc, rect, "Waiting for data…");
        }

        // 3) Lines, clipped to the plot area.
        dc.PushClip(new RectangleGeometry(rect));
        for (int i = 0; i < series.Count; i++)
            DrawSeries(dc, rect, _pool[i], series[i].Color, fromTicks, windowTicks, yMin, yMax);
        dc.Pop();

        if (title is not null)
        {
            var ft = MakeText(title, GetStyle(series[0].Color).Brush, 11.5, _titleTypeface);
            dc.DrawText(ft, new Point(rect.Left + 8, rect.Top + 5));
        }
    }

    /// <summary>
    /// Auto-scale with hysteresis: expand instantly when data leaves the range,
    /// shrink gradually so the axis doesn't jitter on every frame.
    /// </summary>
    private (double Min, double Max) UpdateRange(object key, double min, double max)
    {
        double span = max - min;
        double pad = span < 1e-9 ? Math.Max(Math.Abs(max) * 0.05, 0.5) : span * 0.12;
        double targetMin = min - pad, targetMax = max + pad;

        if (!_ranges.TryGetValue(key, out var current))
        {
            current = (targetMin, targetMax);
        }
        else
        {
            current.Min = targetMin < current.Min ? targetMin : current.Min + (targetMin - current.Min) * 0.05;
            current.Max = targetMax > current.Max ? targetMax : current.Max + (targetMax - current.Max) * 0.05;
        }

        _ranges[key] = current;
        return current;
    }

    private void DrawValueAxis(DrawingContext dc, Rect rect, double yMin, double yMax, string suffix)
    {
        double step = NiceStep((yMax - yMin) / Math.Max(2, rect.Height / 42));
        string format = step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00";

        for (double v = Math.Ceiling(yMin / step) * step; v <= yMax; v += step)
        {
            double y = Math.Round(rect.Bottom - (v - yMin) / (yMax - yMin) * rect.Height) + 0.5;
            dc.DrawLine(GridPen, new Point(rect.Left, y), new Point(rect.Right, y));

            // Avoid "-0" labels from floating-point rounding.
            double shown = Math.Abs(v) < step * 1e-6 ? 0 : v;
            var ft = MakeText(shown.ToString(format, CultureInfo.CurrentCulture) + suffix, AxisTextBrush, 11, _typeface);
            dc.DrawText(ft, new Point(rect.Left - 8 - ft.Width, y - ft.Height / 2));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, Rect rect, long fromTicks, long windowTicks, bool showLabels)
    {
        double windowSeconds = windowTicks / (double)TimeSpan.TicksPerSecond;
        double targetStep = windowSeconds / Math.Max(2, rect.Width / 110);
        double stepSeconds = TimeStepsSeconds.FirstOrDefault(s => s >= targetStep);
        if (stepSeconds == 0) stepSeconds = TimeStepsSeconds[^1];

        long stepTicks = (long)(stepSeconds * TimeSpan.TicksPerSecond);
        long toTicks = fromTicks + windowTicks;

        // Ticks aligned to the wall clock, so grid lines scroll with the data.
        for (long t = (fromTicks + stepTicks - 1) / stepTicks * stepTicks; t <= toTicks; t += stepTicks)
        {
            double x = Math.Round(rect.Left + (t - fromTicks) / (double)windowTicks * rect.Width) + 0.5;
            dc.DrawLine(GridPen, new Point(x, rect.Top), new Point(x, rect.Bottom));

            if (!showLabels) continue;
            string label = new DateTime(t, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            var ft = MakeText(label, AxisTextBrush, 11, _typeface);
            double lx = x - ft.Width / 2;
            if (lx < rect.Left - 4 || lx + ft.Width > rect.Right + 4) continue;
            dc.DrawText(ft, new Point(lx, rect.Bottom + 6));
        }
    }

    private void DrawSeries(DrawingContext dc, Rect rect, List<DataPoint> pts, Color color,
        long fromTicks, long windowTicks, double yMin, double yMax)
    {
        if (pts.Count == 0) return;
        var (pen, brush) = GetStyle(color);

        double xScale = rect.Width / windowTicks;
        double yScale = rect.Height / (yMax - yMin);
        double X(DataPoint p) => rect.Left + (p.Ticks - fromTicks) * xScale;
        double Y(DataPoint p) => rect.Bottom - (p.Value - yMin) * yScale;

        _points.Clear();

        if (pts.Count <= rect.Width * 2)
        {
            foreach (var p in pts) _points.Add(new Point(X(p), Y(p)));
        }
        else
        {
            // Min/max decimation per pixel column - preserves spikes, caps point count.
            int column = int.MinValue;
            double minY = 0, maxY = 0;
            int minIndex = 0, maxIndex = 0;

            void Flush()
            {
                if (column == int.MinValue) return;
                if (minIndex == maxIndex)
                {
                    _points.Add(new Point(column, minY));
                }
                else if (minIndex < maxIndex)
                {
                    _points.Add(new Point(column, minY));
                    _points.Add(new Point(column, maxY));
                }
                else
                {
                    _points.Add(new Point(column, maxY));
                    _points.Add(new Point(column, minY));
                }
            }

            for (int i = 0; i < pts.Count; i++)
            {
                int col = (int)X(pts[i]);
                double y = Y(pts[i]);
                if (col != column)
                {
                    Flush();
                    column = col;
                    minY = maxY = y;
                    minIndex = maxIndex = i;
                }
                else
                {
                    if (y < minY) { minY = y; minIndex = i; }
                    if (y > maxY) { maxY = y; maxIndex = i; }
                }
            }
            Flush();
        }

        // Sample markers: makes each sensor's frame rate (or the common
        // resampling grid) visible. Only when points are at least ~5 px apart.
        if (ShowSamplePoints && pts.Count > 1 && pts.Count <= rect.Width / 5)
        {
            foreach (var p in pts)
            {
                double x = X(p);
                if (x < rect.Left - 2 || x > rect.Right + 2) continue;
                dc.DrawEllipse(brush, null, new Point(x, Y(p)), 2.2, 2.2);
            }
        }

        if (_points.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_points[0], isFilled: false, isClosed: false);
                ctx.PolyLineTo(_points.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        // Marker on the newest sample.
        var last = pts[^1];
        dc.DrawEllipse(brush, null, new Point(X(last), Y(last)), 3, 3);
    }

    private string LaneTitle(IChartSeries s) => NormalizationMode switch
    {
        NormalizationMode.MinMax => $"{s.Name}  ·  min–max",
        NormalizationMode.ZScore => $"{s.Name}  ·  z-score",
        NormalizationMode.NominalRange => $"{s.Name}  ·  % of {s.NominalMin:0}–{s.NominalMax:0} {s.Unit}",
        _ => $"{s.Name} ({s.Unit})",
    };

    private (Pen Pen, Brush Brush) GetStyle(Color color)
    {
        if (_styles.TryGetValue(color, out var style)) return style;

        var brush = Frozen(new SolidColorBrush(color));
        var pen = Frozen(new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round });
        style = (pen, brush);
        _styles[color] = style;
        return style;
    }

    private void DrawCenteredText(DrawingContext dc, Rect rect, string text)
    {
        var ft = MakeText(text, AxisTextBrush, 13, _typeface);
        dc.DrawText(ft, new Point(rect.Left + (rect.Width - ft.Width) / 2, rect.Top + (rect.Height - ft.Height) / 2));
    }

    private FormattedText MakeText(string text, Brush brush, double size, Typeface typeface) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, _pixelsPerDip);

    private static double NiceStep(double rough)
    {
        if (rough <= 0 || double.IsNaN(rough) || double.IsInfinity(rough)) return 1;
        double exponent = Math.Floor(Math.Log10(rough));
        double magnitude = Math.Pow(10, exponent);
        double fraction = rough / magnitude;
        double nice = fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10;
        return nice * magnitude;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
