using System.Collections.Concurrent;
using System.Text;
using GhostShow.Inputs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Inputs;

/// <summary>The serial input with a stand-in port: commands arrive, and a missing or unplugged device is retried.</summary>
public class SerialInputServiceTests : IDisposable
{
    /// <summary>A port the test sends bytes through, or unplugs.</summary>
    sealed class FakeLink : ISerialLink
    {
        readonly BlockingCollection<byte[]?> _chunks = new();
        public volatile bool Disposed;

        public void Send(string text) => _chunks.Add(Encoding.Latin1.GetBytes(text));
        public void Unplug() => _chunks.Add(null);

        public int Read(byte[] buffer)
        {
            if (Disposed) throw new ObjectDisposedException("port");
            if (!_chunks.TryTake(out var chunk, 20)) return 0;   // quiet
            if (chunk is null) throw new IOException("The device was removed.");
            chunk.CopyTo(buffer, 0);
            return chunk.Length;
        }

        public void Dispose() => Disposed = true;
    }

    readonly ConcurrentQueue<string> _commands = new();
    readonly List<FakeLink> _opened = [];
    readonly SerialInputService _service;
    volatile bool _plugged = true;
    int _opens;

    public SerialInputServiceTests()
    {
        _service = new SerialInputService(NullLogger.Instance, Open, TimeSpan.FromMilliseconds(30));
        _service.CommandReceived += _commands.Enqueue;
    }

    public void Dispose() => _service.Dispose();

    ISerialLink Open(string port, int baud)
    {
        Interlocked.Increment(ref _opens);
        if (!_plugged) throw new FileNotFoundException($"{port} not found");
        var link = new FakeLink();
        lock (_opened) _opened.Add(link);
        return link;
    }

    FakeLink Latest
    {
        get { lock (_opened) return _opened[^1]; }
    }

    SerialInputState State => _service.Status.State;

    [Fact]
    public async Task It_reports_each_command_while_listening()
    {
        _service.Start("COM3", 9600);
        await Eventually.True(() => State == SerialInputState.Listening, "it listens");
        Assert.Equal(new SerialInputStatus(SerialInputState.Listening, "COM3", 9600), _service.Status);

        Latest.Send("channel 1 1\rchan");
        Latest.Send("nel 1 0\r\n");
        Latest.Send("NO ENDING");   // taken once the line goes quiet
        await Eventually.True(() => _commands.Count == 3, "three commands arrive");
        Assert.Equal(["channel 1 1", "channel 1 0", "NO ENDING"], _commands);
    }

    [Fact]
    public async Task A_missing_device_is_retried_until_it_is_plugged_in_and_again_when_unplugged()
    {
        _plugged = false;
        _service.Start("COM9", 9600);
        await Eventually.True(() => State == SerialInputState.Retrying, "it says the port is missing");
        Assert.Equal("COM9 isn't connected.", _service.Status.Problem);
        await Eventually.True(() => _opens >= 3, "it keeps trying");

        _plugged = true;
        await Eventually.True(() => State == SerialInputState.Listening, "it listens once plugged in");

        var first = Latest;
        first.Unplug();
        await Eventually.True(() => _opened.Count == 2 && State == SerialInputState.Listening, "it opens the port again");
        Assert.True(first.Disposed);
        Latest.Send("GO\r");
        await Eventually.True(() => _commands.Contains("GO"), "commands arrive again");
    }

    [Fact]
    public async Task Stopping_closes_the_port_and_starting_the_same_port_again_keeps_it_open()
    {
        _service.Start("COM3", 9600);
        await Eventually.True(() => State == SerialInputState.Listening, "it listens");
        _service.Start("COM3", 9600);
        Assert.Single(_opened);

        _service.Start("COM3", 19200);   // a new speed reopens it
        await Eventually.True(() => _opened.Count == 2 && _service.Status.BaudRate == 19200 && State == SerialInputState.Listening, "it reopens at the new speed");
        await Eventually.True(() => _opened[0].Disposed, "the old one is closed");

        _service.Stop();
        Assert.Equal(new SerialInputStatus(SerialInputState.Off, null, 0), _service.Status);
        await Eventually.True(() => Latest.Disposed, "the port is closed");
    }
}
