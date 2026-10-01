using MediaFlow.Core;
using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Tests;

/// <summary>Runs real GStreamer pipelines (skipped automatically when GStreamer is not installed).</summary>
public class PipelineIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GStreamerFact]
    public async Task Appsink_frames_arrive_as_async_stream_with_negotiated_format()
    {
        await using var host = PipelineHost.Parse(
            "videotestsrc num-buffers=12 ! video/x-raw,format=GRAY8,width=160,height=90,framerate=30/1 ! appsink name=out sync=false");
        await host.StartAsync(Ct);

        var frames = new List<(long Seq, int Length, VideoFormat Format, TimeSpan? Pts)>();
        await foreach (var frame in new FrameReader(host.GetElement<GstApp.AppSink>("out"), dropWhenBehind: false).ReadAllAsync(Ct))
        {
            using (frame) frames.Add((frame.Sequence, frame.Length, frame.Format, frame.Pts));
        }

        Assert.Equal(12, frames.Count);
        Assert.All(frames, f => Assert.Equal(160 * 90, f.Length));
        Assert.Equal(new VideoFormat("GRAY8", 160, 90, new Fraction(30, 1)), frames[0].Format);
        Assert.Equal(TimeSpan.Zero, frames[0].Pts);
        Assert.Equal(TimeSpan.FromSeconds(11 / 30.0).TotalMilliseconds, frames[^1].Pts!.Value.TotalMilliseconds, 1);
        Assert.Equal(PipelineOutcome.EndOfStream, await host.Completion);
    }

    [GStreamerFact]
    public async Task Appsrc_to_appsink_round_trip_preserves_pixels_and_timestamps()
    {
        var format = new VideoFormat("GRAY8", 8, 4, new Fraction(25, 1));
        await using var host = PipelineHost.Parse("appsrc name=in ! appsink name=out sync=false");
        var writer = new FrameWriter(host.GetElement<GstApp.AppSrc>("in"), format);
        await host.StartAsync(waitForPreroll: false, Ct);

        var reader = new FrameReader(host.GetElement<GstApp.AppSink>("out"), dropWhenBehind: false);
        var received = new List<(byte First, TimeSpan? Pts)>();
        var consume = Task.Run(async () =>
        {
            await foreach (var f in reader.ReadAllAsync(Ct)) using (f) received.Add((f.Pixels[0], f.Pts));
        }, Ct);

        for (byte i = 0; i < 5; i++)
        {
            var pixels = new byte[format.FrameSize];
            pixels.AsSpan().Fill(i);
            Assert.Equal(Gst.FlowReturn.Ok, writer.Write(pixels));
        }
        writer.Complete();
        await consume;

        Assert.Equal([0, 1, 2, 3, 4], received.Select(r => (int)r.First));
        Assert.Equal(TimeSpan.FromMilliseconds(160), received[^1].Pts);
    }

    [GStreamerFact]
    public async Task Errors_surface_as_typed_events_and_faulted_completion()
    {
        await using var host = PipelineHost.Parse("filesrc location=/nope/missing.mp4 ! fakesink");

        var ex = await Assert.ThrowsAsync<PipelineFailedException>(() => host.StartAsync(Ct));
        Assert.Contains("No such file", ex.Message);
        Assert.True(host.Events.TryRead(out var evt));
        Assert.IsType<PipelineError>(evt);
    }

    [GStreamerFact]
    public void Invalid_syntax_throws_GException() =>
        Assert.Throws<GLib.GException>(() => PipelineHost.Parse("videotestsrc ! no-such-element-anywhere"));

    [GStreamerFact]
    public async Task Graceful_stop_finalises_an_mp4()
    {
        Assert.SkipUnless(GstRuntime.HasElement("x264enc") && GstRuntime.HasElement("mp4mux"), "x264enc/mp4mux not installed");
        var file = Path.Combine(Path.GetTempPath(), $"mediaflow-{Guid.NewGuid():N}.mp4");
        try
        {
            await using (var host = PipelineHost.Parse(
                $"videotestsrc is-live=true ! video/x-raw,width=320,height=180 ! x264enc tune=zerolatency ! h264parse ! mp4mux ! filesink location={file}"))
            {
                await host.StartAsync(Ct);
                await Task.Delay(1000, Ct);
                await host.StopAsync(cancellationToken: Ct);
            }

            // A finalised MP4 has a duration; a killed one does not.
            await using var check = PipelineHost.Parse($"filesrc location={file} ! qtdemux ! fakesink");
            await check.PauseAsync(Ct);
            Assert.True(check.Duration > TimeSpan.FromMilliseconds(500), $"duration was {check.Duration}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [GStreamerFact]
    public async Task Dot_graph_describes_the_pipeline()
    {
        await using var host = PipelineHost.Parse("videotestsrc name=cam ! fakesink");
        Assert.Contains("digraph", host.ToDot());
    }
}
