using System.Buffers.Binary;
using System.Text.Json;

namespace SensorMonitor.Shared;

/// <summary>Message types on the wire. Values are part of the protocol - don't renumber.</summary>
public enum MessageType : byte
{
    // ── sensor -> monitor ──
    Hello = 1,              // JSON SensorInfo. TCP: first frame. UDP: repeated until the monitor answers.
    Readings = 2,           // TCP: int32 count, then count x (int64 utcTicks, float64 value)
    Heartbeat = 3,          // UDP: byte streaming (0/1), float64 current rate. Sent every second.
    SequencedReadings = 4,  // UDP: uint32 sequence number, then the same body as Readings

    // ── monitor -> sensor ──
    Start = 10,             // TCP: begin sending readings
    Stop = 11,              // TCP: pause sending readings
    SetRate = 12,           // TCP: float64 new rate in Hz
    Shutdown = 13,          // TCP + UDP: stop and exit the process
    State = 14,             // UDP: byte streaming (0/1), float64 rate - the full desired state
    Identify = 15,          // UDP: "I don't know you" - the sensor should send Hello again
}

/// <summary>What a sensor announces about itself.</summary>
public sealed record SensorInfo(
    string Name,
    string Unit,
    double NominalMin,
    double NominalMax,
    double SampleRateHz,
    int ProcessId);

/// <summary>One measurement: UTC timestamp in ticks + value.</summary>
public readonly record struct Sample(long UtcTicks, double Value);

/// <summary>One decoded message.</summary>
public sealed record Frame(MessageType Type, byte[] Payload);

/// <summary>
/// Wire format shared by TCP and UDP. Every message is a frame:
///
///   [ int32 payload length (little-endian) ][ byte message type ][ payload ... ]
///
/// TCP is a byte stream with no boundaries, so the reader uses the length to
/// find where each frame ends. UDP keeps datagram boundaries, so each datagram
/// carries exactly one frame and the length is just a consistency check.
/// All numbers are little-endian.
/// </summary>
public static class Protocol
{
    public const int DefaultPort = 5055;     // TCP
    public const int DefaultUdpPort = 5056;  // UDP (separate from TCP to keep things obvious)
    public const int HeaderSize = 5;
    public const int MaxPayloadSize = 1 << 20;

    /// <summary>
    /// Keeps each UDP datagram at about 1 KB, safely below the ~1,500-byte network MTU,
    /// so it is never fragmented into IP pieces (one lost piece = whole datagram lost).
    /// </summary>
    public const int MaxSamplesPerDatagram = 64;

    private const int SampleSize = 16; // int64 + float64

    // ───────────── Framing ─────────────

    /// <summary>Builds header + payload as one buffer (one TCP write, or one UDP datagram).</summary>
    public static byte[] BuildFrame(MessageType type, byte[]? payload)
    {
        int length = payload?.Length ?? 0;
        var buffer = new byte[HeaderSize + length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, length);
        buffer[4] = (byte)type;
        payload?.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    /// <summary>TCP: writes one frame.</summary>
    public static async Task WriteFrameAsync(Stream stream, MessageType type, byte[]? payload, CancellationToken ct)
    {
        await stream.WriteAsync(BuildFrame(type, payload), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TCP: reads one frame. Returns <c>null</c> if the other side closed the
    /// connection cleanly between frames.
    /// </summary>
    public static async Task<Frame?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxPayloadSize)
            throw new InvalidDataException($"Invalid frame length {length}.");

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyOrEndAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Connection closed in the middle of a frame.");

        return new Frame((MessageType)header[4], payload);
    }

    /// <summary>UDP: one datagram = one frame. Throws <see cref="InvalidDataException"/> if malformed.</summary>
    public static Frame ParseDatagram(byte[] datagram)
    {
        if (datagram.Length < HeaderSize)
            throw new InvalidDataException("Datagram shorter than a frame header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(datagram);
        if (length != datagram.Length - HeaderSize)
            throw new InvalidDataException($"Datagram length {datagram.Length} doesn't match header length {length}.");

        return new Frame((MessageType)datagram[4], datagram[HeaderSize..]);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        // A single ReadAsync may return fewer bytes than requested - keep reading.
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("Connection closed in the middle of a frame.");
            }
            read += n;
        }
        return true;
    }

    // ───────────── Hello ─────────────

    public static byte[] EncodeHello(SensorInfo info) => JsonSerializer.SerializeToUtf8Bytes(info);

    public static SensorInfo DecodeHello(byte[] payload) =>
        JsonSerializer.Deserialize<SensorInfo>(payload)
        ?? throw new InvalidDataException("Empty Hello message.");

    // ───────────── Readings ─────────────

    public static byte[] EncodeReadings(IReadOnlyList<Sample> samples)
    {
        var buffer = new byte[4 + samples.Count * SampleSize];
        WriteSamples(buffer, samples, 0, samples.Count);
        return buffer;
    }

    public static void DecodeReadings(byte[] payload, List<Sample> destination) =>
        ReadSamples(payload, destination);

    /// <summary>UDP: <paramref name="count"/> samples starting at <paramref name="start"/>, tagged with a sequence number.</summary>
    public static byte[] EncodeSequencedReadings(uint sequence, IReadOnlyList<Sample> samples, int start, int count)
    {
        var buffer = new byte[4 + 4 + count * SampleSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, sequence);
        WriteSamples(buffer.AsSpan(4), samples, start, count);
        return buffer;
    }

    /// <summary>Returns the sequence number and fills <paramref name="destination"/>.</summary>
    public static uint DecodeSequencedReadings(byte[] payload, List<Sample> destination)
    {
        if (payload.Length < 4) throw new InvalidDataException("SequencedReadings payload too short.");
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        ReadSamples(payload.AsSpan(4), destination);
        return sequence;
    }

    private static void WriteSamples(Span<byte> destination, IReadOnlyList<Sample> samples, int start, int count)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, count);
        int offset = 4;
        for (int i = start; i < start + count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], samples[i].UtcTicks);
            BinaryPrimitives.WriteDoubleLittleEndian(destination[(offset + 8)..], samples[i].Value);
            offset += SampleSize;
        }
    }

    private static void ReadSamples(ReadOnlySpan<byte> source, List<Sample> destination)
    {
        destination.Clear();
        if (source.Length < 4) throw new InvalidDataException("Readings payload too short.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(source);
        if (count < 0 || source.Length != 4 + (long)count * SampleSize)
            throw new InvalidDataException($"Readings payload size {source.Length} doesn't match count {count}.");

        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            long ticks = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
            double value = BinaryPrimitives.ReadDoubleLittleEndian(source[(offset + 8)..]);
            destination.Add(new Sample(ticks, value));
            offset += SampleSize;
        }
    }

    // ───────────── Rate / state ─────────────

    public static byte[] EncodeRate(double rateHz)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, rateHz);
        return buffer;
    }

    public static double DecodeRate(byte[] payload)
    {
        if (payload.Length != 8) throw new InvalidDataException("Rate payload must be 8 bytes.");
        return ValidateRate(BinaryPrimitives.ReadDoubleLittleEndian(payload));
    }

    /// <summary>Heartbeat (sensor's actual state) and State (monitor's desired state) share this layout.</summary>
    public static byte[] EncodeState(bool streaming, double rateHz)
    {
        var buffer = new byte[9];
        buffer[0] = streaming ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(1), rateHz);
        return buffer;
    }

    public static (bool Streaming, double RateHz) DecodeState(byte[] payload)
    {
        if (payload.Length != 9) throw new InvalidDataException("State payload must be 9 bytes.");
        return (payload[0] != 0, ValidateRate(BinaryPrimitives.ReadDoubleLittleEndian(payload.AsSpan(1))));
    }

    private static double ValidateRate(double rate) =>
        rate > 0 && rate <= 10_000 ? rate : throw new InvalidDataException($"Invalid rate {rate}.");
}
