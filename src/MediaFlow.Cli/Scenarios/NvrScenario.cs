using MediaFlow.Core.Analytics;
using MediaFlow.Core.Media;
using MediaFlow.Core.Recipes;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 3: a supervised, 24/7 camera recorder (a tiny NVR) in one command.
/// One ingest → one decode → one encode → HLS + rotating MP4 archive + snapshots + analytics.
/// Pull the network cable of your IP camera and watch it back off and reconnect.
/// </summary>
internal static class NvrScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var source = args.Get("source", "test");
        var options = new CameraOptions
        {
            Id = args.Get("id", "cam-1"),
            Source = source == "test" || VideoSources.IsWebcam(source) ? source : CliArgs.ToUri(source),
            OutputRoot = args.Get("out", "recordings"),
            SegmentDuration = TimeSpan.FromSeconds(args.GetInt("segment", 10)),
            SnapshotInterval = TimeSpan.FromSeconds(args.GetInt("snapshot", 2)),
        };

        Out.Title($"nvr: supervised camera '{options.Id}'");
        // The synthetic ball covers ~0.2 % of the frame; real scenes (people, cars) are far larger.
        var motion = source == "test" ? new MotionOptions { StartRatio = 0.001, StopRatio = 0.0002 } : null;
        await using var camera = new CameraSession(options, motionOptions: motion);
        Out.Pipeline(camera.Description);
        Out.Info($"HLS      : {Path.GetFullPath(Path.Combine(options.OutputDirectory, "hls", "index.m3u8"))}");
        Out.Info($"Archive  : {Path.GetFullPath(Path.Combine(options.OutputDirectory, "archive"))}");
        Out.Info($"Snapshots: {Path.GetFullPath(Path.Combine(options.OutputDirectory, "snapshots"))}\n");

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (args.GetInt("seconds", 0) is > 0 and var seconds) run.CancelAfter(TimeSpan.FromSeconds(seconds));

        camera.Start();
        var heartbeat = HeartbeatAsync(camera, run.Token);
        try
        {
            await foreach (var e in camera.SubscribeAsync(run.Token))
            {
                var color = e.Kind switch
                {
                    CameraEventKind.MotionStarted or CameraEventKind.Error => ConsoleColor.Red,
                    CameraEventKind.MotionEnded or CameraEventKind.SegmentClosed => ConsoleColor.Green,
                    CameraEventKind.Warning => ConsoleColor.DarkYellow,
                    CameraEventKind.Snapshot => ConsoleColor.DarkGray,
                    _ => ConsoleColor.Cyan,
                };
                Out.Event(e.Kind.ToString(), e.Path is null ? e.Message : $"{e.Message}: {e.Path}", color);
            }
        }
        catch (OperationCanceledException) { }
        await heartbeat;

        Out.Info("\nstopping: draining pipeline so the last MP4 segment is finalised...");
        return 0;
    }

    private static async Task HeartbeatAsync(CameraSession camera, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Out.Event("health", $"{camera.Status.State}, restarts {camera.Status.Restarts}, " +
                                    $"motion score {camera.MotionScore:P2}{(camera.InMotion ? " (MOTION)" : "")}", ConsoleColor.DarkCyan);
            }
        }
        catch (OperationCanceledException) { }
    }
}
