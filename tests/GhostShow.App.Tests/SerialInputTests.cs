using GhostShow.App.ViewModels;
using GhostShow.Core.Storage;
using GhostShow.Inputs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>Inputs → Serial input: picking the port and speed, saving them, and never taking a DMX adapter's port.</summary>
public class SerialInputTests : IDisposable
{
    /// <summary>A port that opens and stays quiet.</summary>
    sealed class QuietLink : ISerialLink
    {
        public int Read(byte[] buffer)
        {
            Thread.Sleep(10);
            return 0;
        }

        public void Dispose() { }
    }

    readonly MachineSettings _settings = new();
    readonly List<(int Universe, string Port)> _dmx = [];
    readonly List<string> _opened = [];
    readonly SerialInputService _service;
    readonly SerialInputViewModel _screen;
    int _saves;

    public SerialInputTests()
    {
        _service = new SerialInputService(NullLogger.Instance, (port, baud) =>
        {
            lock (_opened) _opened.Add($"{port} {baud}");
            return new QuietLink();
        });
        _screen = new SerialInputViewModel(_service, _settings, () => { _saves++; return Task.CompletedTask; }, () => _dmx,
                                           () => ["COM3", "COM4"], port => port == "COM4" ? "Ethereal FX QC-24" : null);
    }

    public void Dispose() => _service.Dispose();

    /// <summary>Waits for the port to open (it opens in the background), then shows how it stands, as the UI thread would.</summary>
    void Settle(SerialInputState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_service.Status.State != expected && DateTime.UtcNow < deadline) Thread.Sleep(5);
        _screen.Refresh();
    }

    [Fact]
    public void Picking_a_port_saves_it_and_listens_on_it()
    {
        _screen.RefreshPortsCommand.Execute(null);
        Assert.Equal(["None", "COM3", "COM4 · Ethereal FX QC-24"], _screen.Ports.Select(p => p.Label));   // the device's own name, where the computer says
        Assert.Equal("Off: pick the port your device is on.", _screen.StatusLabel);

        _screen.SelectedPort = _screen.Ports[1];
        Settle(SerialInputState.Listening);
        Assert.Equal(new SerialInputSettings("COM3", 9600, true), _settings.SerialInput);
        Assert.Equal(1, _saves);
        Assert.Equal("Listening on COM3 at 9600 baud.", _screen.StatusLabel);
        Assert.True(_screen.IsListening);
        Assert.Null(_screen.Warning);

        _screen.BaudRate = 19200;
        Settle(SerialInputState.Listening);
        Assert.Equal("Listening on COM3 at 19200 baud.", _screen.StatusLabel);

        _screen.IsEnabled = false;
        Assert.Equal(SerialInputState.Off, _service.Status.State);
        Assert.Equal("Off: COM3 isn't being listened to.", _screen.StatusLabel);
        Assert.False(_screen.IsListening);
        lock (_opened) Assert.Equal(["COM3 9600", "COM3 19200"], _opened);
    }

    [Fact]
    public void A_port_a_DMX_adapter_is_on_is_not_opened_and_says_so()
    {
        _dmx.Add((1, "COM3"));
        _screen.RefreshPortsCommand.Execute(null);
        Assert.Equal(["None", "COM3 (DMX universe 1)", "COM4 · Ethereal FX QC-24"], _screen.Ports.Select(p => p.Label));

        _screen.SelectedPort = _screen.Ports[1];
        Assert.Equal(SerialInputState.Off, _service.Status.State);
        Assert.Equal("COM3 is DMX universe 1's adapter: pick another port.", _screen.StatusLabel);
        Assert.True(_screen.IsFaulted);
        Assert.Equal("Serial input: COM3 is DMX universe 1's adapter: pick another port. See Inputs.", _screen.Warning);

        // The DMX adapter moves to another port: the serial input takes COM3 on the next tick.
        _dmx[0] = (1, "COM4");
        _screen.Tick();
        Settle(SerialInputState.Listening);
        Assert.Equal("Listening on COM3 at 9600 baud.", _screen.StatusLabel);
        lock (_opened) Assert.Equal(["COM3 9600"], _opened);
    }

    [Fact]
    public void A_saved_port_that_is_unplugged_stays_picked()
    {
        _settings.SerialInput = new SerialInputSettings("COM7", 4800);
        var screen = new SerialInputViewModel(_service, _settings, () => Task.CompletedTask, () => _dmx, () => ["COM3"]);
        screen.RefreshPortsCommand.Execute(null);
        Assert.Equal("COM7 (not connected)", screen.SelectedPort?.Label);
        Assert.Equal(4800, screen.BaudRate);
        Assert.Equal(new SerialInputSettings("COM7", 4800), _settings.SerialInput);
    }
}
