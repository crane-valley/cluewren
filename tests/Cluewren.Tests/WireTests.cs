using System.Buffers;
using System.Diagnostics;
using Cluewren.Kit;

namespace Cluewren.Tests;

[Collection("wall clock")]
public class LineReaderTests
{
    [Fact]
    public void A_line_of_many_megabytes_is_read_whole_in_about_the_time_of_a_copy()
    {
        const int size = 64 << 20;
        var bytes = new byte[size + 3];
        bytes.AsSpan(0, size).Fill((byte)'x');
        bytes[size - 1] = (byte)'y';
        bytes[size] = (byte)'\n';
        bytes[size + 1] = (byte)'z';
        bytes[size + 2] = (byte)'\n';
        var reader = new LineReader(new MemoryStream(bytes), maxLine: 128 << 20);
        var watch = Stopwatch.StartNew();
        var (line, length) = reader.Next()!.Value;
        watch.Stop();
        try
        {
            Assert.Equal(size, length);
            Assert.Equal(size - 1, line.AsSpan(0, length).IndexOfAnyExcept((byte)'x'));
            Assert.Equal((byte)'y', line[size - 1]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(line);
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"reading the line took {watch.Elapsed.TotalSeconds:F1} s");
        var (next, nextLength) = reader.Next()!.Value;
        Assert.Equal(1, nextLength);
        Assert.Equal((byte)'z', next[0]);
        ArrayPool<byte>.Shared.Return(next);
        Assert.Null(reader.Next());
    }
}
