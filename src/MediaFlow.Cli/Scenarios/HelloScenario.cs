using MediaFlow.Core;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 1: the anatomy of a pipeline.
/// source → caps filter → converters/overlays → encoder → parser → muxer → sink,
/// driven through states (NULL → READY → PAUSED → PLAYING) and observed through the bus.
/// </summary>
internal static class HelloScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var seconds = args.GetInt("seconds", 5);
        var fps = 30;
        var display = args.Flag("display");
        var output = args.Get("out", "hello.mp4");

        var description = PipelineDescription.Create()
            .Element("videotestsrc", ("pattern", "ball"), ("num-buffers", seconds * fps), ("is-live", display))
            .Caps("video/x-raw", ("width", 1280), ("height", 720), ("framerate", new Fraction(fps, 1)))
            .Element("textoverlay", ("text", "Hello GStreamer from C# 14 / .NET 10"), ("valignment", "top"), ("font-desc", "Sans 24"))
            .Element("timeoverlay", ("halignment", "right"), ("valignment", "bottom"))
            .Element("videoconvert");

        if (display)
        {
            description.Element("autovideosink");
        }
        else
        {
            GstRuntime.Require("x264enc", "h264parse", "mp4mux");
            description
                .Element("x264enc", ("speed-preset", "veryfast"), ("bitrate", 2000))
                .Element("h264parse")
                .Element("mp4mux")
                .Element("filesink", ("location", output));
        }

        Out.Title("hello: your first pipeline");
        Out.Pipeline(description.ToString());

        await using var host = PipelineHost.Parse(description);
        await host.StartAsync(cancellationToken);
        Out.Event("state", "PLAYING", ConsoleColor.Green);

        using var reg = cancellationToken.Register(() => _ = host.StopAsync());
        var outcome = await host.Completion;
        Out.Event("bus", $"finished: {outcome}", ConsoleColor.Green);
        if (!display) Out.Info($"Wrote {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024} KiB). Try: mediaflow play {output}");
        return 0;
    }
}
