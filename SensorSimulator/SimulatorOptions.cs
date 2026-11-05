using System.Globalization;
using SensorMonitor.Shared;

namespace SensorSimulator;

/// <summary>Command-line options, e.g. <c>--template vibration --rate 50 --port 5055</c>.</summary>
public sealed class SimulatorOptions
{
    public string Host { get; private set; } = "127.0.0.1";
    /// <summary>"tcp" or "udp".</summary>
    public string Transport { get; private set; } = "tcp";

    /// <summary>Explicit --port, or the default port for the chosen transport.</summary>
    public int Port => _port ?? (Transport == "udp" ? Protocol.DefaultUdpPort : Protocol.DefaultPort);
    private int? _port;
    public string Template { get; private set; } = "temperature";
    public string? Name { get; private set; }
    public double? RateHz { get; private set; }
    public int? Seed { get; private set; }
    public bool ShowHelp { get; private set; }

    public static SimulatorOptions Parse(string[] args)
    {
        var o = new SimulatorOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i].ToLowerInvariant();
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value after {args[i]}");

            switch (key)
            {
                case "--host": o.Host = Next(); break;
                case "--port": o._port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--transport":
                    o.Transport = Next().ToLowerInvariant();
                    if (o.Transport is not ("tcp" or "udp"))
                        throw new ArgumentException("--transport must be tcp or udp");
                    break;
                case "--template": o.Template = Next(); break;
                case "--name": o.Name = Next(); break;
                case "--rate": o.RateHz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "-h":
                case "--help": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'. Use --help.");
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            SensorSimulator - pretends to be one sensor and streams to SensorMonitor over TCP or UDP.

            Options:
              --template <key>   Sensor type (default: temperature)
              --name <text>      Display name (default: the template's name)
              --rate <hz>        Sample rate in Hz (default: the template's rate)
              --transport <t>    tcp (default) or udp
              --host <address>   Monitor address (default: 127.0.0.1)
              --port <number>    Monitor port (default: 5055 for tcp, 5056 for udp)
              --seed <number>    Random seed, for repeatable signals
            """);
        Console.WriteLine("Templates: " + string.Join(", ", SensorTemplates.All.Select(t => t.Key)));
    }
}
