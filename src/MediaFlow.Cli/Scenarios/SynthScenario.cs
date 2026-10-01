using MediaFlow.Core;
using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 2: pushing pixels from C#.
/// Frames are rendered in managed code (think: charts, dashboards, frames from a proprietary
/// camera SDK, ML-annotated frames) and pushed through appsrc with exact PTS into a standard
/// H.264/MP4 encoder. appsrc 'block=true' gives natural back-pressure: we never outrun the encoder.
/// </summary>
internal static class SynthScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var output = args.Get("out", "synth.mp4");
        var seconds = args.GetInt("seconds", 5);
        var format = new VideoFormat("RGB", 640, 360, new Fraction(30, 1));
        GstRuntime.Require("appsrc", "x264enc", "h264parse", "mp4mux");

        var description = PipelineDescription.Create()
            .Element("appsrc", ("name", "src"))
            .Element("videoconvert")
            .Element("x264enc", ("speed-preset", "fast"), ("bitrate", 1500))
            .Element("h264parse")
            .Element("mp4mux")
            .Element("filesink", ("location", output));

        Out.Title("synth: C#-rendered frames -> appsrc -> MP4");
        Out.Pipeline(description.ToString());

        await using var host = PipelineHost.Parse(description);
        var writer = new FrameWriter(host.GetElement<GstApp.AppSrc>("src"), format);
        await host.StartAsync(waitForPreroll: false, cancellationToken);

        var total = seconds * 30;
        var pixels = new byte[format.FrameSize];

        // Produce on a worker thread: PushBuffer blocks when the encoder is busy.
        await Task.Run(() =>
        {
            for (var i = 0; i < total && !cancellationToken.IsCancellationRequested; i++)
            {
                Render(pixels, format, i, total);
                var flow = writer.Write(pixels);
                if (flow != Gst.FlowReturn.Ok) throw new InvalidOperationException($"push failed: {flow}");
                if (i % 30 == 0) Console.Write($"\r  rendered {i}/{total} frames");
            }
            writer.Complete();
        }, CancellationToken.None);
        Console.WriteLine($"\r  rendered {writer.FramesWritten}/{total} frames");

        var outcome = await host.Completion;
        Out.Event("done", $"{outcome}: {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024} KiB)", ConsoleColor.Green);
        return 0;
    }

    /// <summary>An animated plasma background with a live "KPI" bar chart on top.</summary>
    private static void Render(Span<byte> rgb, VideoFormat f, int frame, int total)
    {
        var t = frame / 30.0;
        for (var y = 0; y < f.Height; y++)
        {
            var row = rgb.Slice(y * f.Stride, f.Width * 3);
            for (var x = 0; x < f.Width; x++)
            {
                var v = Math.Sin(x / 40.0 + t) + Math.Sin((y / 30.0) + (t * 1.3)) + Math.Sin((x + y) / 60.0 - t);
                row[(x * 3) + 0] = (byte)(40 + (30 * Math.Sin(v)));
                row[(x * 3) + 1] = (byte)(60 + (40 * Math.Sin(v + 2)));
                row[(x * 3) + 2] = (byte)(110 + (60 * Math.Sin(v + 4)));
            }
        }

        const int bars = 8;
        for (var b = 0; b < bars; b++)
        {
            var height = (int)((0.3 + (0.25 * (1 + Math.Sin(t * 2 + b)))) * f.Height * 0.8);
            var x0 = 40 + (b * 70);
            for (var y = f.Height - 30 - height; y < f.Height - 30; y++)
            {
                var row = rgb.Slice(y * f.Stride, f.Width * 3);
                for (var x = x0; x < x0 + 50; x++)
                {
                    row[(x * 3) + 0] = 255;
                    row[(x * 3) + 1] = (byte)(180 - (b * 15));
                    row[(x * 3) + 2] = 40;
                }
            }
        }

        // progress line at the bottom
        var progress = (int)((double)frame / total * f.Width);
        var last = rgb.Slice((f.Height - 4) * f.Stride, f.Width * 3);
        for (var x = 0; x < progress; x++) { last[x * 3] = 255; last[(x * 3) + 1] = 255; last[(x * 3) + 2] = 255; }
    }
}
