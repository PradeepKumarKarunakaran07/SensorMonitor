using System.Diagnostics;
using SensorMonitor.Shared;

namespace SensorSimulator;

/// <summary>
/// Decides which frames are due and timestamps them. Shared by the TCP and UDP
/// clients so both transports produce exactly the same data.
///
/// Timers tick every ~15 ms on Windows, so on each call we emit every frame that
/// came due since the last call (with a little timing jitter and the occasional
/// dropped frame). Fast sensors produce small batches, slow ones a frame now and then.
/// </summary>
public sealed class SampleGenerator
{
    private readonly SimulatedSensor _sensor;
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _next;      // seconds since start when the next frame is due
    private long _lastTicks;

    public SampleGenerator(SimulatedSensor sensor)
    {
        _sensor = sensor;
    }

    /// <summary>Seconds since this generator was created.</summary>
    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Clears <paramref name="batch"/> and fills it with the frames due now.</summary>
    public void Generate(bool streaming, List<Sample> batch)
    {
        batch.Clear();
        var rng = _sensor.Random;
        double now = Now;

        // While paused - or if we fell far behind (e.g. debugger break) - don't
        // build up a backlog that would be sent as one huge burst later.
        if (!streaming || now - _next > 1.0) _next = now;

        double interval = 1.0 / _sensor.RateHz;
        while (_next <= now)
        {
            double jitter = (rng.NextDouble() * 2 - 1) * _sensor.TimingJitter * interval;
            double t = Math.Max(0, _next + jitter);
            double value = _sensor.ValueAt(t);
            _next += interval;

            if (rng.NextDouble() < _sensor.DropProbability) continue; // frame lost at the source

            long ticks = _startUtc.Ticks + (long)(t * TimeSpan.TicksPerSecond);
            if (ticks <= _lastTicks) ticks = _lastTicks + 1; // keep timestamps strictly increasing
            _lastTicks = ticks;
            batch.Add(new Sample(ticks, value));
        }

        if (!streaming) batch.Clear();
    }
}
