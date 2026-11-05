using System.Net.Sockets;
using SensorMonitor.Shared;

namespace SensorSimulator;

/// <summary>
/// UDP transport. UDP has no connection, no delivery guarantee and no ordering,
/// so the things TCP gave us for free are rebuilt by hand:
///
///   TCP gives us                    UDP replacement here
///   ─────────────────────────────   ─────────────────────────────────────────────
///   connect / handshake             Hello repeated every second until the monitor answers
///   "is the other side alive?"      Heartbeat every second; monitor replies with State
///   reliable commands               State = full desired state, re-sent on every heartbeat
///   ordered, lossless data          sequence number per datagram -> monitor counts loss
///   message boundaries (framing)    one frame per datagram, max 64 samples (no IP fragmentation)
/// </summary>
public sealed class UdpSensorClient
{
    private const double HelloInterval = 1.0;      // s, until registered
    private const double HeartbeatInterval = 1.0;  // s
    private const double MonitorSilenceLimit = 5;  // s without a reply -> assume monitor is gone
    private const double RegisterTimeout = 30;     // s to find a monitor before giving up

    private readonly SimulatedSensor _sensor;
    private readonly string _host;
    private readonly int _port;

    private volatile bool _registered;   // monitor has answered our Hello
    private volatile bool _streaming;
    private volatile bool _everRegistered;
    private long _lastHeardTicks;        // Environment.TickCount64 when we last got anything
    private uint _sequence;
    private long _sent;

    public UdpSensorClient(SimulatedSensor sensor, string host, int port)
    {
        _sensor = sensor;
        _host = host;
        _port = port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        UdpSocketHelper.IgnoreConnectionResets(udp.Client);

        // "Connect" on UDP sends nothing - it just sets the default destination
        // and filters incoming datagrams to that address.
        udp.Connect(_host, _port);
        Log($"UDP sending to {_host}:{_port}. Announcing...");

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task receive = ReceiveAsync(udp, session.Token);
        Task send = SendLoopAsync(udp, session.Token);

        await Task.WhenAny(receive, send);
        session.Cancel();
        await SessionErrors.Ignore(receive);
        await SessionErrors.Ignore(send);

        Log($"Session ended. {Interlocked.Read(ref _sent):N0} samples in {_sequence:N0} datagrams.");
    }

    private async Task ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        while (true)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(ct);
            }
            catch (SocketException ex) when (UdpSocketHelper.IsConnectionReset(ex))
            {
                continue; // monitor not listening (yet) - see UdpSocketHelper
            }

            Frame frame;
            try
            {
                frame = Protocol.ParseDatagram(result.Buffer);
            }
            catch (InvalidDataException)
            {
                continue; // garbage - with UDP you just drop it
            }

            Volatile.Write(ref _lastHeardTicks, Environment.TickCount64);

            switch (frame.Type)
            {
                case MessageType.State:
                    var (streaming, rate) = Protocol.DecodeState(frame.Payload);
                    if (!_registered) Log("Registered with monitor.");
                    _registered = true;
                    _everRegistered = true;
                    if (streaming != _streaming) Log(streaming ? "Streaming." : "Paused.");
                    _streaming = streaming;
                    if (Math.Abs(rate - _sensor.RateHz) > 1e-9)
                    {
                        _sensor.RateHz = rate;
                        Log($"Rate set to {rate:0.##} Hz.");
                    }
                    break;

                case MessageType.Identify:
                    Log("Monitor doesn't know us (it restarted?) - announcing again.");
                    _registered = false;
                    _streaming = false;
                    break;

                case MessageType.Shutdown:
                    Log("Shutdown requested.");
                    return;
            }
        }
    }

    private async Task SendLoopAsync(UdpClient udp, CancellationToken ct)
    {
        var generator = new SampleGenerator(_sensor);
        var batch = new List<Sample>(256);
        var t = _sensor.Template;
        double nextHello = 0, nextHeartbeat = 0, nextStatus = 5;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            double now = generator.Now;

            // 1) Announce ourselves until the monitor answers.
            if (!_registered && now >= nextHello)
            {
                nextHello = now + HelloInterval;
                var info = new SensorInfo(_sensor.Name, t.Unit, t.NominalMin, t.NominalMax, _sensor.RateHz, Environment.ProcessId);
                await SendAsync(udp, MessageType.Hello, Protocol.EncodeHello(info), ct);
            }

            // 2) Heartbeat: "I'm alive, this is my state". The monitor's reply keeps us alive too.
            if (now >= nextHeartbeat)
            {
                nextHeartbeat = now + HeartbeatInterval;
                await SendAsync(udp, MessageType.Heartbeat, Protocol.EncodeState(_streaming, _sensor.RateHz), ct);
            }

            // 3) Give up if there's no monitor.
            if (!_everRegistered && now > RegisterTimeout)
            {
                Log($"No monitor answered on {_host}:{_port} within {RegisterTimeout:0} s. Exiting.");
                return;
            }
            double silence = (Environment.TickCount64 - Volatile.Read(ref _lastHeardTicks)) / 1000.0;
            if (_everRegistered && silence > MonitorSilenceLimit)
            {
                Log($"Monitor silent for {silence:0} s - assuming it is gone. Exiting.");
                return;
            }

            // 4) Data: split into datagrams of at most MaxSamplesPerDatagram samples.
            bool streaming = _streaming && _registered;
            generator.Generate(streaming, batch);
            for (int start = 0; start < batch.Count; start += Protocol.MaxSamplesPerDatagram)
            {
                int count = Math.Min(Protocol.MaxSamplesPerDatagram, batch.Count - start);
                uint sequence = ++_sequence;
                await SendAsync(udp, MessageType.SequencedReadings,
                    Protocol.EncodeSequencedReadings(sequence, batch, start, count), ct);
                Interlocked.Add(ref _sent, count);
            }

            if (now >= nextStatus)
            {
                nextStatus = now + 5;
                Log($"{(streaming ? "streaming" : "paused   ")} | {_sensor.RateHz,5:0.##} Hz | " +
                    $"{Interlocked.Read(ref _sent),9:N0} sent | seq {_sequence:N0}");
            }
        }
    }

    private static async Task SendAsync(UdpClient udp, MessageType type, byte[] payload, CancellationToken ct)
    {
        try
        {
            // Fire and forget: no acknowledgement, no retry. That is UDP.
            await udp.SendAsync(Protocol.BuildFrame(type, payload), ct);
        }
        catch (SocketException ex) when (UdpSocketHelper.IsConnectionReset(ex)
                                         || ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Nobody listening right now; the next heartbeat will try again.
        }
    }

    private void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_sensor.Name}: {message}");
}
