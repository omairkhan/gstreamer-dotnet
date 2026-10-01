using System.Runtime.InteropServices;

namespace MediaFlow.Core.Media;

/// <summary>
/// Pushes frames generated or processed in C# into a pipeline through <c>appsrc</c>:
/// render overlays, synthesise video, re-inject analysed frames, feed frames from a
/// proprietary SDK (industrial cameras, medical devices) into standard encoders and streamers.
/// </summary>
public sealed class FrameWriter
{
    private readonly GstApp.AppSrc _source;
    private readonly TimeSpan _frameDuration;
    private long _frames;

    public FrameWriter(GstApp.AppSrc source, VideoFormat format, bool isLive = false)
    {
        _source = source;
        Format = format;
        _frameDuration = TimeSpan.FromSeconds(format.Framerate.Denominator / (double)format.Framerate.Numerator);

        _source.Caps = Gst.Caps.FromString(format.ToCaps())
            ?? throw new ArgumentException($"Invalid caps: {format.ToCaps()}", nameof(format));
        _source.Format = Gst.Format.Time;
        _source.IsLive = isLive;
        // Block the producer when downstream is full: natural back-pressure for offline rendering.
        _source.Block = !isLive;
        _source.MaxBytes = (ulong)format.FrameSize * 8;
    }

    public VideoFormat Format { get; }

    public long FramesWritten => _frames;

    /// <summary>Copies one frame into a new GstBuffer with correct PTS/duration and pushes it.</summary>
    public Gst.FlowReturn Write(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != Format.FrameSize)
        {
            throw new ArgumentException($"Expected {Format.FrameSize} bytes, got {pixels.Length}.", nameof(pixels));
        }

        // NewMemdup copies, so handing it a writable view of the caller's read-only span is safe.
        using var buffer = Gst.Buffer.NewMemdup(MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(pixels), pixels.Length));
        buffer.Pts = _frameDuration * _frames;
        buffer.Duration = _frameDuration;
        _frames++;
        return _source.PushBuffer(buffer);
    }

    /// <summary>Signals end-of-stream so encoders flush and muxers finalise the file.</summary>
    public Gst.FlowReturn Complete() => _source.EndOfStream();
}
