using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SensorMonitor.Services;

/// <summary>
/// Starts SensorSimulator processes (one per sensor) and cleans them up on exit.
/// The simulator's build output is copied next to SensorMonitor.exe by the
/// CopySensorSimulator target in SensorMonitor.csproj.
/// </summary>
public sealed class SensorProcessManager : IDisposable
{
    private readonly List<Process> _processes = new();
    private readonly object _gate = new();

    /// <summary>Show a console window per sensor (handy for watching their logs).</summary>
    public bool ShowConsoleWindows { get; set; }

    /// <summary>Launches one sensor process. Returns null on success, or an error message.</summary>
    public string? Launch(string templateKey, string name, double rateHz, string transport, int port)
    {
        var startInfo = CreateStartInfo();
        if (startInfo is null)
            return "SensorSimulator not found next to SensorMonitor.exe. Rebuild the solution.";

        startInfo.ArgumentList.Add("--template");
        startInfo.ArgumentList.Add(templateKey);
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add("--rate");
        startInfo.ArgumentList.Add(rateHz.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add(transport);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = !ShowConsoleWindows;

        try
        {
            var process = Process.Start(startInfo);
            if (process is null) return "Process.Start returned null.";
            lock (_gate)
            {
                _processes.RemoveAll(p => HasExited(p));
                _processes.Add(process);
            }
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Could not start SensorSimulator: {ex.Message}";
        }
    }

    /// <summary>Kills a sensor process by id (fallback if it ignores Shutdown).</summary>
    public void Kill(int processId)
    {
        lock (_gate)
        {
            var process = _processes.FirstOrDefault(p => !HasExited(p) && p.Id == processId);
            if (process is null) return;
            TryKill(process);
        }
    }

    /// <summary>
    /// Gives processes we started a moment to exit on their own (they exit when
    /// their socket closes), then kills any that are still running.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(1.5);
            foreach (var process in _processes)
            {
                int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                try
                {
                    if (!process.WaitForExit(remaining)) TryKill(process);
                }
                catch (InvalidOperationException)
                {
                    // not started / already gone
                }
                process.Dispose();
            }
            _processes.Clear();
        }
    }

    private static ProcessStartInfo? CreateStartInfo()
    {
        string dir = AppContext.BaseDirectory;

        // Normal case: the apphost exe built for Windows.
        string exe = Path.Combine(dir, "SensorSimulator.exe");
        if (File.Exists(exe))
            return new ProcessStartInfo(exe);

        // Fallback: run the dll through the dotnet host.
        string dll = Path.Combine(dir, "SensorSimulator.dll");
        if (File.Exists(dll))
        {
            var info = new ProcessStartInfo("dotnet");
            info.ArgumentList.Add(dll);
            return info;
        }

        return null;
    }

    private static bool HasExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // already exited or access denied
        }
    }
}
