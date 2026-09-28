using System.Buffers;

namespace CloudSync.Core.Chunking;

/// <summary>
/// Content-Defined Chunking (FastCDC, Xia et al. 2016). A Gear rolling hash over the last ~64 bytes
/// decides boundaries, so they depend on content rather than offset: an insert only changes the
/// chunks around it. Normalized chunking uses a harder mask before <c>avg</c> and an easier one after,
/// pulling chunk sizes toward the average.
/// </summary>
public sealed class FastCdcChunker : ChunkerBase
{
    // Must be identical on every client and server, so it is generated from a fixed seed.
    private static readonly ulong[] Gear = BuildGearTable(0x436C6F756453796EUL);

    private readonly int _min, _avg, _max;
    private readonly ulong _maskHard, _maskEasy;

    public FastCdcChunker(int minSize, int avgSize, int maxSize)
    {
        if (minSize < 64 || minSize >= avgSize || avgSize >= maxSize)
            throw new ArgumentException("Require 64 <= min < avg < max.");

        _min = minSize;
        _avg = avgSize;
        _max = maxSize;

        var bits = (int)Math.Round(Math.Log2(avgSize));
        _maskHard = TopBits(bits + 2);
        _maskEasy = TopBits(bits - 2);
    }

    public override string Name => $"fastcdc-{_min}-{_avg}-{_max}";
    public override int MaxChunkSize => _max;

    protected override int FindCutPoint(ReadOnlySequence<byte> buffer, bool isFinal)
    {
        var length = (int)Math.Min(buffer.Length, _max);
        if (length <= _min) return length;

        var normal = Math.Min(_avg, length);
        ulong fp = 0;
        var pos = _min;

        foreach (var segment in buffer.Slice(_min, length - _min))
        {
            var span = segment.Span;
            for (var i = 0; i < span.Length; i++, pos++)
            {
                fp = (fp << 1) + Gear[span[i]];
                if ((fp & (pos < normal ? _maskHard : _maskEasy)) == 0)
                    return pos + 1;
            }
        }

        return length;
    }

    // High bits of the Gear hash depend on the most bytes, so the masks test those.
    private static ulong TopBits(int n) => n <= 0 ? 0 : ulong.MaxValue << (64 - n);

    private static ulong[] BuildGearTable(ulong seed)
    {
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            // SplitMix64
            seed += 0x9E3779B97F4A7C15UL;
            var z = seed;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }
}
