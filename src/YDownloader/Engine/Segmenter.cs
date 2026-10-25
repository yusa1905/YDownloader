using System;
using System.Collections.Generic;

namespace YDownloader.Engine;

/// <summary>Teilt eine Datei in gleich große Bereiche (Start und Ende jeweils einschließlich).</summary>
public static class Segmenter
{
    public static IReadOnlyList<(long Start, long End)> Split(long total, int maxSegments, long minSegmentSize)
    {
        if (total <= 0) return Array.Empty<(long, long)>();
        if (maxSegments < 1) maxSegments = 1;
        if (minSegmentSize < 1) minSegmentSize = 1;

        long byMinSize = Math.Max(1, total / minSegmentSize);
        int count = (int)Math.Min(maxSegments, byMinSize);

        long baseSize = total / count;
        long remainder = total % count;
        var list = new List<(long, long)>(count);
        long start = 0;
        for (int i = 0; i < count; i++)
        {
            long size = baseSize + (i < remainder ? 1 : 0);
            list.Add((start, start + size - 1));
            start += size;
        }
        return list;
    }
}
