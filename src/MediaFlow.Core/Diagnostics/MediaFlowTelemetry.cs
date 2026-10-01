using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MediaFlow.Core.Diagnostics;

/// <summary>
/// OpenTelemetry-compatible instrumentation (System.Diagnostics). Plug in any exporter
/// (Prometheus, OTLP, Azure Monitor) with <c>AddMeter("MediaFlow")</c> / <c>AddSource("MediaFlow")</c>.
/// </summary>
public static class MediaFlowTelemetry
{
    public const string Name = "MediaFlow";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> FramesProcessed = Meter.CreateCounter<long>("mediaflow.frames.processed", "{frame}");
    public static readonly Counter<long> FramesDropped = Meter.CreateCounter<long>("mediaflow.frames.dropped", "{frame}");
    public static readonly Counter<long> PipelineErrors = Meter.CreateCounter<long>("mediaflow.pipeline.errors", "{error}");
    public static readonly Counter<long> PipelineRestarts = Meter.CreateCounter<long>("mediaflow.pipeline.restarts", "{restart}");
    public static readonly Counter<long> MotionEvents = Meter.CreateCounter<long>("mediaflow.motion.events", "{event}");
    public static readonly Histogram<double> FrameAnalysisMs = Meter.CreateHistogram<double>("mediaflow.frame.analysis.duration", "ms");
}
