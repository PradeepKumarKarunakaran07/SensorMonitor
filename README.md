# Sensor Monitor (WPF, .NET 10)

Real-time chart of data from multiple sensors. **Each sensor is a separate program** that
talks to the monitor over a socket: most use **TCP**, the Vibration sensor uses **UDP**.
No third-party packages.

> 📐 Architecture, threading model, protocol sequences and design decisions (with diagrams):
> see **[docs/DESIGN.md](docs/DESIGN.md)**.

## Run

Requirements: Windows, Visual Studio with the ".NET desktop development" workload, .NET 10 SDK.

1. Open `SensorMonitor.sln`.
2. Make sure **SensorMonitor** is the startup project (bold in Solution Explorer).
3. Press **F5**.

The monitor listens on `127.0.0.1` (TCP port 5055, UDP port 5056) and launches four sensor
processes. Vibration connects over UDP; the others over TCP. Each card shows its link, e.g.
`UDP · 3 lost (0.2%) · 0 late`.
Each one connects, introduces itself, and starts streaming. Open Task Manager to see the
`SensorSimulator.exe` processes, or tick **Show console window for new sensors** and click
**+ Add sensor** to watch one sensor's log.

> To use .NET 8 instead, change `<SensorTfm>` in `Directory.Build.props` to `net8.0`.

### Running a sensor by hand

With the monitor running, open a terminal in `SensorMonitor\bin\Debug\net10.0-windows\` and run:

```
SensorSimulator --template vibration --rate 50 --name "Pump vibration"
SensorSimulator --template humidity --transport udp
SensorSimulator --help
```

It appears in the monitor within a second. Press Ctrl+C to stop it; its card turns "offline".
Templates: `temperature`, `humidity`, `vibration`, `voltage`, `flow`, `current`.

## Solution layout

```
SensorMonitor.sln
Directory.Build.props        .NET version + shared settings for all projects
SensorMonitor.Shared/        class library used by both programs
  Protocol.cs                message types, framing, encode/decode
  SensorTemplates.cs         sensor types (units, ranges, default rates)
  UdpSocketHelper.cs         Windows UDP "connection reset" quirk
SensorSimulator/             console app: ONE running instance = ONE sensor
  Program.cs                 command-line entry point
  TcpSensorClient.cs         TCP: connect, Hello, sampling loop + command loop
  UdpSensorClient.cs         UDP: Hello until answered, heartbeats, sequenced datagrams
  SampleGenerator.cs         which frames are due + timestamps (shared by both)
  SimulatedSensor.cs         signal model (wave + drift + noise + spikes)
  SimulatorOptions.cs        --template, --rate, --port ...
SensorMonitor/               WPF app
  Services/
    ISensorDataSource.cs     abstraction the UI depends on
    TcpSensorDataSource.cs   TCP server: one task per connection
    UdpSensorDataSource.cs   UDP receiver: one socket for all UDP sensors, liveness, loss stats
    CompositeSensorDataSource.cs  merges TCP + UDP into one source for the UI
    SensorProcessManager.cs  launches / cleans up sensor processes
  Models/, Processing/, ViewModels/, Controls/   (chart, normalization, MVVM)
```

The WPF project builds the simulator first and copies it next to `SensorMonitor.exe`
(see the `CopySensorSimulator` target in `SensorMonitor.csproj`).

## Architecture

```
SensorSimulator.exe (Temperature) ──TCP──┐
SensorSimulator.exe (Humidity)    ──TCP──┼─► TcpSensorDataSource ──┐
SensorSimulator.exe (Voltage)     ──TCP──┘                         ├─► Composite ──► Channel ──► consumer ──► buffers ──► chart
SensorSimulator.exe (Vibration)   ──UDP────► UdpSensorDataSource ──┘

```

Everything after the channel is unchanged from the in-process version: the UI doesn't know
or care that the data now comes over sockets.

- **Monitor = server** for both transports, sensors = clients. Sensors can come and go.
- **Which sensors use UDP**: `UdpTemplates` in `MainViewModel` (default: vibration).
- **Loopback only** (`127.0.0.1`): no firewall prompt, nothing exposed to the network.
- **Monitor assigns the sensor id** when a connection says Hello.
- **Disconnects**: TCP sees the socket close; UDP sees 3 s of silence. Either way the card is
  marked *offline* and its data stays on screen. When the monitor closes, TCP sensors see
  end-of-stream, UDP sensors get a Shutdown (or exit after 5 s without hearing from it).

## Protocol

TCP is a byte stream with no message boundaries, so every message is a **frame**:

```
┌──────────────────────────┬──────────────┬──────────────────────┐
│ payload length (int32 LE)│ type (byte)  │ payload (length bytes)│
└──────────────────────────┴──────────────┴──────────────────────┘
```

The reader reads exactly 5 header bytes, then exactly `length` payload bytes, looping
because a single socket read can return fewer bytes than asked for.

| Type | Value | Direction | Payload |
|---|---|---|---|
| Hello | 1 | sensor → monitor | UTF-8 JSON `{ Name, Unit, NominalMin, NominalMax, SampleRateHz, ProcessId }`. Must be first. |
| Readings | 2 | sensor → monitor | `int32 count`, then `count ×` (`int64` UTC ticks, `float64` value) |
| Start | 10 | monitor → sensor | — begin sending readings |
| Stop | 11 | monitor → sensor | — pause (connection stays open) |
| SetRate | 12 | monitor → sensor | `float64` Hz |
| Shutdown | 13 | monitor → sensor | — close and exit (TCP + UDP) |
| Heartbeat | 3 | sensor → monitor (UDP) | `byte` streaming, `float64` current rate. Every second. |
| SequencedReadings | 4 | sensor → monitor (UDP) | `uint32` sequence, then the same body as Readings |
| State | 14 | monitor → sensor (UDP) | `byte` streaming, `float64` desired rate |
| Identify | 15 | monitor → sensor (UDP) | — "unknown sensor, send Hello again" |

All numbers are little-endian. Each sensor sends all the frames that came due since its
last timer tick as one Readings message, so fast sensors send small batches and slow
sensors send single frames.

## TCP vs UDP in this app

| Concern | TCP sensors | UDP sensor |
|---|---|---|
| Message boundaries | Length header, read until full | One frame per datagram (≤ 64 samples, ~1 KB, below MTU) |
| Joining | Connect, then Hello | Hello repeated every second until the monitor replies |
| Alive? | Read returns 0 when the socket closes | Heartbeat each second; offline after 3 s of silence |
| Commands | Start / Stop / SetRate, delivered reliably | **State** (streaming + rate) sent in reply to every heartbeat, so a lost command is fixed within ~1 s |
| Lost / out-of-order data | Can't happen | **Sequence number** per datagram. Gaps count as *lost*; anything older than the newest is *late* (includes duplicates) and dropped, so buffers stay in time order |
| Monitor forgot the sensor | — | Monitor replies **Identify**; the sensor sends Hello again |
| Windows quirk | — | ICMP "port unreachable" surfaces as ConnectionReset on receive: disabled with `SIO_UDP_CONNRESET` and also caught |

"Lost" means "not shown": datagrams that never arrived **and** ones that arrived too late
to use. On loopback you'll normally see 0; on a real network you'd see some.

## Features

- Different frame rate per sensor (1–100 Hz), changeable live (sent as **SetRate**);
  the card shows the measured fps. Frames have timing jitter and ~1 % are dropped.
- **Start/Stop stream** sends Start / Stop to every sensor process.
- **Normalization** so sensors with different units and rates share one chart (below).
- Scrolling chart, wall-clock time axis, auto-scaling, split view, sample markers.
- Add up to 8 sensors (each **+ Add sensor** launches a new process); remove any sensor
  (sends **Shutdown**; the process is killed after 2 s if it doesn't exit).

## Normalization

Each frame the chart runs every sensor through: raw buffer → time alignment → value scaling → draw.
The logic is in `SensorMonitor/Processing/SignalProcessor.cs` (pure functions, no UI types).

**Value scaling** (different units on one axis)

| Mode | Formula | Axis |
|---|---|---|
| Raw | value as measured | engineering units |
| Min–max *(default)* | (v − min) / (max − min) over the visible window | 0 – 1 |
| Z-score | (v − mean) / std-dev over the visible window | σ |
| % of nominal range | (v − nominalMin) / (nominalMax − nominalMin) × 100 | % |

Nominal ranges come from each sensor's Hello message (defined in `SensorTemplates.cs`).

**Time alignment** (different frame rates on one time base)

| Mode | Behaviour |
|---|---|
| Off *(default)* | every sample at its native rate and timestamp |
| Linear interpolation | all sensors resampled to a common grid (1–50 Hz) aligned to the clock |
| Sample & hold | same grid; between frames a sensor holds its last value (extends to "now") |

When a sensor is faster than the grid, the samples in each grid cell are averaged
(anti-aliasing) instead of picking one.

## Troubleshooting

- **"Can't listen on TCP 5055 / UDP 5056"** in the status line: another monitor instance is
  running, or something else uses a port. Close it, or change the ports in `Protocol.cs`.
- **"SensorSimulator not found"**: rebuild the whole solution (Build → Rebuild Solution).
- **Sensors stay "Starting…"** and never appear: run `SensorSimulator` by hand from the
  output folder (see above) to see its error message.

## Using real sensors

Two options:

1. Make the device (or a small bridge program) speak the protocol above over TCP.
   Nothing in the monitor changes.
2. Implement `ISensorDataSource` for your transport (serial port, MQTT, OPC UA...) and
   pass it to `MainViewModel`.

## Tuning

- Sensor types, default rates, nominal ranges: `SensorMonitor.Shared/SensorTemplates.cs`
- Jitter / dropped frames / spikes: properties on `SensorSimulator/SimulatedSensor.cs`
- Ports: `Protocol.DefaultPort` (TCP), `Protocol.DefaultUdpPort` (UDP)
- UDP timeouts: `SilenceTimeout` in `UdpSensorDataSource`, constants at the top of `UdpSensorClient`
- History kept per sensor: `SensorViewModel.Buffer` capacity (30,000 samples = 5 min at 100 Hz)
- Max sensors: `MainViewModel.MaxSensors`
