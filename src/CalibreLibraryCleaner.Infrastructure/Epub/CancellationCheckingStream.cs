namespace CalibreLibraryCleaner.Infrastructure.Epub;

internal sealed class CancellationCheckingStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    private const int MaximumReadSize = 128 * 1024;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, Math.Min(count, MaximumReadSize));
    }

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer[..Math.Min(buffer.Length, MaximumReadSize)]);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        return inner.ReadAsync(buffer[..Math.Min(buffer.Length, MaximumReadSize)], token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
