using SensorMonitor.Models;

namespace SensorMonitor.Processing;

/// <summary>How sensor values are scaled before plotting.</summary>
public enum NormalizationMode
{
    /// <summary>Plot values in their own engineering units.</summary>
    Raw,

    /// <summary>(v - min) / (max - min) over the visible window -> 0..1.</summary>
    MinMax,

    /// <summary>(v - mean) / stdDev over the visible window -> standard deviations.</summary>
    ZScore,

    /// <summary>(v - nominalMin) / (nominalMax - nominalMin) * 100 -> % of the sensor's rated range.</summary>
    NominalRange,
}

/// <summary>How sensors with different frame rates are aligned onto a common time base.</summary>
public enum ResampleMethod
{
    /// <summary>Plot every sample at its native rate and timestamp.</summary>
    Off,

    /// <summary>Common grid; values between samples are linearly interpolated.</summary>
    Linear,

    /// <summary>Common grid; each grid point holds the most recent sample (zero-order hold).</summary>
    SampleAndHold,
}

/// <summary>
/// Pure functions that turn a sensor's raw samples into the series that is drawn:
/// first time alignment (resampling to a common rate), then value normalization.
/// No state, no UI types - easy to unit test.
/// </summary>
public static class SignalProcessor
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// Resamples <paramref name="source"/> (time-ordered) onto a uniform grid of
    /// <paramref name="rateHz"/> aligned to absolute time, so every sensor shares
    /// the same timestamps regardless of its native frame rate.
    /// <list type="bullet">
    /// <item>Faster sensor than grid (2+ samples per grid cell): the cell's samples are
    /// averaged - an anti-aliasing low-pass instead of just picking one sample.</item>
    /// <item>Slower sensor than grid: values are filled by linear interpolation or
    /// sample-and-hold depending on <paramref name="method"/>.</item>
    /// </list>
    /// Linear stops at the newest sample (it can't interpolate into the future);
    /// sample-and-hold extends the last value up to <paramref name="toTicks"/>.
    /// </summary>
    public static void Resample(
        List<DataPoint> source,
        List<DataPoint> destination,
        long fromTicks,
        long toTicks,
        double rateHz,
        ResampleMethod method)
    {
        destination.Clear();
        if (source.Count == 0) return;

        if (method == ResampleMethod.Off || rateHz <= 0)
        {
            destination.AddRange(source);
            return;
        }

        long step = Math.Max(1, (long)(TimeSpan.TicksPerSecond / rateHz));
        long half = step / 2;

        long start = Math.Max(fromTicks, source[0].Ticks);
        long end = method == ResampleMethod.SampleAndHold
            ? Math.Max(toTicks, source[^1].Ticks)
            : Math.Min(toTicks, source[^1].Ticks);

        int count = source.Count;
        int last = 0;   // index of the last sample with Ticks <= t
        int lo = 0;     // first sample with Ticks >  t - half  (cell start)
        int hi = 0;     // first sample with Ticks >  t + half  (cell end)

        for (long t = CeilToMultiple(start, step); t <= end; t += step)
        {
            while (last + 1 < count && source[last + 1].Ticks <= t) last++;
            while (lo < count && source[lo].Ticks <= t - half) lo++;
            if (hi < lo) hi = lo;
            while (hi < count && source[hi].Ticks <= t + half) hi++;

            double value;
            int inCell = hi - lo;

            if (inCell >= 2)
            {
                // Downsampling: average everything that falls in this grid cell.
                double sum = 0;
                for (int k = lo; k < hi; k++) sum += source[k].Value;
                value = sum / inCell;
            }
            else if (method == ResampleMethod.SampleAndHold || last + 1 >= count)
            {
                value = source[last].Value;
            }
            else
            {
                // Upsampling: linear interpolation between the neighbours of t.
                var a = source[last];
                var b = source[last + 1];
                double f = b.Ticks == a.Ticks ? 0 : (t - a.Ticks) / (double)(b.Ticks - a.Ticks);
                value = a.Value + (b.Value - a.Value) * Math.Clamp(f, 0, 1);
            }

            destination.Add(new DataPoint(t, value));
        }
    }

    /// <summary>
    /// Normalizes values in place. Window-based modes (min-max, z-score) use the
    /// statistics of the points passed in, i.e. the visible window, so the scale
    /// adapts as data scrolls.
    /// </summary>
    public static void Normalize(List<DataPoint> points, NormalizationMode mode, double nominalMin, double nominalMax)
    {
        if (points.Count == 0 || mode == NormalizationMode.Raw) return;

        switch (mode)
        {
            case NormalizationMode.MinMax:
            {
                double min = double.MaxValue, max = double.MinValue;
                foreach (var p in points)
                {
                    if (p.Value < min) min = p.Value;
                    if (p.Value > max) max = p.Value;
                }
                double span = max - min;
                for (int i = 0; i < points.Count; i++)
                {
                    double v = span < Epsilon ? 0.5 : (points[i].Value - min) / span;
                    points[i] = points[i] with { Value = v };
                }
                break;
            }

            case NormalizationMode.ZScore:
            {
                double mean = 0;
                foreach (var p in points) mean += p.Value;
                mean /= points.Count;

                double variance = 0;
                foreach (var p in points) variance += (p.Value - mean) * (p.Value - mean);
                double std = Math.Sqrt(variance / points.Count);

                for (int i = 0; i < points.Count; i++)
                {
                    double v = std < Epsilon ? 0 : (points[i].Value - mean) / std;
                    points[i] = points[i] with { Value = v };
                }
                break;
            }

            case NormalizationMode.NominalRange:
            {
                double span = nominalMax - nominalMin;
                if (Math.Abs(span) < Epsilon) span = 1;
                for (int i = 0; i < points.Count; i++)
                {
                    double v = (points[i].Value - nominalMin) / span * 100.0;
                    points[i] = points[i] with { Value = v };
                }
                break;
            }
        }
    }

    /// <summary>Suffix for Y-axis labels in the given mode.</summary>
    public static string AxisSuffix(NormalizationMode mode) => mode switch
    {
        NormalizationMode.ZScore => "σ",
        NormalizationMode.NominalRange => "%",
        _ => string.Empty,
    };

    private static long CeilToMultiple(long value, long step)
    {
        long r = value % step;
        return r == 0 ? value : value + (step - r);
    }
}
