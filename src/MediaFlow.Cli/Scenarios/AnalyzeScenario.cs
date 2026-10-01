using System.Diagnostics;
using MediaFlow.Core;
using MediaFlow.Core.Analytics;
using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 2: pulling pixels into C#.
/// GStreamer decodes, scales and converts to GRAY8 (native, fast); appsink hands the frames to
/// an IAsyncEnumerable; a SIMD motion detector runs in managed code. Meanwhile we change a
/// property on the running pipeline to simulate "someone walks through the scene".
/// </summary>
internal static class AnalyzeScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var source = args.Get("source", "test");
        var seconds = args.GetInt("seconds", 12);

        var description = PipelineDescription.Create();
        if (source == "test")
        {
            description.Element("videotestsrc", ("name", "camera"), ("is-live", true), ("pattern", "black"))
                       .Caps("video/x-raw", ("width", 640), ("height", 360), ("framerate", new Fraction(25, 1)));
        }
        else
        {
            description.Element("uridecodebin", ("uri", CliArgs.ToUri(source)));
        }

        description
            .Element("videoconvert")
            .Element("videoscale")
            .Element("videorate", ("drop-only", true))
            .Caps("video/x-raw", ("format", "GRAY8"), ("width", 320), ("height", 180), ("framerate", new Fraction(10, 1)))
            .Element("appsink", ("name", "frames"), ("sync", source == "test"));

        Out.Title("analyze: appsink -> C# motion detection");
        Out.Pipeline(description.ToString());

        await using var host = PipelineHost.Parse(description);
        await host.StartAsync(cancellationToken);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.CancelAfter(TimeSpan.FromSeconds(seconds));

        var scene = source == "test" ? SimulateSceneAsync(host.GetElement<Gst.Element>("camera"), run.Token) : Task.CompletedTask;

        // Live sources: drop frames when behind. Files: analyse every frame (lossless back-pressure).
        var isLive = source == "test" || !CliArgs.ToUri(source).StartsWith("file:", StringComparison.Ordinal);
        var reader = new FrameReader(host.GetElement<GstApp.AppSink>("frames"), dropWhenBehind: isLive);
        var detector = new MotionDetector("demo", new MotionOptions { Cooldown = TimeSpan.FromMilliseconds(500) });
        var clock = Stopwatch.StartNew();
        long frames = 0;

        try
        {
            await foreach (var frame in reader.ReadAllAsync(run.Token))
            {
                using (frame)
                {
                    frames++;
                    switch (detector.Process(frame))
                    {
                        case MotionStarted m: Out.Event("MOTION", $"started  frame #{m.FrameSequence} score {m.Score:P1}", ConsoleColor.Red); break;
                        case MotionEnded m: Out.Event("motion", $"ended    after {m.Length.TotalSeconds:F1}s", ConsoleColor.Green); break;
                    }

                    if (frames % 10 == 0)
                    {
                        var bar = new string('|', (int)Math.Min(40, detector.CurrentScore * 400));
                        Out.Info($"  {frame.Format.Width}x{frame.Format.Height} {frame.Format.PixelFormat} pts={Out.Time(frame.Pts)}  " +
                                 $"{frames / clock.Elapsed.TotalSeconds,5:F1} fps  score {detector.CurrentScore,6:P2} {bar}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }

        await scene;
        Out.Info($"\nanalysed {frames} frames, dropped {reader.Dropped} (consumer too slow), " +
                 $"{frames / clock.Elapsed.TotalSeconds:F1} fps");
        return 0;
    }

    /// <summary>Changes videotestsrc's pattern on the fly: black (idle) ↔ moving ball (activity).</summary>
    private static async Task SimulateSceneAsync(Gst.Element camera, CancellationToken cancellationToken)
    {
        string[] script = ["black", "ball", "ball", "black", "black", "ball", "black"];
        try
        {
            foreach (var pattern in script)
            {
                camera.Set("pattern", pattern);
                Out.Event("scene", pattern == "ball" ? "object moving" : "scene idle", ConsoleColor.DarkCyan);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
