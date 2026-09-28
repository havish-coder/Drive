using System.Buffers;

namespace CloudSync.Core.Chunking;

/// <summary>
/// One chunk cut from a stream. Owns a buffer rented from <see cref="ArrayPool{T}.Shared"/>;
/// dispose it to return the buffer.
/// </summary>
public sealed class RawChunk : IDisposable
{
    private byte[]? _buffer;

    public RawChunk(int sequenceIndex, long offset, byte[] buffer, int length)
    {
        SequenceIndex = sequenceIndex;
        Offset = offset;
        Length = length;
        _buffer = buffer;
    }

    public int SequenceIndex { get; }
    public long Offset { get; }
    public int Length { get; }

    public ReadOnlyMemory<byte> Memory =>
        (_buffer ?? throw new ObjectDisposedException(nameof(RawChunk))).AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => Memory.Span;

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
    }
}
