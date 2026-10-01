namespace MediaFlow.Core.Pipelines;

/// <summary>Strongly-typed projection of GStreamer bus messages.</summary>
public abstract record PipelineEvent(DateTimeOffset At)
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow;
}

public sealed record EndOfStream(DateTimeOffset At) : PipelineEvent(At);

public sealed record PipelineError(DateTimeOffset At, string Message, string? Debug) : PipelineEvent(At);

public sealed record PipelineWarning(DateTimeOffset At, string Message, string? Debug) : PipelineEvent(At);

/// <summary>Network sources (HTTP/HLS/RTSP) report buffering progress; pause at &lt; 100 %.</summary>
public sealed record Buffering(DateTimeOffset At, int Percent) : PipelineEvent(At);

/// <summary>
/// Element-specific messages, e.g. "splitmuxsink-fragment-closed" (a recording segment was finalised)
/// or "GstMultiFileSink" (a snapshot was written). <see cref="Fields"/> is the serialised structure.
/// </summary>
public sealed record ElementMessage(DateTimeOffset At, string Name, string Fields, string? Location) : PipelineEvent(At);

/// <summary>Messages posted by our own code (application messages) on the bus.</summary>
public sealed record ApplicationMessage(DateTimeOffset At, string Name, string Fields) : PipelineEvent(At);

/// <summary>How a pipeline run finished.</summary>
public enum PipelineOutcome
{
    EndOfStream,
    Stopped,
    Failed,
}
