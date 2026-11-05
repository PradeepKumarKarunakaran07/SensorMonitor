namespace SensorMonitor.Models;

/// <summary>
/// Fixed-capacity, thread-safe ring buffer of time-ordered samples.
/// Written by the background consumer, read by the UI (chart + stats).
/// When full, the oldest sample is overwritten.
/// </summary>
public sealed class TimeSeriesBuffer
{
    private readonly long[] _ticks;
    private readonly double[] _values;
    private readonly object _gate = new();
    private int _start;
    private int _count;

    public TimeSeriesBuffer(int capacity)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ticks = new long[capacity];
        _values = new double[capacity];
    }

    public int Capacity => _ticks.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(DateTime timestampUtc, double value)
    {
        lock (_gate)
        {
            int p;
            if (_count < Capacity)
            {
                p = Physical(_count);
                _count++;
            }
            else
            {
                p = _start;                       // overwrite oldest
                _start = (_start + 1) % Capacity;
            }
            _ticks[p] = timestampUtc.Ticks;
            _values[p] = value;
        }
    }

    /// <summary>
    /// Copies every sample with timestamp &gt;= <paramref name="fromTicks"/> into
    /// <paramref name="destination"/> (which is cleared first). With
    /// <paramref name="includePrevious"/>, the last sample before the cut-off is
    /// included too so a plotted line enters cleanly from the left edge.
    /// </summary>
    public void CopySince(long fromTicks, List<DataPoint> destination, bool includePrevious = false)
    {
        destination.Clear();
        lock (_gate)
        {
            if (_count == 0) return;

            // Scan backwards: the visible window is the newest part of the buffer.
            int first = _count;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_ticks[Physical(i)] < fromTicks) break;
                first = i;
            }
            if (includePrevious && first > 0) first--;

            for (int i = first; i < _count; i++)
            {
                int p = Physical(i);
                destination.Add(new DataPoint(_ticks[p], _values[p]));
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _count = 0;
        }
    }

    private int Physical(int logicalIndex) => (_start + logicalIndex) % Capacity;
}
