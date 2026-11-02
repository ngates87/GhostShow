using Microsoft.Win32;

namespace GhostShow.Dmx.Discovery;

/// <summary>USB descriptor of the device behind a serial port. Serial is the USB serial number, not a DMX one.</summary>
public sealed record UsbPortInfo(int VendorId, int ProductId, string? Manufacturer, string? Product, string? Serial)
{
    /// <summary>FTDI FT232R, the chip in Enttec Open DMX USB and almost every cheap USB-DMX cable.</summary>
    public bool IsFt232R => VendorId == 0x0403 && ProductId == 0x6001;
}

/// <summary>Looks up which USB device a serial port belongs to. Best effort: null when the OS won't say.</summary>
public static class UsbSerialPorts
{
    /// <summary>The serial port whose USB device has this serial number, or null if none is plugged in.</summary>
    public static string? FindPortByUsbSerial(string usbSerial) =>
        System.IO.Ports.SerialPort.GetPortNames().FirstOrDefault(p => Describe(p)?.Serial == usbSerial);

    public static UsbPortInfo? Describe(string port)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return DescribeLinux(port);
            if (OperatingSystem.IsMacOS()) return DescribeMac(port);
            if (OperatingSystem.IsWindows()) return DescribeWindows(port);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { }
        return null;
    }

    /// <summary>/sys/class/tty/ttyUSB0/device is the USB interface; the device with the descriptors is above it.</summary>
    static UsbPortInfo? DescribeLinux(string port)
    {
        // Both hops are relative symlinks; resolve the tty first, or "device" resolves against the wrong parent.
        var tty = new DirectoryInfo($"/sys/class/tty/{Path.GetFileName(port)}");
        if (!tty.Exists) return null;
        var dev = new DirectoryInfo(Path.Combine(tty.ResolveLinkTarget(true)?.FullName ?? tty.FullName, "device"));
        if (!dev.Exists) return null;
        var dir = dev.ResolveLinkTarget(true) as DirectoryInfo ?? dev;
        for (int i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
        {
            string Attr(string name) => File.ReadAllText(Path.Combine(dir.FullName, name)).Trim();
            string? Opt(string name) => File.Exists(Path.Combine(dir.FullName, name)) ? Attr(name) : null;
            if (!File.Exists(Path.Combine(dir.FullName, "idVendor"))) continue;
            return new UsbPortInfo(Convert.ToInt32(Attr("idVendor"), 16), Convert.ToInt32(Attr("idProduct"), 16),
                                   Opt("manufacturer"), Opt("product"), Opt("serial"));
        }
        return null;
    }

    /// <summary>
    /// macOS's FTDI driver names ports /dev/cu.usbserial-&lt;USB serial&gt;. Without IOKit we can't read the
    /// vendor ID, so this assumes FTDI; other chips that use the usbserial- prefix get numeric location IDs instead.
    /// </summary>
    static UsbPortInfo? DescribeMac(string port)
    {
        const string prefix = "/dev/cu.usbserial-";
        if (!port.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var serial = port[prefix.Length..];
        return serial.All(char.IsDigit) ? null : new UsbPortInfo(0x0403, 0x6001, "FTDI", null, serial);
    }

    /// <summary>The FTDI VCP driver registers each device as FTDIBUS\VID_0403+PID_6001+&lt;serial&gt;&lt;port letter&gt;.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static UsbPortInfo? DescribeWindows(string port)
    {
        using var bus = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");
        if (bus is null) return null;
        foreach (var name in bus.GetSubKeyNames())
        {
            using var parms = bus.OpenSubKey($@"{name}\0000\Device Parameters");
            if (!string.Equals(parms?.GetValue("PortName") as string, port, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = name.Split('+');   // VID_0403, PID_6001, AB0PFDWZA
            if (parts.Length < 3) return null;
            return new UsbPortInfo(Convert.ToInt32(parts[0][4..], 16), Convert.ToInt32(parts[1][4..], 16),
                                   "FTDI", null, parts[2][..^1]);
        }
        return null;
    }
}
