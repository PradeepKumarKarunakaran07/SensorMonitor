using System.Threading.Channels;
using SensorMonitor.Models;
using SensorMonitor.Shared;

namespace SensorMonitor.Services;

/// <summary>A sensor has connected (TCP) or registered (UDP) and introduced itself.</summary>
public sealed class SensorConnectedEventArgs(int sensorId, SensorInfo info, string transport) : EventArgs
{
    /// <summary>Id assigned by the monitor, unique across all transports.</summary>
    public int SensorId { get; } = sensorId;
    public SensorInfo Info { get; } = info;

    /// <summary>"TCP" or "UDP".</summary>
    public string Transport { get; } = transport;
}

/// <summary>
/// Where sensor data comes from. Implementations: TCP server, UDP receiver, and a
/// composite that combines them. The rest of the app only sees this interface.
/// </summary>
public interface ISensorDataSource : IDisposable
{
    /// <summary>All readings from all sensors, in arrival order.</summary>
    ChannelReader<SensorReading> Readings { get; }

    /// <summary>Raised on a background thread when a sensor connects.</summary>
    event EventHandler<SensorConnectedEventArgs>? SensorConnected;

    /// <summary>Raised on a background thread when a sensor goes away. Arg: sensor id.</summary>
    event EventHandler<int>? SensorDisconnected;

    bool IsStreaming { get; }

    /// <summary>Starts accepting sensors. Throws SocketException if a port is in use.</summary>
    void Start();

    /// <summary>Tells every sensor (and any that connect later) to stream or pause.</summary>
    void SetStreaming(bool streaming);

    void SetRate(int sensorId, double rateHz);

    /// <summary>Asks one sensor to disconnect and exit.</summary>
    void Shutdown(int sensorId);

    /// <summary>Short link-health text for the UI (e.g. packet loss), or null if this source doesn't own the sensor.</summary>
    string? GetLinkStatus(int sensorId);
}

/// <summary>Hands out sensor ids that are unique across all data sources.</summary>
public static class SensorIds
{
    private static int _last;
    public static int Next() => Interlocked.Increment(ref _last);
}
