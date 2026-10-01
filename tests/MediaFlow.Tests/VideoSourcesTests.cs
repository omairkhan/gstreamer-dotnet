using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;
using MediaFlow.Core.Recipes;

namespace MediaFlow.Tests;

public class VideoSourcesTests
{
    [Theory]
    [InlineData("webcam", true, null)]
    [InlineData("webcam:0", true, 0)]
    [InlineData("webcam:12", true, 12)]
    [InlineData("webcam:-1", false, null)]
    [InlineData("webcam:abc", false, null)]
    [InlineData("webcam:0;rm", false, null)]
    [InlineData("test", false, null)]
    [InlineData("rtsp://cam/stream", false, null)]
    public void Parses_webcam_sources(string source, bool isWebcam, int? index)
    {
        Assert.Equal(isWebcam, VideoSources.IsWebcam(source));
        Assert.Equal(index, VideoSources.WebcamIndex(source));
    }

    [Fact]
    public void Default_webcam_uses_autovideosrc_and_normalises_output()
    {
        var text = VideoSources.AppendWebcam(PipelineDescription.Create(), "webcam", 1280, 720, 25).ToString();
        Assert.Equal("autovideosrc ! decodebin ! videoconvert ! videoscale ! videorate ! video/x-raw,width=1280,height=720,framerate=25/1", text);
    }

    [Fact]
    public void Numbered_webcam_uses_the_native_capture_api_of_the_os()
    {
        var text = VideoSources.AppendWebcam(PipelineDescription.Create(), "webcam:1", 640, 360, 30).ToString();
        var expected = OperatingSystem.IsWindows() ? "mfvideosrc device-index=1"
            : OperatingSystem.IsMacOS() ? "avfvideosrc device-index=1"
            : "v4l2src device=/dev/video1";
        Assert.StartsWith(expected, text);
    }

    [Fact]
    public void Camera_pipeline_accepts_a_webcam_source()
    {
        var text = CameraPipeline.Build(new CameraOptions { Id = "usb", Source = "webcam" }).ToString();
        Assert.StartsWith("autovideosrc ! decodebin", text);
        Assert.Contains("hlssink2", text);
    }
}
