using MediaFlow.Core;

namespace MediaFlow.Cli.Scenarios;

/// <summary>Level 1: is the native runtime there and which plugins can we use?</summary>
internal static class DoctorScenario
{
    private static readonly (string Group, string[] Elements)[] Checks =
    [
        ("core", ["fakesink", "queue", "tee", "capsfilter", "identity"]),
        ("base", ["videotestsrc", "audiotestsrc", "videoconvert", "videoscale", "videorate", "playbin", "uridecodebin", "appsrc", "appsink"]),
        ("good", ["mp4mux", "qtdemux", "splitmuxsink", "jpegenc", "multifilesink", "rtspsrc", "rtph264depay", "level"]),
        ("bad", ["h264parse", "hlssink2", "webrtcbin", "srtsink"]),
        ("ugly", ["x264enc"]),
        ("libav", ["avdec_h264", "avenc_aac"]),
        ("pango", ["clockoverlay", "textoverlay"]), // Ubuntu/Debian: gstreamer1.0-x
        ("display", ["autovideosink", "autoaudiosink"]),
    ];

    public static int Run()
    {
        Out.Title("MediaFlow doctor");
        Out.Info($"Runtime : {GstRuntime.Version}");
        Out.Info($".NET    : {Environment.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n");

        var missing = 0;
        foreach (var (group, elements) in Checks)
        {
            Console.Write($"  {group,-8}");
            foreach (var element in elements)
            {
                var ok = GstRuntime.HasElement(element);
                if (!ok) missing++;
                Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write($"{(ok ? "✓" : "✗")} {element}  ");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Out.Info(missing == 0
            ? "\nAll good: every scenario can run."
            : $"\n{missing} element(s) missing. Install the corresponding gstreamer1.0-plugins-* packages.");
        return 0;
    }
}
