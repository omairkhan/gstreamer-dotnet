using System.Numerics;
using System.Runtime.Intrinsics;
using MediaFlow.Core.Diagnostics;
using MediaFlow.Core.Media;

namespace MediaFlow.Core.Analytics;

public sealed record MotionOptions
{
    /// <summary>Per-pixel luminance delta (0-255) that counts as "changed".</summary>
    public byte PixelThreshold { get; init; } = 25;

    /// <summary>Fraction of changed pixels (after smoothing) that starts a motion event.</summary>
    public double StartRatio { get; init; } = 0.004;

    /// <summary>Lower threshold that ends a motion event (hysteresis avoids flapping).</summary>
    public double StopRatio { get; init; } = 0.001;

    /// <summary>Exponential smoothing factor for the changed-pixel ratio (0..1, higher = more reactive).</summary>
    public double Smoothing { get; init; } = 0.5;

    /// <summary>Minimum quiet time before a new event may start.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(1);
}

public abstract record MotionEvent(string StreamId, long FrameSequence, TimeSpan? StreamTime, double Score, DateTimeOffset At);

public sealed record MotionStarted(string StreamId, long FrameSequence, TimeSpan? StreamTime, double Score, DateTimeOffset At)
    : MotionEvent(StreamId, FrameSequence, StreamTime, Score, At);

public sealed record MotionEnded(string StreamId, long FrameSequence, TimeSpan? StreamTime, double Score, DateTimeOffset At, TimeSpan Length)
    : MotionEvent(StreamId, FrameSequence, StreamTime, Score, At);

/// <summary>
/// Real-time motion detection on GRAY8 frames by SIMD frame differencing.
/// GStreamer does the heavy lifting (decode, scale down, convert to grey) so C# only
/// touches e.g. 320x180 bytes per frame: a few microseconds with Vector128/256.
/// </summary>
public sealed class MotionDetector(string streamId, MotionOptions? options = null)
{
    private readonly MotionOptions _options = options ?? new MotionOptions();
    private byte[]? _previous;
    private double _smoothed;
    private bool _inMotion;
    private DateTimeOffset _motionSince;
    private DateTimeOffset _lastEnded = DateTimeOffset.MinValue;

    public double CurrentScore => _smoothed;

    public bool InMotion => _inMotion;

    /// <summary>Analyses one frame and returns a state transition, if any.</summary>
    public MotionEvent? Process(VideoFrame frame, DateTimeOffset? now = null)
    {
        if (frame.Format.PixelFormat != "GRAY8")
        {
            throw new NotSupportedException("MotionDetector expects GRAY8 frames; add 'videoconvert ! video/x-raw,format=GRAY8' to the pipeline.");
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var pixels = frame.Pixels;
        var at = now ?? DateTimeOffset.UtcNow;

        if (_previous is null || _previous.Length != pixels.Length)
        {
            _previous = pixels.ToArray();
            return null;
        }

        var changed = CountChangedPixels(_previous, pixels, _options.PixelThreshold);
        pixels.CopyTo(_previous);

        var ratio = (double)changed / pixels.Length;
        _smoothed = (_options.Smoothing * ratio) + ((1 - _options.Smoothing) * _smoothed);

        MediaFlowTelemetry.FrameAnalysisMs.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        if (!_inMotion && _smoothed >= _options.StartRatio && at - _lastEnded >= _options.Cooldown)
        {
            _inMotion = true;
            _motionSince = at;
            MediaFlowTelemetry.MotionEvents.Add(1);
            return new MotionStarted(streamId, frame.Sequence, frame.Pts, _smoothed, at);
        }

        if (_inMotion && _smoothed <= _options.StopRatio)
        {
            _inMotion = false;
            _lastEnded = at;
            return new MotionEnded(streamId, frame.Sequence, frame.Pts, _smoothed, at, at - _motionSince);
        }

        return null;
    }

    /// <summary>Counts pixels whose absolute difference exceeds <paramref name="threshold"/>.</summary>
    internal static int CountChangedPixels(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, byte threshold)
    {
        var count = 0;
        var i = 0;

        if (Vector256.IsHardwareAccelerated && current.Length >= Vector256<byte>.Count)
        {
            var limit = Vector256.Create(threshold);
            for (; i <= current.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                var a = Vector256.Create(previous.Slice(i, Vector256<byte>.Count));
                var b = Vector256.Create(current.Slice(i, Vector256<byte>.Count));
                var diff = Vector256.Max(a, b) - Vector256.Min(a, b); // |a-b| without overflow
                count += BitOperations.PopCount(Vector256.GreaterThan(diff, limit).ExtractMostSignificantBits());
            }
        }
        else if (Vector128.IsHardwareAccelerated && current.Length >= Vector128<byte>.Count)
        {
            var limit = Vector128.Create(threshold);
            for (; i <= current.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                var a = Vector128.Create(previous.Slice(i, Vector128<byte>.Count));
                var b = Vector128.Create(current.Slice(i, Vector128<byte>.Count));
                var diff = Vector128.Max(a, b) - Vector128.Min(a, b);
                count += BitOperations.PopCount(Vector128.GreaterThan(diff, limit).ExtractMostSignificantBits());
            }
        }

        for (; i < current.Length; i++)
        {
            if (Math.Abs(previous[i] - current[i]) > threshold) count++;
        }

        return count;
    }
}
