using MediaFlow.Core.Pipelines;
using MediaFlow.Core.Recipes;

namespace MediaFlow.Tests;

public class PipelineDescriptionTests
{
    [Fact]
    public void Renders_elements_caps_and_typed_values()
    {
        var d = PipelineDescription.Create()
            .Element("videotestsrc", ("pattern", "ball"), ("is-live", true), ("num-buffers", 10))
            .Caps("video/x-raw", ("width", 640), ("framerate", new Fraction(30, 1)))
            .Element("fakesink");

        Assert.Equal("videotestsrc pattern=ball is-live=true num-buffers=10 ! video/x-raw,width=640,framerate=30/1 ! fakesink", d.ToString());
    }

    [Theory]
    [InlineData("C:\\videos\\my clip.mp4", "\"C:\\\\videos\\\\my clip.mp4\"")]
    [InlineData("rtsp://cam/stream?a=1", "\"rtsp://cam/stream?a=1\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Quotes_values_that_would_break_the_parser(string raw, string expected) =>
        Assert.Equal(expected, PipelineDescription.FormatValue(raw));

    [Fact]
    public void Null_properties_are_omitted() =>
        Assert.Equal("queue", PipelineDescription.Create().Element("queue", ("leaky", null)).ToString());

    [Fact]
    public void Tee_renders_named_branches()
    {
        var d = PipelineDescription.Create()
            .Element("videotestsrc")
            .Tee("t", a => a.Queue().Element("fakesink"), b => b.Queue(leaky: true, maxBuffers: 1).Element("appsink"));

        Assert.Equal(
            "videotestsrc ! tee name=t allow-not-linked=true " +
            "t. ! queue ! fakesink " +
            "t. ! queue leaky=downstream max-size-buffers=1 max-size-bytes=0 max-size-time=0 ! appsink",
            d.ToString());
    }

    [Fact]
    public void Camera_pipeline_contains_every_enabled_output()
    {
        var text = CameraPipeline.Build(new CameraOptions { Id = "cam", OutputRoot = "out" }).ToString();

        Assert.Contains("hlssink2", text);
        Assert.Contains("splitmuxsink", text);
        Assert.Contains("multifilesink", text);
        Assert.Contains($"appsink name={CameraPipeline.AnalyticsSinkName}", text);
        Assert.Equal(2, text.Split("h264parse").Length - 1); // one parser per compressed output
    }

    [Fact]
    public void Camera_pipeline_skips_disabled_outputs()
    {
        var text = CameraPipeline.Build(new CameraOptions { Id = "cam", EnableHls = false, EnableRecording = false, EnableSnapshots = false }).ToString();

        Assert.DoesNotContain("x264enc", text);
        Assert.Contains("appsink", text);
    }
}
