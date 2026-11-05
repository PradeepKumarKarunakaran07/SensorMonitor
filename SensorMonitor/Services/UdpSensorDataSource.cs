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
/// Receives sensors that talk UDP. One socket serves every UDP sensor; datagrams
/// are told apart by their sender's address (IP + port).
///
/// Because UDP has no connections:
/// <list type="bullet">
/// <item><b>Registration</b> - a sensor exists once its Hello arrives.</item>
/// <item><b>Liveness</b> - a sensor is offline when nothing has arrived for
///   <see cref="SilenceTimeout"/> (there is no "connection closed" event).</item>
/// <item><b>Commands</b> - instead of one-off Start/SetRate (which could be lost), we send
///   the full desired <c>State</c> in reply to every Heartbeat, so a lost command is
///   corrected within a second.</item>
/// <item><b>Loss and ordering</b> - every data datagram has a sequence number. Gaps are
///   counted as lost; anything older than the newest seen is counted as late and dropped
///   (the chart's buffers need samples in time order).</item>
/// </list>
/// </summary>
public sealed class UdpSensorDataSource : ISensorDataSource
{
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(3);

    private readonly Channel<SensorReading> _channel =
        Channel.CreateBounded<SensorReading>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true, // only the receive loop writes
        });

    private readonly ConcurrentDictionary<IPEndPoint, UdpSensor> _byEndpoint = new();
    private readonly ConcurrentDictionary<int, UdpSensor> _byId = new();
    private readonly ConcurrentDictionary<IPEndPoint, long> _shutdownRequested = new(); // endpoint -> TickCount64
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;
    private volatile bool _isStreaming;

    public UdpSensorDataSource(int port = Protocol.DefaultUdpPort)
    {
        Port = port;
    }

    public int Port { get; }

    public ChannelReader<SensorReading> Readings => _channel.Reader;

    public event EventHandler<SensorConnectedEventArgs>? SensorConnected;
    public event EventHandler<int>? SensorDisconnected;

    public bool IsStreaming => _isStreaming;

    public void Start()
    {
        if (_udp is not null) return;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
        UdpSocketHelper.IgnoreConnectionResets(_udp.Client);
        _ = ReceiveLoopAsync(_cts.Token);
        _ = LivenessLoopAsync(_cts.Token);
    }

    public void SetStreaming(bool streaming)
    {
        _isStreaming = streaming;
        foreach (var sensor in _byId.Values) SendState(sensor);
    }

    public void SetRate(int sensorId, double rateHz)
    {
        if (!_byId.TryGetValue(sensorId, out var sensor)) return;
        sensor.DesiredRateHz = rateHz;
        SendState(sensor); // and again with every heartbeat reply until the sensor reports it
    }

    public void Shutdown(int sensorId)
    {
        if (!_byId.TryRemove(sensorId, out var sensor)) return;
        _byEndpoint.TryRemove(sensor.Endpoint, out _);

        // Remember the endpoint: if Shutdown is lost, the sensor keeps sending and
        // gets another Shutdown in reply, instead of being treated as a new sensor.
        _shutdownRequested[sensor.Endpoint] = Environment.TickCount64;
        Send(MessageType.Shutdown, null, sensor.Endpoint);
    }

    public string? GetLinkStatus(int sensorId)
    {
        if (!_byId.TryGetValue(sensorId, out var s)) return null;
        long received = Interlocked.Read(ref s.Received);
        long lost = Interlocked.Read(ref s.Lost);
        long late = Interlocked.Read(ref s.Late);
        double lossPct = received + lost == 0 ? 0 : 100.0 * lost / (received + lost);
        return $"UDP · {lost:N0} lost ({lossPct:0.0}%) · {late:N0} late";
    }

    // ───────────── Receiving ─────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var samples = new List<Sample>(Protocol.MaxSamplesPerDatagram);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct);
            }
            catch (SocketException ex) when (UdpSocketHelper.IsConnectionReset(ex))
            {
                continue; // we sent to a sensor that has exited - harmless
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break; // shutting down
            }

            try
            {
                HandleDatagram(result.RemoteEndPoint, Protocol.ParseDatagram(result.Buffer), samples);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                // A bad datagram only costs that datagram - no connection to tear down.
                Debug.WriteLine($"Dropped bad UDP datagram from {result.RemoteEndPoint}: {ex.Message}");
            }
        }
    }

    private void HandleDatagram(IPEndPoint from, Frame frame, List<Sample> samples)
    {
        if (_shutdownRequested.ContainsKey(from))
        {
            Send(MessageType.Shutdown, null, from); // it missed the first one
            return;
        }

        _byEndpoint.TryGetValue(from, out var sensor);

        switch (frame.Type)
        {
            case MessageType.Hello:
                if (sensor is null)
                {
                    var info = Protocol.DecodeHello(frame.Payload);
                    sensor = new UdpSensor(SensorIds.Next(), from, info.SampleRateHz);
                    sensor.Touch();
                    _byEndpoint[from] = sensor;
                    _byId[sensor.Id] = sensor;
                    SensorConnected?.Invoke(this, new SensorConnectedEventArgs(sensor.Id, info, "UDP"));
                }
                sensor.Touch();
                SendState(sensor); // doubles as "registered" acknowledgement
                break;

            case MessageType.Heartbeat:
                if (sensor is null)
                {
                    Send(MessageType.Identify, null, from); // monitor restarted, or we timed it out
                    return;
                }
                sensor.Touch();
                SendState(sensor); // reply = keep-alive for the sensor + re-send any lost command
                break;

            case MessageType.SequencedReadings:
                if (sensor is null)
                {
                    Send(MessageType.Identify, null, from);
                    return;
                }
                sensor.Touch();

                uint sequence = Protocol.DecodeSequencedReadings(frame.Payload, samples);
                if (!sensor.AcceptSequence(sequence)) return; // late / duplicate

                foreach (var s in samples)
                {
                    if (s.UtcTicks <= DateTime.MinValue.Ticks || s.UtcTicks >= DateTime.MaxValue.Ticks) continue;
                    _channel.Writer.TryWrite(new SensorReading(sensor.Id, new DateTime(s.UtcTicks, DateTimeKind.Utc), s.Value));
                }
                break;
        }
    }

    /// <summary>Marks sensors offline when they go quiet - the UDP version of "connection closed".</summary>
    private async Task LivenessLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long now = Environment.TickCount64;

                foreach (var sensor in _byId.Values)
                {
                    if (now - sensor.LastSeen < SilenceTimeout.TotalMilliseconds) continue;
                    if (!_byId.TryRemove(sensor.Id, out _)) continue;
                    _byEndpoint.TryRemove(sensor.Endpoint, out _);
                    SensorDisconnected?.Invoke(this, sensor.Id);
                }

                // Forget shut-down endpoints after 30 s.
                foreach (var (endpoint, since) in _shutdownRequested)
                    if (now - since > 30_000) _shutdownRequested.TryRemove(endpoint, out _);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    // ───────────── Sending ─────────────

    private void SendState(UdpSensor sensor) =>
        Send(MessageType.State, Protocol.EncodeState(_isStreaming, sensor.DesiredRateHz), sensor.Endpoint);

    private void Send(MessageType type, byte[]? payload, IPEndPoint to)
    {
        var udp = _udp;
        if (udp is null) return;
        try
        {
            // Each SendAsync is one datagram; it either arrives whole or not at all.
            _ = udp.SendAsync(Protocol.BuildFrame(type, payload), to, CancellationToken.None)
                   .AsTask()
                   .ContinueWith(static t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // fire and forget
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        // UDP sensors don't notice a closed socket, so tell them explicitly (synchronously,
        // so the datagrams are out before the socket closes). If one is lost, the sensor
        // still exits after 5 s without hearing from us.
        var shutdown = Protocol.BuildFrame(MessageType.Shutdown, null);
        foreach (var sensor in _byId.Values)
        {
            try
            {
                _udp?.Send(shutdown, shutdown.Length, sensor.Endpoint);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // best effort
            }
        }
        _byId.Clear();
        _byEndpoint.Clear();

        _udp?.Dispose();
        _channel.Writer.TryComplete();
    }

    /// <summary>Per-sensor state. Counters are written by the receive loop and read by the UI.</summary>
    private sealed class UdpSensor(int id, IPEndPoint endpoint, double rateHz)
    {
        private long _lastSeen;
        private double _desiredRateHz = rateHz;
        private bool _hasSequence;
        private uint _lastSequence;

        public long Received;
        public long Lost;
        public long Late;

        public int Id { get; } = id;
        public IPEndPoint Endpoint { get; } = endpoint;

        public long LastSeen => Volatile.Read(ref _lastSeen);

        public double DesiredRateHz
        {
            get => Volatile.Read(ref _desiredRateHz);
            set => Volatile.Write(ref _desiredRateHz, value);
        }

        public void Touch() => Volatile.Write(ref _lastSeen, Environment.TickCount64);

        /// <summary>Returns false for late/duplicate datagrams; counts gaps as lost. Receive loop only.</summary>
        public bool AcceptSequence(uint sequence)
        {
            if (_hasSequence && sequence <= _lastSequence)
            {
                Interlocked.Increment(ref Late);
                return false;
            }

            if (_hasSequence && sequence > _lastSequence + 1)
                Interlocked.Add(ref Lost, sequence - _lastSequence - 1);

            _hasSequence = true;
            _lastSequence = sequence;
            Interlocked.Increment(ref Received);
            return true;
        }
    }
}
