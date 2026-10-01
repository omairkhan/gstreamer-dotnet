using System.Buffers;
using MediaFlow.Core.Analytics;
using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Tests;

public class MotionDetectorTests
{
    private static readonly VideoFormat Gray = new("GRAY8", 64, 36, new Fraction(10, 1));

    private static VideoFrame Frame(long seq, Action<Span<byte>>? paint = null)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(Gray.FrameSize);
        var span = bytes.AsSpan(0, Gray.FrameSize);
        span.Fill(16);
        paint?.Invoke(span);
        return new VideoFrame(bytes, Gray.FrameSize, Gray, seq, null);
    }

    private static void Square(Span<byte> px, int x0, int size)
    {
        for (var y = 10; y < 10 + size; y++)
            for (var x = x0; x < x0 + size; x++)
                px[(y * Gray.Stride) + x] = 235;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(33)]
    [InlineData(1000)]
    public void Simd_count_matches_scalar(int length)
    {
        var rnd = new Random(length);
        var a = new byte[length];
        var b = new byte[length];
        rnd.NextBytes(a);
        rnd.NextBytes(b);

        var expected = a.Zip(b).Count(p => Math.Abs(p.First - p.Second) > 25);
        Assert.Equal(expected, MotionDetector.CountChangedPixels(a, b, 25));
    }

    [Fact]
    public void Static_scene_produces_no_events()
    {
        var detector = new MotionDetector("t");
        for (var i = 0; i < 20; i++)
        {
            using var f = Frame(i);
            Assert.Null(detector.Process(f));
        }
    }

    [Fact]
    public void Moving_object_starts_and_ends_an_event_with_hysteresis()
    {
        var detector = new MotionDetector("t", new MotionOptions { StartRatio = 0.02, StopRatio = 0.005, Cooldown = TimeSpan.Zero });
        var t0 = DateTimeOffset.UnixEpoch;
        var events = new List<MotionEvent>();

        for (var i = 0; i < 30; i++)
        {
            var moving = i is >= 5 and < 15;
            using var f = Frame(i, px => { if (moving) Square(px, 2 * i, 8); });
            if (detector.Process(f, t0.AddMilliseconds(100 * i)) is { } e) events.Add(e);
        }

        Assert.Collection(events,
            e => Assert.IsType<MotionStarted>(e),
            e => Assert.True(Assert.IsType<MotionEnded>(e).Length > TimeSpan.Zero));
    }

    [Fact]
    public void Rejects_colour_frames()
    {
        var rgb = new VideoFormat("RGB", 4, 4, new Fraction(1, 1));
        using var frame = new VideoFrame(ArrayPool<byte>.Shared.Rent(rgb.FrameSize), rgb.FrameSize, rgb, 0, null);
        Assert.Throws<NotSupportedException>(() => new MotionDetector("t").Process(frame));
    }
}
