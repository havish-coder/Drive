using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace CloudSync.Core.Chunking;

/// <summary>
/// Template Method: owns the pipe mechanics (read, buffer until a full window is available,
/// copy into a pooled buffer, advance). Subclasses only decide where to cut.
/// </summary>
public abstract class ChunkerBase : IChunker
{
    public abstract string Name { get; }
    public abstract int MaxChunkSize { get; }

    /// <summary>
    /// Returns the length of the next chunk, in [1, min(buffer.Length, MaxChunkSize)].
    /// Only called when <paramref name="buffer"/> holds at least MaxChunkSize bytes or the stream has ended,
    /// so each byte is scanned once.
    /// </summary>
    protected abstract int FindCutPoint(ReadOnlySequence<byte> buffer, bool isFinal);

    public async IAsyncEnumerable<RawChunk> ChunkAsync(
        PipeReader source, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var index = 0;
        long offset = 0;

        while (true)
        {
            var result = await source.ReadAsync(ct);
            var buffer = result.Buffer;

            while (buffer.Length > 0 && (buffer.Length >= MaxChunkSize || result.IsCompleted))
            {
                var cut = FindCutPoint(buffer, result.IsCompleted);
                var owned = ArrayPool<byte>.Shared.Rent(cut);
                buffer.Slice(0, cut).CopyTo(owned);
                buffer = buffer.Slice(cut);

                yield return new RawChunk(index++, offset, owned, cut);
                offset += cut;
            }

            // Consumed what we cut; examined everything so the next read waits for more data.
            source.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted) yield break;
        }
    }
}
