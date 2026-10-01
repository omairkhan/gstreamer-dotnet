using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MediaFlow.Core.Diagnostics;

namespace MediaFlow.Core.Media;

/// <summary>
/// Bridges an <c>appsink</c> into .NET as an <see cref="IAsyncEnumerable{T}"/> of frames.
/// A dedicated pull thread copies samples into pooled buffers and hands them over through a
/// bounded channel. When the consumer is slower than the camera, the oldest frames are dropped
/// (and counted) instead of stalling the whole pipeline: the right trade-off for live analytics.
/// For offline work (files, batch jobs) pass <c>dropWhenBehind: false</c> to get lossless
/// back-pressure all the way up to the source instead.
/// </summary>
public sealed class FrameReader
{
    private readonly GstApp.AppSink _sink;
    private readonly int _capacity;
    private readonly bool _dropWhenBehind;

    public FrameReader(GstApp.AppSink sink, int capacity = 4, bool dropWhenBehind = true)
    {
        _sink = sink;
        _capacity = capacity;
        _dropWhenBehind = dropWhenBehind;

        // Keep the native side lean too: never queue more than a couple of frames inside GStreamer.
        _sink.MaxBuffers = 2;
        _sink.Drop = dropWhenBehind;
        _sink.EmitSignals = false;
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    private long _dropped;

    public async IAsyncEnumerable<VideoFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = _dropWhenBehind ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
        }, dropped =>
        {
            dropped.Dispose();
            Interlocked.Increment(ref _dropped);
            MediaFlowTelemetry.FramesDropped.Add(1);
        });

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pull = new Thread(() => Pull(channel.Writer, stop.Token)) { IsBackground = true, Name = "gst-appsink-pull" };
        pull.Start();

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                MediaFlowTelemetry.FramesProcessed.Add(1);
                yield return frame;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            pull.Join(TimeSpan.FromSeconds(2));
            while (channel.Reader.TryRead(out var leftover)) leftover.Dispose();
        }
    }

    private void Pull(ChannelWriter<VideoFrame> writer, CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(200).ToClockTime();
        long sequence = 0;
        VideoFormat? format = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var sample = _sink.TryPullSample(timeout);
                if (sample is null)
                {
                    if (_sink.IsEos()) break;
                    continue;
                }

                format ??= VideoFormat.FromCaps(sample.GetCaps()
                    ?? throw new InvalidOperationException("appsink sample without caps"));

                using var buffer = sample.GetBuffer();
                if (buffer is null) continue;

                var size = (int)buffer.GetSize();
                var rented = ArrayPool<byte>.Shared.Rent(size);
                buffer.Extract(0, rented.AsSpan(0, size));
                var frame = new VideoFrame(rented, size, format.Value, sequence++, buffer.Pts);
                if (_dropWhenBehind)
                {
                    writer.TryWrite(frame);
                }
                else
                {
                    // Dedicated thread: blocking here is intended. appsink fills up, and GStreamer
                    // back-pressures upstream instead of dropping anything.
                    try
                    {
                        writer.WriteAsync(frame, token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        frame.Dispose();
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return;
        }
        writer.TryComplete();
    }
}
