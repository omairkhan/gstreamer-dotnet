using MediaFlow.Core;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 1 → 2: building a pipeline by hand instead of from a string.
/// uridecodebin only knows which streams a file contains after it started reading it,
/// so its source pads appear at runtime ("sometimes pads"). We react to <c>pad-added</c>,
/// inspect the caps, and attach a matching encoder branch while the pipeline is running.
/// </summary>
internal static class TranscodeScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var input = CliArgs.ToUri(args.Arg(0, "input"));
        var output = args.Arg(1, "output.mp4");
        var height = args.GetInt("height", 0);
        GstRuntime.Require("uridecodebin", "x264enc", "h264parse", "mp4mux", "avenc_aac", "aacparse");

        Out.Title("transcode: dynamic pads + manual linking");

        var pipeline = Gst.Pipeline.New("transcode");
        var source = Make("uridecodebin", "source");
        source.Set("uri", input);
        var mux = Make("mp4mux", "mux");
        var sink = Make("filesink", "sink");
        sink.Set("location", output);

        pipeline.Add(source);
        pipeline.Add(mux);
        pipeline.Add(sink);
        if (!mux.Link(sink)) throw new InvalidOperationException("mux -> filesink link failed");

        var scale = height > 0 ? $"videoscale ! video/x-raw,height={height},pixel-aspect-ratio=1/1 ! " : "";
        var branches = new Dictionary<string, string>
        {
            ["video/"] = $"queue ! videoconvert ! {scale}x264enc speed-preset=faster bitrate=2500 ! h264parse",
            ["audio/"] = "queue ! audioconvert ! audioresample ! avenc_aac bitrate=128000 ! aacparse",
        };
        var linked = new HashSet<string>();
        var sync = new Lock();

        // Runs on a GStreamer streaming thread: keep it short and thread-safe.
        source.OnPadAdded += (_, e) =>
        {
            using var caps = e.NewPad.GetCurrentCaps() ?? e.NewPad.QueryCaps(null);
            var media = caps?.GetStructure(0).GetName() ?? "";
            var kind = branches.Keys.FirstOrDefault(k => media.StartsWith(k, StringComparison.Ordinal));
            lock (sync)
            {
                if (kind is null || !linked.Add(kind))
                {
                    Out.Event("pad-added", $"{e.NewPad.GetName()} ({media}) ignored", ConsoleColor.DarkGray);
                    return;
                }
            }

            var branch = Gst.Functions.ParseBinFromDescription(branches[kind], true);
            pipeline.Add(branch);
            branch.Link(mux);                      // requests a mux pad (video_0 / audio_0)
            branch.SyncStateWithParent();          // bring it to PLAYING like the rest
            var result = e.NewPad.Link(branch.GetStaticPad("sink")!);
            Out.Event("pad-added", $"{e.NewPad.GetName()} ({media}) -> {kind} branch: {result}", ConsoleColor.Green);
        };

        await using var host = PipelineHost.Adopt(pipeline);
        await host.StartAsync(cancellationToken);

        if (args.Get("dot", "") is { Length: > 0 } dot)
        {
            await Task.Delay(500, cancellationToken);
            await File.WriteAllTextAsync(dot, host.ToDot(), cancellationToken);
            Out.Info($"pipeline graph written to {dot} (render: dot -Tsvg {dot} -o graph.svg)");
        }

        using var reg = cancellationToken.Register(() => _ = host.StopAsync());
        while (!host.Completion.IsCompleted)
        {
            var (pos, dur) = (host.Position, host.Duration);
            var pct = pos is { } p && dur is { TotalMilliseconds: > 0 } d ? p / d : 0;
            Console.Write($"\r  [{new string('#', (int)(pct * 30)),-30}] {pct,6:P0}  {Out.Time(pos)} / {Out.Time(dur)}");
            await Task.WhenAny(host.Completion, Task.Delay(250, CancellationToken.None));
        }
        Console.WriteLine();

        var outcome = await host.Completion;
        Out.Event("done", $"{outcome}: {Path.GetFullPath(output)} ({new FileInfo(output).Length / 1024} KiB)", ConsoleColor.Green);
        return 0;
    }

    private static Gst.Element Make(string factory, string name) =>
        Gst.ElementFactory.Make(factory, name) ?? throw new MissingGstPluginException([factory]);
}
