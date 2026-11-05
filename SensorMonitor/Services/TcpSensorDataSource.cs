using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using SensorMonitor.Models;
using SensorMonitor.Shared;

namespace SensorMonitor.Services;

/// <summary>
/// TCP server that sensor processes connect to (UDP sensors: see UdpSensorDataSource).
///
///   sensor process --TCP--> [connection task] --+
///   sensor process --TCP--> [connection task] --+--> Channel&lt;SensorReading&gt; --> app
///   sensor process --TCP--> [connection task] --+
///
/// Each connection gets its own task that reads frames (see <see cref="Protocol"/>)
/// and pushes decoded samples into one shared channel - so the rest of the app
/// works exactly as it did with in-process sensors.
/// Listens on loopback only, so no firewall prompt and nothing exposed to the network.
/// </summary>
public sealed class TcpSensorDataSource : ISensorDataSource
{
    private readonly Channel<SensorReading> _channel =
        Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false, // one writer per connection
        });

    private readonly ConcurrentDictionary<int, SensorConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private volatile bool _isStreaming;

    public TcpSensorDataSource(int port = Protocol.DefaultPort)
    {
        Port = port;
    }

    public int Port { get; }

    public ChannelReader<SensorReading> Readings => _channel.Reader;

    public event EventHandler<SensorConnectedEventArgs>? SensorConnected;
    public event EventHandler<int>? SensorDisconnected;

    public bool IsStreaming => _isStreaming;

    /// <summary>Starts listening. Throws <see cref="SocketException"/> if the port is in use.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void SetStreaming(bool streaming)
    {
        _isStreaming = streaming;
        var command = streaming ? MessageType.Start : MessageType.Stop;
        foreach (var connection in _connections.Values)
            _ = connection.SendAsync(command, null);
    }

    public void SetRate(int sensorId, double rateHz)
    {
        if (_connections.TryGetValue(sensorId, out var connection))
            _ = connection.SendAsync(MessageType.SetRate, Protocol.EncodeRate(rateHz));
    }

    public void Shutdown(int sensorId)
    {
        if (_connections.TryGetValue(sensorId, out var connection))
            _ = connection.SendAsync(MessageType.Shutdown, null);
    }

    public string? GetLinkStatus(int sensorId) =>
        _connections.ContainsKey(sensorId) ? "TCP · reliable, ordered" : null;

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                                           or SocketException or InvalidOperationException)
            {
                break; // listener stopped
            }

            client.NoDelay = true;
            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        int id = SensorIds.Next();
        SensorConnection? connection = null;

        try
        {
            var stream = client.GetStream();

            // 1) Handshake: the first frame must be Hello, within 5 seconds.
            Frame? hello;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                hello = await Protocol.ReadFrameAsync(stream, timeout.Token);
            }
            if (hello is null || hello.Type != MessageType.Hello)
                return;

            var info = Protocol.DecodeHello(hello.Payload);
            connection = new SensorConnection(id, client, stream);
            _connections[id] = connection;

            // Subscribers register the sensor before we tell it to start, so no
            // early readings are dropped for an unknown id.
            SensorConnected?.Invoke(this, new SensorConnectedEventArgs(id, info, "TCP"));
            if (_isStreaming)
                await connection.SendAsync(MessageType.Start, null);

            // 2) Receive loop.
            var samples = new List<Sample>(256);
            while (!ct.IsCancellationRequested)
            {
                var frame = await Protocol.ReadFrameAsync(stream, ct);
                if (frame is null) break; // sensor closed the connection

                if (frame.Type != MessageType.Readings) continue; // ignore anything unexpected

                Protocol.DecodeReadings(frame.Payload, samples);
                foreach (var s in samples)
                {
                    if (s.UtcTicks <= DateTime.MinValue.Ticks || s.UtcTicks >= DateTime.MaxValue.Ticks) continue;
                    _channel.Writer.TryWrite(new SensorReading(id, new DateTime(s.UtcTicks, DateTimeKind.Utc), s.Value));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                       or ObjectDisposedException or InvalidDataException or JsonException)
        {
            // Sensor crashed, was killed, sent garbage, or we're shutting down.
            Debug.WriteLine($"Sensor connection {id} ended: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            if (connection is not null && _connections.TryRemove(id, out _))
            {
                connection.Dispose();
                if (!ct.IsCancellationRequested)
                    SensorDisconnected?.Invoke(this, id);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // already stopped
        }

        // Closing the sockets makes each sensor see end-of-stream and exit.
        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();
        _channel.Writer.TryComplete();
    }

    /// <summary>One connected sensor. Serializes writes so two commands never interleave on the wire.</summary>
    private sealed class SensorConnection(int id, TcpClient client, NetworkStream stream) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public int Id { get; } = id;

        public async Task SendAsync(MessageType type, byte[]? payload)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                await _writeLock.WaitAsync();
                try
                {
                    await Protocol.WriteFrameAsync(stream, type, payload, CancellationToken.None);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // Connection is going away; the receive loop will report the disconnect.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            client.Dispose();
        }
    }
}
