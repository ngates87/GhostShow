using System.IO.Pipelines;
using System.Net.WebSockets;

namespace GhostShow.Web;

/// <summary>
/// An in-memory connection, for the show computer's own window: two WebSockets joined by pipes. The window works the
/// show exactly as a device does (the same messages, see <see cref="RemoteServer.RunLocalAsync"/>), with no network and
/// no port, so it works with Remote Access off.
/// </summary>
public static class LocalConnection
{
    public static (WebSocket Client, WebSocket Server) Create()
    {
        Pipe toServer = new(), toClient = new();
        static WebSocketCreationOptions Options(bool server) => new() { IsServer = server, KeepAliveInterval = Timeout.InfiniteTimeSpan };
        return (WebSocket.CreateFromStream(new Duplex(toClient.Reader, toServer.Writer), Options(false)),
                WebSocket.CreateFromStream(new Duplex(toServer.Reader, toClient.Writer), Options(true)));
    }

    /// <summary>One end: reads what the other end writes, and the other way round.</summary>
    sealed class Duplex(PipeReader reader, PipeWriter writer) : Stream
    {
        readonly Stream _in = reader.AsStream();
        readonly Stream _out = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => _in.ReadAsync(buffer, offset, count, cancel);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) => _in.ReadAsync(buffer, cancel);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancel) => _out.WriteAsync(buffer, offset, count, cancel);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default) => _out.WriteAsync(buffer, cancel);
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken cancel) => _out.FlushAsync(cancel);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _in.Dispose();   // done reading...
                _out.Dispose();   // ...and the other end reads the end of the stream
            }
            base.Dispose(disposing);
        }
    }
}
