using System.Net.Sockets;
using SensorMonitor.Shared;

namespace SensorSimulator;

/// <summary>
/// TCP transport. Connects to the monitor, sends Hello, then runs two loops at once:
/// <list type="bullet">
/// <item>sampling loop - sends due frames as Readings messages</item>
/// <item>command loop  - reacts to Start / Stop / SetRate / Shutdown</item>
/// </list>
/// TCP gives us reliable, ordered delivery and tells us when the other side goes
/// away (read returns 0 bytes), so no heartbeats or sequence numbers are needed.
/// </summary>
public sealed class TcpSensorClient
{
    private readonly SimulatedSensor _sensor;
    private readonly string _host;
    private readonly int _port;
    private volatile bool _streaming;
    private long _sent;

    public TcpSensorClient(SimulatedSensor sensor, string host, int port)
    {
        _sensor = sensor;
        _host = host;
        _port = port;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        var stream = client.GetStream();

        var t = _sensor.Template;
        var info = new SensorInfo(_sensor.Name, t.Unit, t.NominalMin, t.NominalMax, _sensor.RateHz, Environment.ProcessId);
        await Protocol.WriteFrameAsync(stream, MessageType.Hello, Protocol.EncodeHello(info), ct);
        Log($"TCP connected to {_host}:{_port}. Waiting for Start...");

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task commands = ReceiveCommandsAsync(stream, session.Token);
        Task sampling = SampleAndSendAsync(stream, session.Token);

        // Whichever loop ends first (Shutdown, disconnect, error) ends the session.
        await Task.WhenAny(commands, sampling);
        session.Cancel();
        await SessionErrors.Ignore(commands);
        await SessionErrors.Ignore(sampling);

        Log($"Session ended. {Interlocked.Read(ref _sent):N0} samples sent.");
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        // The monitor may not be listening yet (e.g. sensor started first) - retry for a while.
        for (int attempt = 1; ; attempt++)
        {
            var client = new TcpClient { NoDelay = true }; // send small frames immediately
            try
            {
                await client.ConnectAsync(_host, _port, ct);
                return client;
            }
            catch (SocketException) when (attempt < 30)
            {
                client.Dispose();
                Log($"Monitor not reachable on {_host}:{_port}, retrying ({attempt})...");
                await Task.Delay(1000, ct);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private async Task ReceiveCommandsAsync(NetworkStream stream, CancellationToken ct)
    {
        while (true)
        {
            var frame = await Protocol.ReadFrameAsync(stream, ct);
            if (frame is null)
            {
                Log("Monitor closed the connection.");
                return;
            }

            switch (frame.Type)
            {
                case MessageType.Start:
                    _streaming = true;
                    Log("Start - streaming.");
                    break;
                case MessageType.Stop:
                    _streaming = false;
                    Log("Stop - paused.");
                    break;
                case MessageType.SetRate:
                    _sensor.RateHz = Protocol.DecodeRate(frame.Payload);
                    Log($"Rate set to {_sensor.RateHz:0.##} Hz.");
                    break;
                case MessageType.Shutdown:
                    Log("Shutdown requested.");
                    return;
                default:
                    Log($"Ignoring unexpected message {frame.Type}.");
                    break;
            }
        }
    }

    private async Task SampleAndSendAsync(NetworkStream stream, CancellationToken ct)
    {
        var generator = new SampleGenerator(_sensor);
        var batch = new List<Sample>(256);
        double nextStatus = 5;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            bool streaming = _streaming;
            generator.Generate(streaming, batch);

            if (batch.Count > 0)
            {
                await Protocol.WriteFrameAsync(stream, MessageType.Readings, Protocol.EncodeReadings(batch), ct);
                Interlocked.Add(ref _sent, batch.Count);
            }

            if (generator.Now >= nextStatus)
            {
                nextStatus = generator.Now + 5;
                Log($"{(streaming ? "streaming" : "paused   ")} | {_sensor.RateHz,5:0.##} Hz | {Interlocked.Read(ref _sent),9:N0} sent");
            }
        }
    }

    private void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_sensor.Name}: {message}");
}

/// <summary>Errors that just mean "the session is over".</summary>
internal static class SessionErrors
{
    public static async Task Ignore(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                       or ObjectDisposedException or InvalidDataException)
        {
            // Expected when the monitor goes away or the session is cancelled.
        }
    }
}
