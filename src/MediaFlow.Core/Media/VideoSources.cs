using System.Globalization;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Core.Media;

/// <summary>
/// Local capture devices (USB webcams, built-in cameras, capture cards).
/// <list type="bullet">
///   <item><c>webcam</c>: the system default camera (<c>autovideosrc</c>).</item>
///   <item><c>webcam:N</c>: camera number N, using the native API of the OS:
///         Media Foundation on Windows, AVFoundation on macOS, V4L2 (<c>/dev/videoN</c>) on Linux.</item>
/// </list>
/// List the cameras on a machine with <c>gst-device-monitor-1.0 Video/Source</c>.
/// </summary>
public static class VideoSources
{
    public const string Webcam = "webcam";

    public static bool IsWebcam(string source) =>
        source == Webcam || (source.StartsWith(Webcam + ":", StringComparison.Ordinal) && WebcamIndex(source) is not null);

    public static int? WebcamIndex(string source) =>
        source.StartsWith(Webcam + ":", StringComparison.Ordinal)
        && int.TryParse(source.AsSpan(Webcam.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index is >= 0 and < 64
            ? index
            : null;

    /// <summary>The capture element used for this source on the current OS.</summary>
    public static string CaptureElement(string source) => WebcamIndex(source) is null
        ? "autovideosrc"
        : OperatingSystem.IsWindows() ? "mfvideosrc"
        : OperatingSystem.IsMacOS() ? "avfvideosrc"
        : "v4l2src";

    /// <summary>
    /// Appends the camera and normalises its output to raw video of the requested size and rate.
    /// Many USB cameras only reach 720p/1080p in MJPEG, so <c>decodebin</c> sits behind the source:
    /// it passes raw video straight through and decodes JPEG when the camera sends it.
    /// </summary>
    public static PipelineDescription AppendWebcam(PipelineDescription d, string source, int width, int height, int framerate)
    {
        var index = WebcamIndex(source);
        var element = CaptureElement(source);
        _ = element switch
        {
            "mfvideosrc" or "avfvideosrc" => d.Element(element, ("device-index", index)),
            "v4l2src" => d.Element(element, ("device", $"/dev/video{index}")),
            _ => d.Element(element),
        };

        return d.Element("decodebin")
                .Element("videoconvert")
                .Element("videoscale")
                .Element("videorate")
                .Caps("video/x-raw", ("width", width), ("height", height), ("framerate", new Fraction(framerate, 1)));
    }

    public static string[] RequiredElements(string source) =>
        [CaptureElement(source), "decodebin", "videoconvert", "videoscale", "videorate"];
}
