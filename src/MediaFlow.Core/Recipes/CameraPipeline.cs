using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Core.Recipes;

public sealed record CameraOptions
{
    /// <summary>Unique id, used for folders, metrics and URLs (e.g. "gate-1").</summary>
    public required string Id { get; init; }

    /// <summary>
    /// "test" for a synthetic camera, "webcam" / "webcam:N" for a local USB camera,
    /// or any URI GStreamer understands: rtsp://, http(s)://, file://, srt://.
    /// </summary>
    public string Source { get; init; } = "test";

    public string OutputRoot { get; init; } = "recordings";

    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Framerate { get; init; } = 25;
    public int BitrateKbps { get; init; } = 2500;

    public bool EnableHls { get; init; } = true;
    public bool EnableRecording { get; init; } = true;
    public bool EnableSnapshots { get; init; } = true;
    public bool EnableAnalytics { get; init; } = true;

    public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Resolution used for analytics; small frames = cheap CPU.</summary>
    public int AnalyticsWidth { get; init; } = 320;
    public int AnalyticsHeight { get; init; } = 180;
    public int AnalyticsFramerate { get; init; } = 10;

    public string OutputDirectory => Path.Combine(OutputRoot, Id);
}

/// <summary>
/// The "real world" pipeline: one ingest, one decode, one encode, many consumers.
/// <code>
///                         ┌─ queue ─ clock/text overlay ─ x264enc ─ tee(enc) ─┬─ h264parse ─ hlssink2      (live browser playback)
/// source ─ videoconvert ─ tee(raw)                                           └─ h264parse ─ splitmuxsink  (rotating MP4 archive)
///                         ├─ queue(leaky) ─ videorate ─ jpegenc ─ multifilesink    (periodic snapshots)
///                         └─ queue(leaky) ─ scale ─ GRAY8 ─ appsink ─► C# MotionDetector
/// </code>
/// Encoding once and fanning out the compressed stream (instead of one encoder per output) is the single biggest CPU saving.
/// </summary>
public static class CameraPipeline
{
    public const string AnalyticsSinkName = "analytics";

    public static IReadOnlyList<string> RequiredElements(CameraOptions options)
    {
        List<string> elements = ["videoconvert", "tee", "queue"];
        if (options.Source == "test") elements.Add("videotestsrc");
        else if (VideoSources.IsWebcam(options.Source)) elements.AddRange(VideoSources.RequiredElements(options.Source));
        else elements.Add("uridecodebin");
        if (options.EnableHls || options.EnableRecording) elements.AddRange(["x264enc", "h264parse", "clockoverlay", "textoverlay"]);
        if (options.EnableHls) elements.Add("hlssink2");
        if (options.EnableRecording) elements.AddRange(["splitmuxsink", "mp4mux"]);
        if (options.EnableSnapshots) elements.AddRange(["videorate", "jpegenc", "multifilesink"]);
        if (options.EnableAnalytics) elements.AddRange(["videoscale", "appsink"]);
        return elements;
    }

    public static PipelineDescription Build(CameraOptions o)
    {
        var dir = o.OutputDirectory;
        var d = PipelineDescription.Create();

        if (o.Source == "test")
        {
            d.Element("videotestsrc", ("is-live", true), ("pattern", "ball"), ("background-color", 0xFF1E2A3Au))
             .Caps("video/x-raw", ("width", o.Width), ("height", o.Height), ("framerate", new Fraction(o.Framerate, 1)));
        }
        else if (VideoSources.IsWebcam(o.Source))
        {
            VideoSources.AppendWebcam(d, o.Source, o.Width, o.Height, o.Framerate);
        }
        else
        {
            // uridecodebin auto-plugs the right demuxer/depayloader/decoder (hardware ones when available).
            d.Element("uridecodebin", ("uri", o.Source));
        }

        d.Element("videoconvert");

        var branches = new List<Action<PipelineDescription>>();

        if (o.EnableHls || o.EnableRecording)
        {
            branches.Add(b =>
            {
                Burn(b.Queue(), o)
                 .Element("videoconvert")
                 .Element("x264enc",
                     ("tune", "zerolatency"), ("speed-preset", "veryfast"),
                     ("bitrate", o.BitrateKbps), ("key-int-max", o.Framerate * 2))
                 .Caps("video/x-h264", ("profile", "main"));

                // One h264parse per output, *after* the tee: HLS (MPEG-TS) wants byte-stream,
                // MP4 wants avc. A single shared parser would have to satisfy both and fails with not-negotiated.

                var outputs = new List<Action<PipelineDescription>>();
                if (o.EnableHls)
                {
                    outputs.Add(hls => hls.Queue().Element("h264parse", ("config-interval", -1)).Element("hlssink2",
                        ("name", "hls"),
                        ("location", Path.Combine(dir, "hls", "segment%05d.ts")),
                        ("playlist-location", Path.Combine(dir, "hls", "index.m3u8")),
                        ("target-duration", 2), ("playlist-length", 6), ("max-files", 12)));
                }
                if (o.EnableRecording)
                {
                    outputs.Add(rec => rec.Queue().Element("h264parse").Element("splitmuxsink",
                        ("name", "recorder"),
                        ("location", Path.Combine(dir, "archive", "segment-%05d.mp4")),
                        ("max-size-time", o.SegmentDuration.ToNanoseconds()),
                        ("send-keyframe-requests", true),
                        ("muxer-factory", "mp4mux")));
                }
                b.Tee("enc", [.. outputs]);
            });
        }

        if (o.EnableSnapshots)
        {
            var fps = new Fraction(1, Math.Max(1, (int)o.SnapshotInterval.TotalSeconds));
            branches.Add(b => Burn(b.Queue(leaky: true, maxBuffers: 2)
                .Element("videorate", ("drop-only", true))
                .Caps("video/x-raw", ("framerate", fps)), o)
                .Element("videoconvert")
                .Element("jpegenc", ("quality", 80))
                .Element("multifilesink",
                    ("name", "snapshots"),
                    ("location", Path.Combine(dir, "snapshots", "snap-%05d.jpg")),
                    ("max-files", 10), ("post-messages", true), ("async", false)));
        }

        if (o.EnableAnalytics)
        {
            // Leaky queue: analytics may fall behind, recording must never do so.
            branches.Add(b => b.Queue(leaky: true, maxBuffers: 1)
                .Element("videorate", ("drop-only", true))
                .Element("videoscale")
                .Element("videoconvert")
                .Caps("video/x-raw",
                    ("format", "GRAY8"), ("width", o.AnalyticsWidth), ("height", o.AnalyticsHeight),
                    ("framerate", new Fraction(o.AnalyticsFramerate, 1)))
                .Element("appsink", ("name", AnalyticsSinkName), ("sync", false), ("async", false)));
        }

        if (branches.Count == 0) throw new ArgumentException("Enable at least one output.", nameof(o));

        return d.Tee("raw", [.. branches]);
    }

    /// <summary>
    /// Burns camera name + wall-clock time into the picture (evidence-grade recordings and stills).
    /// Deliberately NOT applied to the analytics branch: a ticking clock would register as motion.
    /// </summary>
    private static PipelineDescription Burn(PipelineDescription d, CameraOptions o) => d
        .Element("clockoverlay", ("time-format", "%Y-%m-%d %H:%M:%S"), ("halignment", "right"), ("valignment", "bottom"), ("font-desc", "Sans 14"))
        .Element("textoverlay", ("text", o.Id), ("halignment", "left"), ("valignment", "top"), ("font-desc", "Sans 16"));

    /// <summary>Creates the folder layout the sinks write into.</summary>
    public static void PrepareOutput(CameraOptions o)
    {
        foreach (var sub in new[] { "hls", "archive", "snapshots" })
        {
            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, sub));
        }
    }
}
