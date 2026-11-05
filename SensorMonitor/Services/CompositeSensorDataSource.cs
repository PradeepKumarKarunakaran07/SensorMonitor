using System.Collections.Concurrent;
using System.Threading.Channels;
using SensorMonitor.Models;

namespace SensorMonitor.Services;

/// <summary>
/// Combines several data sources (here: TCP + UDP) into one, so the view model
/// doesn't care how each sensor is connected.
///
///   TcpSensorDataSource.Readings --+
///                                  +--> forwarding tasks --> one Channel --> app
///   UdpSensorDataSource.Readings --+
///
/// Commands (SetRate, Shutdown) are routed to whichever source owns the sensor.
/// </summary>
public sealed class CompositeSensorDataSource : ISensorDataSource
{
    private readonly ISensorDataSource[] _sources;
    private readonly ConcurrentDictionary<int, ISensorDataSource> _owner = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SensorReading> _channel =
        Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private bool _isStreaming;

    public CompositeSensorDataSource(params ISensorDataSource[] sources)
    {
        _sources = sources;
        foreach (var source in sources)
        {
            var s = source;
            s.SensorConnected += (_, e) =>
            {
                _owner[e.SensorId] = s;
                SensorConnected?.Invoke(this, e);
            };
            s.SensorDisconnected += (_, id) =>
            {
                _owner.TryRemove(id, out var _); // `out var _` is always a discard; plain `_` here is the lambda's sender parameter
                SensorDisconnected?.Invoke(this, id);
            };
        }
    }

    public ChannelReader<SensorReading> Readings => _channel.Reader;

    public event EventHandler<SensorConnectedEventArgs>? SensorConnected;
    public event EventHandler<int>? SensorDisconnected;

    public bool IsStreaming => _isStreaming;

    public void Start()
    {
        foreach (var source in _sources)
        {
            source.Start(); // throws if a port is in use
            _ = ForwardAsync(source, _cts.Token);
        }
    }

    public void SetStreaming(bool streaming)
    {
        _isStreaming = streaming;
        foreach (var source in _sources) source.SetStreaming(streaming);
    }

    public void SetRate(int sensorId, double rateHz)
    {
        if (_owner.TryGetValue(sensorId, out var source)) source.SetRate(sensorId, rateHz);
    }

    public void Shutdown(int sensorId)
    {
        if (_owner.TryRemove(sensorId, out var source)) source.Shutdown(sensorId);
    }

    public string? GetLinkStatus(int sensorId) =>
        _owner.TryGetValue(sensorId, out var source) ? source.GetLinkStatus(sensorId) : null;

    private async Task ForwardAsync(ISensorDataSource source, CancellationToken ct)
    {
        try
        {
            await foreach (var reading in source.Readings.ReadAllAsync(ct).ConfigureAwait(false))
                _channel.Writer.TryWrite(reading);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var source in _sources) source.Dispose();
        _channel.Writer.TryComplete();
    }
}
