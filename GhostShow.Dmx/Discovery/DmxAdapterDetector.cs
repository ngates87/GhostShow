using System.IO.Ports;
using GhostShow.Dmx.Protocol;

namespace GhostShow.Dmx.Discovery;

public enum DmxAdapterKind
{
    /// <summary>Enttec DMX USB Pro protocol: the adapter has its own processor and DMX timing.</summary>
    UsbPro,
    /// <summary>Bare FTDI chip (Enttec Open DMX USB, most cheap cables): the computer times every frame.</summary>
    OpenDmx,
}

/// <param name="SerialNumber">Pro: the widget serial number. Open DMX: the FTDI chip's USB serial number.</param>
public sealed record DmxAdapterInfo(string Port, DmxAdapterKind Kind, string? SerialNumber,
                                    string? Firmware = null, string? Description = null)
{
    public string KindLabel => Kind == DmxAdapterKind.UsbPro ? "USB Pro" : "Open DMX";

    public string Details => string.Join(" · ", new[]
    {
        $"S/N {SerialNumber ?? "unknown"}", Firmware is null ? null : $"firmware {Firmware}", Description,
    }.OfType<string>());

    public override string ToString() =>
        $"{Port}  ({KindLabel}, S/N {SerialNumber ?? "unknown"}{(Firmware is null ? "" : $", fw {Firmware}")})";
}

/// <summary>
/// Finds USB DMX adapters. Pro-protocol adapters are asked for their widget serial number, so they're
/// recognized whatever chip they use. Open DMX adapters can't answer anything, so an FTDI FT232R that
/// doesn't speak the Pro protocol is assumed to be one: that's how nearly all cheap USB-DMX cables are built,
/// but a plain FTDI USB-serial cable will show up too.
/// Probing writes a few bytes to each USB serial port; run it on request, not continuously, and not while a
/// show has an adapter open (an open port shows up as busy and is skipped).
/// </summary>
public static class DmxAdapterDetector
{
    public static async Task<IReadOnlyList<DmxAdapterInfo>> DetectAsync(int timeoutMs = 400, CancellationToken ct = default)
    {
        var probes = CandidatePorts().Select(p => Task.Run(() => Probe(p, timeoutMs), ct));
        var found = await Task.WhenAll(probes);
        return found.OfType<DmxAdapterInfo>()
            .OrderBy(a => a.Port, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The serial ports a DMX adapter could be on, without opening any of them: cheap enough to poll every couple of
    /// seconds to notice an adapter being plugged in.
    /// </summary>
    public static IReadOnlyList<string> CandidatePorts() => FilterCandidates(SerialPort.GetPortNames().Distinct()).ToArray();

    static IEnumerable<string> FilterCandidates(IEnumerable<string> names)
    {
        var ports = names.ToArray();
        // macOS lists every device twice; /dev/tty.* waits for carrier detect on open, /dev/cu.* doesn't.
        if (OperatingSystem.IsMacOS()) return ports.Where(p => !p.StartsWith("/dev/tty.", StringComparison.Ordinal));
        // Linux also lists 32 motherboard UARTs (ttyS*) that are never DMX adapters; only USB ports have a descriptor.
        if (OperatingSystem.IsLinux()) return ports.Where(p => UsbSerialPorts.Describe(p) is not null);
        return ports;
    }

    /// <summary>The adapter on this port, or null if it isn't a DMX adapter (or the port is busy).</summary>
    public static DmxAdapterInfo? Probe(string port, int timeoutMs = 400) => Probe(port, timeoutMs, out _);

    /// <param name="busy">
    /// True if the port couldn't be opened (in use, e.g. by a running output, or no permission): it may be an adapter,
    /// so callers shouldn't remember it as "not an adapter".
    /// </param>
    public static DmxAdapterInfo? Probe(string port, int timeoutMs, out bool busy)
    {
        var pro = ProbePro(port, timeoutMs, out busy);
        if (pro is not null || busy) return pro;
        var usb = UsbSerialPorts.Describe(port);
        return usb is { IsFt232R: true }
            ? new DmxAdapterInfo(port, DmxAdapterKind.OpenDmx, usb.Serial, Description: usb.Product)
            : null;
    }

    static DmxAdapterInfo? ProbePro(string port, int timeoutMs, out bool busy)
    {
        busy = false;
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(port, 57600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs,
            };
            try { sp.Open(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                busy = true;    // in use, or no permission (Linux: not in the dialout group)
                return null;
            }
            sp.DiscardInBuffer();

            var serial = Request(sp, EnttecProPacket.GetSerialNumber, ReadOnlySpan<byte>.Empty, timeoutMs);
            if (serial is not { Length: >= 4 }) return null;

            // The payload is the size of user config data to return with the parameters; we want none.
            var parms = Request(sp, EnttecProPacket.GetWidgetParameters, stackalloc byte[] { 0, 0 }, timeoutMs);
            string? fw = parms is { Length: >= 2 } ? $"{parms[1]}.{parms[0]}" : null;

            return new DmxAdapterInfo(port, DmxAdapterKind.UsbPro, FormatSerial(serial), fw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or TimeoutException or ArgumentException)
        {
            return null;
        }
        finally { sp?.Dispose(); }
    }

    /// <summary>
    /// Finds the configured adapter: its last known port if the serial number still matches there, otherwise a
    /// scan by serial number. Returns null if it isn't connected anywhere. Without a serial number, trusts the port.
    /// </summary>
    public static string? ResolvePort(DmxAdapterKind kind, string port, string? serialNumber, int timeoutMs = 400)
    {
        if (serialNumber is null) return port;
        bool Matches(DmxAdapterInfo? a) => a is not null && a.Kind == kind && a.SerialNumber == serialNumber;
        if (Matches(Probe(port, timeoutMs))) return port;
        return DetectAsync(timeoutMs).GetAwaiter().GetResult().FirstOrDefault(Matches)?.Port;
    }

    /// <summary>The serial number is 4 BCD bytes, least significant first; Enttec prints it as 8 digits.</summary>
    public static string FormatSerial(ReadOnlySpan<byte> bcd) => $"{bcd[3]:X2}{bcd[2]:X2}{bcd[1]:X2}{bcd[0]:X2}";

    static byte[]? Request(SerialPort sp, byte label, ReadOnlySpan<byte> payload, int timeoutMs)
    {
        var req = EnttecProPacket.Build(label, payload);
        sp.Write(req, 0, req.Length);

        var buf = new byte[1024];
        int n = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (n == buf.Length) n = 0;     // a port spewing data at us is not a Pro adapter; keep listening anyway
            try { n += sp.Read(buf, n, buf.Length - n); }
            catch (TimeoutException) { return null; }

            while (true)
            {
                bool ok = EnttecProPacket.TryParse(buf.AsSpan(0, n), out var gotLabel, out var data, out int used);
                byte[]? result = ok && gotLabel == label ? data.ToArray() : null;
                Buffer.BlockCopy(buf, used, buf, 0, n - used);
                n -= used;
                if (result is not null) return result;
                if (!ok && used == 0) break; // incomplete; read more
            }
        }
        return null;
    }
}
