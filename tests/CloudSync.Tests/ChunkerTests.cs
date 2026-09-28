using CloudSync.Core.Chunking;
using static CloudSync.Tests.TestUtil;

namespace CloudSync.Tests;

public class ChunkerTests
{
    [Fact]
    public async Task FixedSize_cuts_at_exact_boundaries_and_reassembles()
    {
        var data = RandomBytes(1_000_000, seed: 1);
        var chunks = await ChunkAllAsync(new FixedSizeChunker(65_536), data);

        Assert.Equal(16, chunks.Count);
        Assert.All(chunks.SkipLast(1), c => Assert.Equal(65_536, c.Data.Length));
        Assert.Equal(1_000_000 - 15 * 65_536, chunks[^1].Data.Length);
        Assert.Equal(data, chunks.SelectMany(c => c.Data).ToArray());
    }

    [Fact]
    public async Task Empty_stream_yields_no_chunks()
    {
        Assert.Empty(await ChunkAllAsync(new FixedSizeChunker(1024), []));
        Assert.Empty(await ChunkAllAsync(new FastCdcChunker(256, 1024, 4096), []));
    }

    [Fact]
    public async Task FastCdc_respects_bounds_is_deterministic_and_reassembles()
    {
        var data = RandomBytes(4_000_000, seed: 2);
        var chunker = new FastCdcChunker(16 * 1024, 64 * 1024, 256 * 1024);

        var first = await ChunkAllAsync(chunker, data);
        var second = await ChunkAllAsync(chunker, data);

        Assert.Equal(first.Select(c => c.Hash), second.Select(c => c.Hash));
        Assert.All(first.SkipLast(1), c => Assert.InRange(c.Data.Length, 16 * 1024, 256 * 1024));
        Assert.Equal(data, first.SelectMany(c => c.Data).ToArray());

        var average = first.Average(c => c.Data.Length);
        Assert.InRange(average, 32 * 1024, 128 * 1024);
    }

    [Fact]
    public async Task Inserting_one_byte_shifts_every_fixed_chunk_but_few_cdc_chunks()
    {
        var original = RandomBytes(4 * 1024 * 1024, seed: 3);
        var edited = new byte[original.Length + 1];
        edited[0] = 0x42;
        original.CopyTo(edited, 1);

        double SharedFraction(List<(string Hash, byte[] Data)> before, List<(string Hash, byte[] Data)> after)
        {
            var known = before.Select(c => c.Hash).ToHashSet();
            return after.Count(c => known.Contains(c.Hash)) / (double)after.Count;
        }

        var fixedChunker = new FixedSizeChunker(64 * 1024);
        var fixedShared = SharedFraction(await ChunkAllAsync(fixedChunker, original), await ChunkAllAsync(fixedChunker, edited));

        var cdc = new FastCdcChunker(16 * 1024, 64 * 1024, 256 * 1024);
        var cdcShared = SharedFraction(await ChunkAllAsync(cdc, original), await ChunkAllAsync(cdc, edited));

        Assert.Equal(0, fixedShared);          // boundary-shift problem
        Assert.True(cdcShared > 0.9, $"CDC shared only {cdcShared:P0}"); // boundaries re-sync after the edit
    }
}
