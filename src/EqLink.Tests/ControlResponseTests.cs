using EqLink.Core;

namespace EqLink.Tests;

internal static partial class Program
{
    private static async Task ControlResponseLimits()
    {
        using (var content = new ByteArrayContent("{\"ok\":true}"u8.ToArray()))
            Check((await ControlApiClient.ReadBoundedResponseAsync(content, default)).AsSpan().SequenceEqual("{\"ok\":true}"u8), "Valid control response changed");
        using (var stream = new UnknownLengthResponse(128_000))
        using (var content = new StreamContent(stream))
            Check((await ControlApiClient.ReadBoundedResponseAsync(content, default)).Length == 128_000, "Exact response limit rejected");
        using (var stream = new UnknownLengthResponse(4_000_000))
        using (var content = new StreamContent(stream))
        {
            try { await ControlApiClient.ReadBoundedResponseAsync(content, default); throw new Exception("Unbounded control response accepted"); }
            catch (IOException ex) when (ex.Message == "CONTROL_RESPONSE_TOO_LARGE") { }
            Check(stream.BytesRead == 128_001, "Oversized unknown-length response was fully buffered");
        }
        using (var stream = new UnknownLengthResponse(4_000_000))
        using (var content = new StreamContent(stream))
        {
            content.Headers.ContentLength = 4_000_000;
            try { await ControlApiClient.ReadBoundedResponseAsync(content, default); throw new Exception("Oversized Content-Length accepted"); }
            catch (IOException ex) when (ex.Message == "CONTROL_RESPONSE_TOO_LARGE") { }
            Check(stream.BytesRead == 0, "Advertised oversize response was read");
        }
        Console.WriteLine("Control response limits: bounded chunked body, Content-Length rejection and exact boundary passed");
    }

    private sealed class UnknownLengthResponse(int size) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = Math.Min(count, size - BytesRead);
            buffer.AsSpan(offset, length).Fill((byte)'x'); BytesRead += length; return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, size - BytesRead);
            buffer.Span[..length].Fill((byte)'x'); BytesRead += length; return ValueTask.FromResult(length);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
