namespace SensorMonitor.Shared;

/// <summary>Defines what a simulated sensor measures and what its signal looks like.</summary>
public sealed record SensorTemplate(
    string Key,             // used on the command line: --template vibration
    string Name,
    string Unit,
    double BaseValue,
    double Amplitude,
    double PeriodSeconds,
    double Noise,
    double DefaultRateHz,
    double NominalMin,
    double NominalMax);

/// <summary>The sensor types the simulator can pretend to be.</summary>
public static class SensorTemplates
{
    /// <summary>Frame rates offered in the monitor UI.</summary>
    public static readonly double[] AvailableRates = { 1, 2, 5, 10, 20, 50, 100 };

    public static readonly IReadOnlyList<SensorTemplate> All = new[]
    {
        //                 Key            Name              Unit     Base   Amp  Period Noise  Rate  NomMin NomMax
        new SensorTemplate("temperature", "Temperature",    "°C",    24.0,  3.0,  30.0, 0.15,    2,    0,    50),
        new SensorTemplate("humidity",    "Humidity",       "%",     45.0,  6.0,  45.0, 0.40,    1,    0,   100),
        new SensorTemplate("vibration",   "Vibration",      "mm/s",  12.0,  6.0,   1.5, 1.20,  100,    0,    40),
        new SensorTemplate("voltage",     "Supply voltage", "V",     36.0,  1.5,  12.0, 0.08,   20,   30,    42),
        new SensorTemplate("flow",        "Flow rate",      "L/min", 18.0,  4.0,  20.0, 0.50,   10,    0,    40),
        new SensorTemplate("current",     "Motor current",  "A",      8.0,  2.0,   8.0, 0.30,   50,    0,    20),
    };

    public static SensorTemplate? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}
