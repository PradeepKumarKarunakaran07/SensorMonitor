using System.Net.Sockets;

namespace SensorMonitor.Shared;

public static class UdpSocketHelper
{
    // SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>
    /// Windows quirk: if you send a UDP datagram to a port nobody is listening on,
    /// Windows reports the ICMP "port unreachable" reply as a ConnectionReset error
    /// on your NEXT receive - which would kill a naive receive loop. This turns that
    /// behaviour off. No-op on other operating systems.
    /// </summary>
    public static void IgnoreConnectionResets(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            socket.IOControl(SioUdpConnReset, new byte[] { 0 }, null);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException)
        {
            // Not critical: receive loops also catch ConnectionReset and carry on.
        }
    }

    /// <summary>True for the error described in <see cref="IgnoreConnectionResets"/>.</summary>
    public static bool IsConnectionReset(SocketException ex) =>
        ex.SocketErrorCode == SocketError.ConnectionReset;
}
