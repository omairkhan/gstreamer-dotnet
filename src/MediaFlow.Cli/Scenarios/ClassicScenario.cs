namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 0: the program from the 2024 article "Bridging the Gap: GStreamer Integration for
/// .NET Core 8 on Windows", ported 1:1 to .NET 10 + GirCore bindings. Compare:
/// <code>
/// // 2024 (.NET 8, gstreamer-sharp-netcore)      // 2026 (.NET 10, GirCore)
/// Application.Init(ref args);                     Gst.Module.Initialize(); Gst.Functions.Init(ref args);
/// Parse.Launch("playbin uri=...");                Gst.Functions.ParseLaunch("playbin uri=...");
/// pipeline.Bus                                    pipeline.GetBus()
/// </code>
/// Everything else in this repository builds on these same few calls.
/// </summary>
internal static class ClassicScenario
{
    public const string SintelTrailer = "http://download.blender.org/durian/trailer/sintel_trailer-1080p.mp4";

    public static int Run(CliArgs args)
    {
        var uri = args.Positional.Count > 0 ? CliArgs.ToUri(args.Positional[0]) : SintelTrailer;
        Out.Title("classic: the 2024 article, on .NET 10");

        // Initialize GStreamer (MediaFlow's GstRuntime does exactly this, once, thread-safely)
        Gst.Module.Initialize();
        var gstArgs = Array.Empty<string>();
        Gst.Functions.Init(ref gstArgs);

        // Build the pipeline
        using var pipeline = Gst.Functions.ParseLaunch($"playbin uri={uri}");

        // Start playing
        pipeline.SetState(Gst.State.Playing);
        Out.Info($"Playing {uri} ... (Ctrl+C to quit)");

        // Wait until error or EOS
        using var bus = pipeline.GetBus()!;
        using var msg = bus.TimedPopFiltered(new Gst.ClockTime(Gst.Constants.CLOCK_TIME_NONE), Gst.MessageType.Eos | Gst.MessageType.Error);
        Out.Info($"Bus says: {msg?.Type}");

        // Free resources
        pipeline.SetState(Gst.State.Null);
        return msg?.Type == Gst.MessageType.Error ? 1 : 0;
    }
}
