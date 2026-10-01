using System.Buffers;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Core.Media;

/// <summary>Describes a raw video format negotiated through caps.</summary>
public readonly record struct VideoFormat(string PixelFormat, int Width, int Height, Fraction Framerate)
{
    public int BytesPerPixel => PixelFormat switch
    {
        "GRAY8" => 1,
        "RGB" or "BGR" => 3,
        "RGBA" or "BGRA" or "RGBx" or "BGRx" or "xRGB" or "ARGB" => 4,
        _ => throw new NotSupportedException($"Pixel format {PixelFormat} is not packed/supported by this sample."),
    };

    /// <summary>Row stride; GStreamer aligns rows of packed formats to 4 bytes.</summary>
    public int Stride => (Width * BytesPerPixel + 3) & ~3;

    public int FrameSize => Stride * Height;

    public string ToCaps() =>
        $"video/x-raw,format={PixelFormat},width={Width},height={Height},framerate={Framerate}";

    internal static VideoFormat FromCaps(Gst.Caps caps)
    {
        var s = caps.GetStructure(0);
        return new VideoFormat(
            s.TryGetString("format") ?? "unknown",
            s.TryGetInt("width") ?? 0,
            s.TryGetInt("height") ?? 0,
            s.TryGetFraction("framerate") ?? new Fraction(0, 1));
    }
}

/// <summary>
/// A decoded frame copied out of GStreamer into pooled managed memory.
/// Dispose it to return the buffer to the pool (zero steady-state allocations).
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _rented;

    internal VideoFrame(byte[] rented, int length, VideoFormat format, long sequence, TimeSpan? pts)
    {
        _rented = rented;
        Length = length;
        Format = format;
        Sequence = sequence;
        Pts = pts;
    }

    public VideoFormat Format { get; }

    /// <summary>Monotonic index of the frame within this reader.</summary>
    public long Sequence { get; }

    /// <summary>Presentation timestamp of the source buffer.</summary>
    public TimeSpan? Pts { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Pixels => (_rented ?? throw new ObjectDisposedException(nameof(VideoFrame))).AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => (_rented ?? throw new ObjectDisposedException(nameof(VideoFrame))).AsMemory(0, Length);

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}
