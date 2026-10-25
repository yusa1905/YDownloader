using System.Linq;
using Xunit;
using YDownloader.Engine;

namespace YDownloader.Tests;

public class SegmenterTests
{
    [Theory]
    [InlineData(10_000_000, 8, 1_000_000)]
    [InlineData(10_000_001, 8, 1_000_000)]
    [InlineData(1_000_003, 4, 1)]
    [InlineData(7, 8, 1)]
    public void Segments_cover_file_without_gaps_or_overlap(long total, int max, long min)
    {
        var parts = Segmenter.Split(total, max, min);

        Assert.True(parts.Count >= 1 && parts.Count <= max);
        Assert.Equal(0, parts[0].Start);
        Assert.Equal(total - 1, parts[^1].End);
        for (int i = 1; i < parts.Count; i++)
            Assert.Equal(parts[i - 1].End + 1, parts[i].Start);
        Assert.Equal(total, parts.Sum(p => p.End - p.Start + 1));
    }

    [Fact]
    public void Small_file_gets_single_segment()
    {
        var parts = Segmenter.Split(500_000, 8, 1_000_000);
        Assert.Single(parts);
        Assert.Equal((0L, 499_999L), parts[0]);
    }

    [Fact]
    public void Segment_count_respects_minimum_size()
    {
        var parts = Segmenter.Split(3_500_000, 8, 1_000_000);
        Assert.Equal(3, parts.Count);
    }

    [Fact]
    public void Empty_file_has_no_segments() => Assert.Empty(Segmenter.Split(0, 8, 1_000_000));
}
