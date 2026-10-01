using System.Globalization;

namespace MediaFlow.Cli;

/// <summary>Tiny dependency-free argument parser: positional args + --key value / --flag.</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private init; }

    public IReadOnlyList<string> Positional { get; private init; } = [];

    public static CliArgs Parse(string[] args)
    {
        var positional = new List<string>();
        var result = new CliArgs { Command = args.FirstOrDefault(), Positional = positional };
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                result._options[key] = hasValue ? args[++i] : null;
            }
            else
            {
                positional.Add(args[i]);
            }
        }
        return result;
    }

    public bool Flag(string name) => _options.ContainsKey(name);

    public string Get(string name, string fallback) => _options.TryGetValue(name, out var v) && v is not null ? v : fallback;

    public int GetInt(string name, int fallback) =>
        _options.TryGetValue(name, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public double GetDouble(string name, double fallback) =>
        _options.TryGetValue(name, out var v) && double.TryParse(v, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    public string Arg(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new ArgumentException($"Missing argument <{name}>.");

    /// <summary>Accepts plain paths as well as URIs.</summary>
    public static string ToUri(string pathOrUri) =>
        pathOrUri.Contains("://", StringComparison.Ordinal) ? pathOrUri : new Uri(Path.GetFullPath(pathOrUri)).AbsoluteUri;

    public const string Help = """
        mediaflow: GStreamer from basics to production with .NET 10 / C# 14

        LEVEL 0: WHERE WE STARTED (2024 article, now on .NET 10)
          classic   [uri]                      playbin + bus.TimedPopFiltered: plays the Sintel trailer

        LEVEL 1: FUNDAMENTALS
          doctor                               GStreamer version + which plugins are installed
          hello     [--display] [--seconds 5] [--out hello.mp4]
                                               source -> caps -> overlay -> encoder -> muxer -> sink
          play      <file|uri> [--seek 10] [--rate 2] [--headless]
                                               playbin: auto-plugging, position/duration, seek, trick-play
          transcode <input> <output.mp4> [--height 720] [--dot graph.dot]
                                               hand-built pipeline, dynamic pads (pad-added), progress

        LEVEL 2: C# IN THE MEDIA PATH
          analyze   [--source test|webcam|webcam:N|uri] [--seconds 10]
                                               appsink -> IAsyncEnumerable<VideoFrame> -> SIMD motion detection
          synth     [--out synth.mp4] [--seconds 5]
                                               appsrc: frames rendered in C# -> H.264/MP4 with correct timestamps

        LEVEL 3: PRODUCTION
          nvr       [--source test|webcam|webcam:N|rtsp://...] [--id cam-1] [--out recordings] [--seconds 0]
                                               supervised 24/7 camera: HLS live + rotating MP4 archive
                                               + JPEG snapshots + motion events, auto-reconnect with backoff

        Sources: test = synthetic camera, webcam = default USB camera, webcam:N = camera N
                 (list them with: gst-device-monitor-1.0 Video/Source), or any URI.

        Web API + dashboard: dotnet run --project src/MediaFlow.Server
        """;
}
