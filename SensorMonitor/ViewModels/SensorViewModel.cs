using System.Windows.Media;
using SensorMonitor.Controls;
using SensorMonitor.Models;
using SensorMonitor.Shared;

namespace SensorMonitor.ViewModels;

/// <summary>
/// One connected sensor as shown in the UI: identity (from its Hello message),
/// colour, frame rate, connection state, live stats and its sample buffer.
/// </summary>
public sealed class SensorViewModel : ObservableObject, IChartSeries
{
    private static readonly TimeSpan RateMeasureWindow = TimeSpan.FromSeconds(3);

    private readonly List<DataPoint> _scratch = new();
    private double _sampleRateHz;
    private bool _isVisible = true;
    private bool _isConnected = true;
    private string _linkStatus = "";
    private double? _latest;
    private double _min, _max, _average, _measuredRate;

    public SensorViewModel(int id, SensorInfo info, string transport, Color color)
    {
        Id = id;
        Info = info;
        Transport = transport;
        _sampleRateHz = info.SampleRateHz;
        Color = color;
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    /// <summary>Raised when the user picks a new frame rate; the owner sends SetRate to the sensor.</summary>
    public event EventHandler? RateChanged;

    /// <summary>Monitor-assigned connection id.</summary>
    public int Id { get; }

    /// <summary>What the sensor announced about itself.</summary>
    public SensorInfo Info { get; }

    public string Name => Info.Name;
    public string Unit => Info.Unit;
    public double NominalMin => Info.NominalMin;
    public double NominalMax => Info.NominalMax;
    /// <summary>"TCP" or "UDP".</summary>
    public string Transport { get; }

    public string ProcessText => $"Separate process · PID {Info.ProcessId} · {Transport}";

    /// <summary>Connection health from the data source, e.g. "UDP · 3 lost (0.2%) · 0 late".</summary>
    public string LinkStatus
    {
        get => _linkStatus;
        set => SetProperty(ref _linkStatus, value);
    }

    public IReadOnlyList<double> AvailableRates => SensorTemplates.AvailableRates;

    public double SampleRateHz
    {
        get => _sampleRateHz;
        set
        {
            if (value <= 0 || !SetProperty(ref _sampleRateHz, value)) return;
            RateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Color Color { get; }
    public SolidColorBrush Brush { get; }

    /// <summary>5 minutes at 100 Hz; plenty for the 2-minute max window.</summary>
    public TimeSeriesBuffer Buffer { get; } = new(30_000);

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>False once the sensor's socket closes (process exited or crashed).</summary>
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value)) OnPropertyChanged(nameof(MeasuredRateText));
        }
    }

    public string LatestText => _latest is { } v ? $"{v:0.00} {Unit}" : "—";

    public string StatsText => _latest is null
        ? $"range {NominalMin:0}–{NominalMax:0} {Unit}"
        : $"min {_min:0.0}   max {_max:0.0}   avg {_average:0.0}";

    /// <summary>Frames actually received per second (lower than configured when frames drop).</summary>
    public string MeasuredRateText => !IsConnected
        ? "offline"
        : _measuredRate > 0 ? $"{_measuredRate:0.#} fps" : "";

    /// <summary>Recomputes latest/min/max/avg over the visible window and the measured frame rate. UI thread.</summary>
    public void RefreshStats(TimeSpan window)
    {
        var now = DateTime.UtcNow;
        Buffer.CopySince((now - window).Ticks, _scratch);

        if (_scratch.Count == 0)
        {
            _latest = null;
            _measuredRate = 0;
        }
        else
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            int recent = 0;
            long recentFrom = (now - RateMeasureWindow).Ticks;
            foreach (var p in _scratch)
            {
                if (p.Value < min) min = p.Value;
                if (p.Value > max) max = p.Value;
                sum += p.Value;
                if (p.Ticks >= recentFrom) recent++;
            }
            _min = min;
            _max = max;
            _average = sum / _scratch.Count;
            _latest = _scratch[^1].Value;
            _measuredRate = recent / Math.Min(RateMeasureWindow.TotalSeconds, window.TotalSeconds);
        }

        OnPropertyChanged(nameof(LatestText));
        OnPropertyChanged(nameof(StatsText));
        OnPropertyChanged(nameof(MeasuredRateText));
    }
}
