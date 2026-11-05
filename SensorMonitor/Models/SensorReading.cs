namespace SensorMonitor.Models;

/// <summary>A single sample received from a sensor, tagged with the monitor's id for that sensor.</summary>
public readonly record struct SensorReading(int SensorId, DateTime Timestamp, double Value);

/// <summary>A stored point: UTC ticks + value (compact, no DateTime overhead).</summary>
public readonly record struct DataPoint(long Ticks, double Value);
