using SensorMonitor.Shared;
using SensorSimulator;

// One process = one sensor. Examples:
//   SensorSimulator --template vibration
//   SensorSimulator --template humidity --rate 5 --name "Greenhouse humidity"
//   SensorSimulator --template vibration --transport udp

SimulatorOptions options;
try
{
    options = SimulatorOptions.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

if (options.ShowHelp)
{
    SimulatorOptions.PrintHelp();
    return 0;
}

var template = SensorTemplates.Find(options.Template);
if (template is null)
{
    Console.Error.WriteLine($"Unknown template '{options.Template}'. " +
                            $"Available: {string.Join(", ", SensorTemplates.All.Select(t => t.Key))}");
    return 1;
}

string name = options.Name ?? template.Name;
var sensor = new SimulatedSensor(
    template,
    name,
    options.RateHz ?? template.DefaultRateHz,
    options.Seed ?? Environment.ProcessId);

try
{
    Console.Title = $"Sensor: {name}";
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
{
    // No console window (e.g. started hidden by the monitor) - fine.
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let us shut down cleanly instead of being killed
    cts.Cancel();
};

try
{
    if (options.Transport == "udp")
        await new UdpSensorClient(sensor, options.Host, options.Port).RunAsync(cts.Token);
    else
        await new TcpSensorClient(sensor, options.Host, options.Port).RunAsync(cts.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (System.Net.Sockets.SocketException ex)
{
    Console.Error.WriteLine($"Could not connect to the monitor at {options.Host}:{options.Port}: {ex.Message}");
    return 2;
}
