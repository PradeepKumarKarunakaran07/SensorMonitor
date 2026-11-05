using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SensorMonitor.Processing;
using SensorMonitor.Services;
using SensorMonitor.Shared;

namespace SensorMonitor.ViewModels;

/// <summary>Label/value pair for ComboBoxes bound to enums or numbers.</summary>
public sealed record Option<T>(string Label, T Value);

/// <summary>
/// Wires everything together:
///   sensor processes --TCP--> TcpSensorDataSource --+
///   sensor processes --UDP--> UdpSensorDataSource --+--> CompositeSensorDataSource --channel--> consumer --> buffers
///   chart (UI thread, every frame) and stats (UI thread, 4x/sec) read the buffers.
/// Sensors appear in the UI when their process connects and says Hello,
/// and are marked offline when their connection drops.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const int MaxSensors = 8;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0xC3, 0xF7), // sky
        Color.FromRgb(0xFF, 0xB7, 0x4D), // amber
        Color.FromRgb(0x81, 0xC7, 0x84), // green
        Color.FromRgb(0xF0, 0x62, 0x92), // pink
        Color.FromRgb(0xBA, 0x68, 0xC8), // violet
        Color.FromRgb(0xFF, 0xF1, 0x76), // yellow
        Color.FromRgb(0x4D, 0xB6, 0xAC), // teal
        Color.FromRgb(0xE5, 0x73, 0x73), // red
    };

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sensor types launched over UDP; everything else uses TCP. Vibration is the
    /// classic UDP case: high rate, and a lost sample or two doesn't matter.
    /// </summary>
    private static readonly HashSet<string> UdpTemplates = new(StringComparer.OrdinalIgnoreCase) { "vibration" };

    private readonly ISensorDataSource _source;
    private readonly SensorProcessManager _processes;
    private readonly int _tcpPort;
    private readonly int _udpPort;
    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<int, SensorViewModel> _byId = new();
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly List<DateTime> _pendingLaunches = new(); // launched, not yet connected

    private long _totalSamples;
    private long _lastTotal;
    private DateTime _lastStatsTime = DateTime.UtcNow;
    private int _launchCount;
    private bool _isListening;

    private bool _isStreaming;
    private bool _isSplitView;
    private bool _showSensorConsoles;
    private string _statusText = "";
    private double _timeWindowSeconds = 30;
    private double _samplesPerSecond;
    private NormalizationMode _normalizationMode = NormalizationMode.MinMax;
    private ResampleMethod _resampleMethod = ResampleMethod.Off;
    private double _resampleRateHz = 10;
    private bool _showSamplePoints = true;

    public MainViewModel()
        : this(new CompositeSensorDataSource(
                   new TcpSensorDataSource(Protocol.DefaultPort),
                   new UdpSensorDataSource(Protocol.DefaultUdpPort)),
               new SensorProcessManager(),
               Protocol.DefaultPort,
               Protocol.DefaultUdpPort)
    {
    }

    public MainViewModel(ISensorDataSource source, SensorProcessManager processes, int tcpPort, int udpPort)
    {
        _source = source;
        _processes = processes;
        _tcpPort = tcpPort;
        _udpPort = udpPort;
        _dispatcher = Dispatcher.CurrentDispatcher;

        ToggleStreamingCommand = new RelayCommand(_ => ToggleStreaming());
        AddSensorCommand = new RelayCommand(_ => LaunchSensor(), _ => CanLaunchSensor());
        RemoveSensorCommand = new RelayCommand(p => { if (p is SensorViewModel s) RemoveSensor(s); });
        ClearCommand = new RelayCommand(_ => ClearData());

        _source.SensorConnected += OnSensorConnected;
        _source.SensorDisconnected += OnSensorDisconnected;
        _ = Task.Run(() => ConsumeAsync(_consumerCts.Token));

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        IsStreaming = true;
        _source.SetStreaming(true); // sensors start streaming as soon as they connect

        try
        {
            _source.Start();
            _isListening = true;
            UpdateStatus();
            for (int i = 0; i < 4; i++) LaunchSensor();
        }
        catch (SocketException ex)
        {
            StatusText = $"Can't listen on TCP {_tcpPort} / UDP {_udpPort}: {ex.Message}";
        }
    }

    public ObservableCollection<SensorViewModel> Sensors { get; } = new();

    public ICommand ToggleStreamingCommand { get; }
    public ICommand AddSensorCommand { get; }
    public ICommand RemoveSensorCommand { get; }
    public ICommand ClearCommand { get; }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetProperty(ref _isStreaming, value);
    }

    public bool IsSplitView
    {
        get => _isSplitView;
        set => SetProperty(ref _isSplitView, value);
    }

    /// <summary>Launch new sensor processes with a visible console window.</summary>
    public bool ShowSensorConsoles
    {
        get => _showSensorConsoles;
        set => SetProperty(ref _showSensorConsoles, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double TimeWindowSeconds
    {
        get => _timeWindowSeconds;
        set => SetProperty(ref _timeWindowSeconds, Math.Clamp(value, 5, 120));
    }

    public double SamplesPerSecond
    {
        get => _samplesPerSecond;
        private set => SetProperty(ref _samplesPerSecond, value);
    }

    public long TotalSamples => Interlocked.Read(ref _totalSamples);

    // ───── Normalization ─────

    public IReadOnlyList<Option<NormalizationMode>> NormalizationOptions { get; } = new[]
    {
        new Option<NormalizationMode>("Raw values (own units)", NormalizationMode.Raw),
        new Option<NormalizationMode>("Min–max  (0 – 1)", NormalizationMode.MinMax),
        new Option<NormalizationMode>("Z-score  (σ from mean)", NormalizationMode.ZScore),
        new Option<NormalizationMode>("% of nominal range", NormalizationMode.NominalRange),
    };

    public IReadOnlyList<Option<ResampleMethod>> ResampleOptions { get; } = new[]
    {
        new Option<ResampleMethod>("Off (native frame rates)", ResampleMethod.Off),
        new Option<ResampleMethod>("Linear interpolation", ResampleMethod.Linear),
        new Option<ResampleMethod>("Sample & hold", ResampleMethod.SampleAndHold),
    };

    public IReadOnlyList<double> CommonRates { get; } = new double[] { 1, 5, 10, 20, 50 };

    /// <summary>How values are scaled so sensors with different units can share one chart.</summary>
    public NormalizationMode NormalizationMode
    {
        get => _normalizationMode;
        set
        {
            if (SetProperty(ref _normalizationMode, value)) OnPropertyChanged(nameof(NormalizationHint));
        }
    }

    /// <summary>How sensors with different frame rates are aligned onto one time base.</summary>
    public ResampleMethod ResampleMethod
    {
        get => _resampleMethod;
        set
        {
            if (SetProperty(ref _resampleMethod, value))
            {
                OnPropertyChanged(nameof(IsResampling));
                OnPropertyChanged(nameof(ResampleHint));
            }
        }
    }

    /// <summary>Common rate every sensor is resampled to when <see cref="ResampleMethod"/> is not Off.</summary>
    public double ResampleRateHz
    {
        get => _resampleRateHz;
        set
        {
            if (SetProperty(ref _resampleRateHz, value)) OnPropertyChanged(nameof(ResampleHint));
        }
    }

    public bool IsResampling => ResampleMethod != ResampleMethod.Off;

    public bool ShowSamplePoints
    {
        get => _showSamplePoints;
        set => SetProperty(ref _showSamplePoints, value);
    }

    public string NormalizationHint => NormalizationMode switch
    {
        NormalizationMode.Raw => "Each sensor in its own unit. Best with split view.",
        NormalizationMode.MinMax => "Each sensor scaled to 0–1 over the visible window.",
        NormalizationMode.ZScore => "Distance from each sensor's mean, in standard deviations.",
        NormalizationMode.NominalRange => "Position within each sensor's rated range.",
        _ => "",
    };

    public string ResampleHint => ResampleMethod switch
    {
        ResampleMethod.Off => "Each sensor plotted at its own frame rate.",
        ResampleMethod.Linear => $"All sensors aligned to a {ResampleRateHz:0} Hz grid. Faster sensors are averaged, slower ones interpolated.",
        ResampleMethod.SampleAndHold => $"All sensors aligned to a {ResampleRateHz:0} Hz grid. Faster sensors are averaged, slower ones hold their last value.",
        _ => "",
    };

    private TimeSpan Window => TimeSpan.FromSeconds(TimeWindowSeconds);

    // ───── Streaming control ─────

    private void ToggleStreaming()
    {
        IsStreaming = !IsStreaming;
        _source.SetStreaming(IsStreaming); // sends Start / Stop to every sensor process
    }

    // ───── Sensor processes ─────

    private bool CanLaunchSensor()
    {
        _pendingLaunches.RemoveAll(t => DateTime.UtcNow - t > LaunchTimeout);
        return _isListening && Sensors.Count + _pendingLaunches.Count < MaxSensors;
    }

    /// <summary>Starts a new sensor process. It shows up in the UI once it connects.</summary>
    private void LaunchSensor()
    {
        if (!CanLaunchSensor()) return;

        var templates = SensorTemplates.All;
        var template = templates[_launchCount % templates.Count];
        string name = $"{template.Name} #{_launchCount / templates.Count + 1}";
        _launchCount++;

        bool udp = UdpTemplates.Contains(template.Key);
        _processes.ShowConsoleWindows = ShowSensorConsoles;
        string? error = udp
            ? _processes.Launch(template.Key, name, template.DefaultRateHz, "udp", _udpPort)
            : _processes.Launch(template.Key, name, template.DefaultRateHz, "tcp", _tcpPort);
        if (error is null)
        {
            _pendingLaunches.Add(DateTime.UtcNow);
            StatusText = $"Starting process for {name} ({(udp ? "UDP" : "TCP")})…";
        }
        else
        {
            StatusText = error;
        }
    }

    // Raised on the connection's background thread.
    private void OnSensorConnected(object? sender, SensorConnectedEventArgs e)
    {
        try
        {
            // Invoke (not BeginInvoke): the sensor is registered before the data
            // source tells it to Start, so its first readings aren't dropped.
            _dispatcher.Invoke(() => AddConnectedSensor(e));
        }
        catch (OperationCanceledException)
        {
            // app is shutting down
        }
    }

    private void AddConnectedSensor(SensorConnectedEventArgs e)
    {
        if (_pendingLaunches.Count > 0) _pendingLaunches.RemoveAt(0);

        if (Sensors.Count >= MaxSensors)
        {
            _source.Shutdown(e.SensorId);
            StatusText = $"Rejected '{e.Info.Name}': maximum of {MaxSensors} sensors.";
            return;
        }

        var vm = new SensorViewModel(e.SensorId, e.Info, e.Transport, PickColor());
        vm.RateChanged += OnSensorRateChanged;
        _byId[e.SensorId] = vm;
        Sensors.Add(vm);
        UpdateStatus();
    }

    // Raised on the connection's background thread.
    private void OnSensorDisconnected(object? sender, int sensorId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            _byId.TryRemove(sensorId, out _);
            var vm = Sensors.FirstOrDefault(s => s.Id == sensorId);
            if (vm is not null) vm.IsConnected = false; // keep its data on screen, marked offline
            UpdateStatus();
        });
    }

    private void OnSensorRateChanged(object? sender, EventArgs e)
    {
        if (sender is SensorViewModel vm && vm.IsConnected)
            _source.SetRate(vm.Id, vm.SampleRateHz); // SetRate message over the socket
    }

    private void RemoveSensor(SensorViewModel sensor)
    {
        sensor.RateChanged -= OnSensorRateChanged;
        if (sensor.IsConnected) _source.Shutdown(sensor.Id); // the process exits by itself (TCP or UDP)
        _byId.TryRemove(sensor.Id, out _);
        Sensors.Remove(sensor);
        UpdateStatus();

        // Safety net: if a process we started ignores Shutdown, kill it.
        int pid = sensor.Info.ProcessId;
        _ = Task.Delay(2000).ContinueWith(_ => _processes.Kill(pid), TaskScheduler.Default);
    }

    private void UpdateStatus()
    {
        if (!_isListening) return;
        int online = Sensors.Count(s => s.IsConnected);
        StatusText = $"Listening on 127.0.0.1 · TCP {_tcpPort} · UDP {_udpPort} · " +
                     $"{online} sensor process{(online == 1 ? "" : "es")} connected";
    }

    // ───── Data ─────

    private void ClearData()
    {
        foreach (var s in Sensors)
        {
            s.Buffer.Clear();
            s.RefreshStats(Window);
        }
    }

    private Color PickColor()
    {
        // First palette colour not currently in use.
        foreach (var c in Palette)
            if (Sensors.All(s => s.Color != c)) return c;
        return Palette[Sensors.Count % Palette.Length];
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var reading in _source.Readings.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_byId.TryGetValue(reading.SensorId, out var sensor))
                    sensor.Buffer.Add(reading.Timestamp, reading.Value);
                Interlocked.Increment(ref _totalSamples);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void RefreshStats()
    {
        foreach (var s in Sensors)
        {
            s.RefreshStats(Window);
            if (s.IsConnected) s.LinkStatus = _source.GetLinkStatus(s.Id) ?? s.Transport;
            else if (!s.LinkStatus.EndsWith("· offline", StringComparison.Ordinal)) s.LinkStatus += " · offline";
        }

        var now = DateTime.UtcNow;
        long total = TotalSamples;
        double seconds = (now - _lastStatsTime).TotalSeconds;
        if (seconds > 0) SamplesPerSecond = (total - _lastTotal) / seconds;
        _lastTotal = total;
        _lastStatsTime = now;
        OnPropertyChanged(nameof(TotalSamples));
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        _consumerCts.Cancel();
        _source.SensorConnected -= OnSensorConnected;
        _source.SensorDisconnected -= OnSensorDisconnected;
        _source.Dispose();     // closes every socket -> each sensor process sees EOF and exits
        _processes.Dispose();  // waits briefly, then kills any process that didn't exit
    }
}
