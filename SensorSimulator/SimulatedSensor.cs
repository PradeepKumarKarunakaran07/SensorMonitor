using SensorMonitor.Shared;

namespace SensorSimulator;

/// <summary>
/// Generates a plausible-looking signal: slow oscillation + harmonic + mean-reverting
/// drift + Gaussian noise + rare spikes. Not thread-safe except <see cref="RateHz"/>.
/// </summary>
public sealed class SimulatedSensor
{
    private readonly Random _rng;
    private readonly double _phase;
    private double _drift;
    private double _rateHz;

    public SimulatedSensor(SensorTemplate template, string name, double rateHz, int seed)
    {
        Template = template;
        Name = name;
        RateHz = rateHz;
        _rng = new Random(seed);
        _phase = _rng.NextDouble() * 2 * Math.PI;
    }

    public SensorTemplate Template { get; }
    public string Name { get; }

    public double TimingJitter { get; init; } = 0.15;   // fraction of the frame interval
    public double DropProbability { get; init; } = 0.01; // chance a frame is lost
    public double SpikeProbability { get; init; } = 0.002;
    public double DriftRate { get; init; } = 0.02;

    /// <summary>Changed by the network thread (SetRate), read by the sampling loop.</summary>
    public double RateHz
    {
        get => Volatile.Read(ref _rateHz);
        set => Volatile.Write(ref _rateHz, Math.Clamp(value, 0.1, 1000));
    }

    public Random Random => _rng;

    /// <summary>Value at <paramref name="t"/> seconds since the sensor started.</summary>
    public double ValueAt(double t)
    {
        var c = Template;
        _drift = _drift * 0.995 + Gaussian() * DriftRate * c.Amplitude;

        double w = 2 * Math.PI / c.PeriodSeconds;
        double value = c.BaseValue
                       + c.Amplitude * Math.Sin(w * t + _phase)
                       + 0.25 * c.Amplitude * Math.Sin(w * 2.7 * t + _phase * 1.3)
                       + _drift
                       + Gaussian() * c.Noise;

        if (_rng.NextDouble() < SpikeProbability)
        {
            double sign = _rng.Next(2) == 0 ? -1 : 1;
            value += sign * c.Amplitude * (1.5 + _rng.NextDouble());
        }
        return value;
    }

    // Box-Muller transform.
    private double Gaussian()
    {
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
