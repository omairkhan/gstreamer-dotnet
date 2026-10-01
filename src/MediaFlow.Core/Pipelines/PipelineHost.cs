using System.Diagnostics;
using System.Threading.Channels;
using MediaFlow.Core.Diagnostics;

namespace MediaFlow.Core.Pipelines;

/// <summary>
/// Owns a GStreamer pipeline and turns its imperative, callback-driven lifecycle into an
/// idiomatic async .NET API:
/// <list type="bullet">
///   <item>a dedicated bus-pump thread projects bus messages into a <see cref="Channel{T}"/> of typed events,</item>
///   <item>state changes are awaitable and fail with the real error from the bus,</item>
///   <item><see cref="StopAsync"/> performs a graceful EOS drain so MP4/MKV files are finalised,</item>
///   <item>position, duration, seeking, trick-play and Graphviz dumps are one call away.</item>
/// </list>
/// </summary>
public sealed class PipelineHost : IAsyncDisposable
{
    private const Gst.MessageType WatchedMessages =
        Gst.MessageType.Eos | Gst.MessageType.Error | Gst.MessageType.Warning |
        Gst.MessageType.Buffering | Gst.MessageType.Element | Gst.MessageType.Application;

    private readonly Gst.Pipeline _pipeline;
    private readonly Gst.Bus _bus;
    private readonly Channel<PipelineEvent> _events = Channel.CreateBounded<PipelineEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly TaskCompletionSource<PipelineOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Thread _busPump;
    private readonly Activity? _activity;
    private PipelineError? _lastError;
    private int _disposed;

    private PipelineHost(Gst.Pipeline pipeline, string description)
    {
        _pipeline = pipeline;
        _bus = pipeline.GetBus() ?? throw new InvalidOperationException("Pipeline has no bus.");
        Description = description;
        _activity = MediaFlowTelemetry.Source.StartActivity("pipeline");
        _activity?.SetTag("gst.pipeline", description);
        _busPump = new Thread(PumpBus) { IsBackground = true, Name = $"gst-bus:{pipeline.GetName()}" };
        _busPump.Start();
    }

    /// <summary>Parses a gst-launch description. Syntax errors surface as <see cref="GLib.GException"/>.</summary>
    public static PipelineHost Parse(string description)
    {
        GstRuntime.EnsureInitialized();
        var element = Gst.Functions.ParseLaunch(description);
        var pipeline = element as Gst.Pipeline
            ?? throw new InvalidOperationException("Description did not produce a pipeline.");
        return new PipelineHost(pipeline, description);
    }

    public static PipelineHost Parse(PipelineDescription description) => Parse(description.ToString());

    /// <summary>Wraps a pipeline you assembled manually with ElementFactory.Make + Link.</summary>
    public static PipelineHost Adopt(Gst.Pipeline pipeline) => new(pipeline, $"<manual:{pipeline.GetName()}>");

    public string Description { get; }

    public Gst.Pipeline Pipeline => _pipeline;

    /// <summary>Typed bus events (errors, EOS, buffering, element messages).</summary>
    public ChannelReader<PipelineEvent> Events => _events.Reader;

    /// <summary>Completes on EOS/stop, faults with <see cref="PipelineFailedException"/> on error.</summary>
    public Task<PipelineOutcome> Completion => _completion.Task;

    public PipelineError? LastError => _lastError;

    public T GetElement<T>(string name) where T : Gst.Element =>
        _pipeline.GetByName(name) as T
        ?? throw new KeyNotFoundException($"Element '{name}' of type {typeof(T).Name} not found in pipeline.");

    public Task StartAsync(CancellationToken cancellationToken = default) => StartAsync(true, cancellationToken);

    /// <param name="waitForPreroll">
    /// Pass false when your own code feeds the pipeline (appsrc): preroll needs data,
    /// so waiting for it before pushing the first frame would only time out.
    /// </param>
    public Task StartAsync(bool waitForPreroll, CancellationToken cancellationToken = default) =>
        SetStateAsync(Gst.State.Playing, waitForPreroll ? TimeSpan.FromSeconds(15) : TimeSpan.Zero, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        SetStateAsync(Gst.State.Paused, TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>
    /// Requests a state change and waits for it. Live sources return NO_PREROLL (success);
    /// ASYNC transitions are awaited off the calling thread; FAILURE throws with the bus error.
    /// </summary>
    public async Task SetStateAsync(Gst.State target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var result = _pipeline.SetState(target);
        if (result == Gst.StateChangeReturn.Async && timeout > TimeSpan.Zero)
        {
            result = await Task.Run(() => _pipeline.GetState(out _, out _, timeout.ToClockTime()), cancellationToken)
                .ConfigureAwait(false);
        }

        if (result == Gst.StateChangeReturn.Failure)
        {
            // Give the bus pump a moment to deliver the ERROR message that explains the failure.
            await Task.WhenAny(_completion.Task, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
            throw new PipelineFailedException(
                _lastError is { } e ? $"Could not change state to {target}: {e.Message}" : $"Could not change state to {target}.", _lastError);
        }

        _activity?.AddEvent(new ActivityEvent($"state:{target}"));
    }

    /// <summary>
    /// Graceful shutdown: injects EOS so muxers write their headers/indices, waits for it to reach
    /// every sink, then tears the pipeline down. Falls back to a hard stop after <paramref name="drainTimeout"/>.
    /// </summary>
    public async Task StopAsync(TimeSpan? drainTimeout = null, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) return;

        if (!_completion.Task.IsCompleted && _pipeline.SendEvent(Gst.Event.NewEos()))
        {
            var drained = await Task.WhenAny(_completion.Task, Task.Delay(drainTimeout ?? TimeSpan.FromSeconds(5), cancellationToken))
                .ConfigureAwait(false);
            if (drained != _completion.Task) _activity?.AddEvent(new ActivityEvent("eos-drain-timeout"));
        }

        _pipeline.SetState(Gst.State.Null);
        _completion.TrySetResult(PipelineOutcome.Stopped);
    }

    public TimeSpan? Position =>
        _pipeline.QueryPosition(Gst.Format.Time, out var ns) && ns >= 0 ? ns.NanosecondsToTimeSpan() : null;

    public TimeSpan? Duration =>
        _pipeline.QueryDuration(Gst.Format.Time, out var ns) && ns >= 0 ? ns.NanosecondsToTimeSpan() : null;

    /// <summary>Flushing, frame-accurate seek (use SeekFlags.KeyUnit for faster, keyframe-snapped scrubbing). Waits (bounded) until the pipeline has prerolled at the new position.</summary>
    public bool Seek(TimeSpan position)
    {
        var ok = _pipeline.SeekSimple(Gst.Format.Time, Gst.SeekFlags.Flush | Gst.SeekFlags.Accurate, position.ToNanoseconds());
        if (ok) _pipeline.GetState(out _, out _, TimeSpan.FromSeconds(5).ToClockTime());
        return ok;
    }

    /// <summary>Trick-play: fast forward (rate &gt; 1), slow motion (0 &lt; rate &lt; 1) or reverse (rate &lt; 0).</summary>
    public bool SetRate(double rate)
    {
        var position = Position ?? TimeSpan.Zero;
        return rate >= 0
            ? _pipeline.Seek(rate, Gst.Format.Time, Gst.SeekFlags.Flush | Gst.SeekFlags.Accurate,
                Gst.SeekType.Set, position.ToNanoseconds(), Gst.SeekType.None, -1)
            : _pipeline.Seek(rate, Gst.Format.Time, Gst.SeekFlags.Flush | Gst.SeekFlags.Accurate,
                Gst.SeekType.Set, 0, Gst.SeekType.Set, position.ToNanoseconds());
    }

    /// <summary>Graphviz DOT of the live pipeline graph (render with: dot -Tsvg).</summary>
    public string ToDot() => Gst.Functions.DebugBinToDotData(_pipeline, Gst.DebugGraphDetails.All);

    private void PumpBus()
    {
        var pollInterval = TimeSpan.FromMilliseconds(100).ToClockTime();
        var token = _pumpCts.Token;
        while (!token.IsCancellationRequested)
        {
            using var message = _bus.TimedPopFiltered(pollInterval, WatchedMessages);
            if (message is null) continue;

            var evt = Translate(message);
            if (evt is null) continue;

            _events.Writer.TryWrite(evt);

            switch (evt)
            {
                case EndOfStream:
                    _completion.TrySetResult(PipelineOutcome.EndOfStream);
                    break;
                case PipelineError error:
                    // Keep the first error: it names the root cause, later ones are usually fallout.
                    _lastError ??= error;
                    MediaFlowTelemetry.PipelineErrors.Add(1);
                    _activity?.SetStatus(ActivityStatusCode.Error, error.Message);
                    _completion.TrySetException(new PipelineFailedException(error.Message, error));
                    break;
            }
        }
    }

    internal static PipelineEvent? Translate(Gst.Message message)
    {
        var structure = message.GetStructure();
        switch (message.Type)
        {
            case Gst.MessageType.Eos:
                return new EndOfStream(PipelineEvent.Now);

            case Gst.MessageType.Error:
            case Gst.MessageType.Warning:
            {
                var debug = structure?.TryGetString("debug");
                var text = Summarise(debug) ?? message.Type.ToString();
                return message.Type == Gst.MessageType.Error
                    ? new PipelineError(PipelineEvent.Now, text, debug)
                    : new PipelineWarning(PipelineEvent.Now, text, debug);
            }

            case Gst.MessageType.Buffering:
                message.ParseBuffering(out var percent);
                return new Buffering(PipelineEvent.Now, percent);

            case Gst.MessageType.Element when structure is not null:
                return new ElementMessage(PipelineEvent.Now, structure.GetName(), structure.ToString(),
                    structure.TryGetString("location") ?? structure.TryGetString("filename"));

            case Gst.MessageType.Application when structure is not null:
                return new ApplicationMessage(PipelineEvent.Now, structure.GetName(), structure.ToString());

            default:
                return null;
        }
    }

    /// <summary>GStreamer debug strings end with the human readable part ("...:\nNo such file").</summary>
    private static string? Summarise(string? debug)
    {
        if (string.IsNullOrWhiteSpace(debug)) return null;
        var lines = debug.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 1 ? $"{lines[^1]} ({ElementPath(lines[0])})" : lines[0];
    }

    private static string ElementPath(string firstLine)
    {
        var slash = firstLine.IndexOf(" /", StringComparison.Ordinal);
        return slash >= 0 ? firstLine[(slash + 1)..].TrimEnd(':') : firstLine;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _pipeline.SetState(Gst.State.Null);
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        await Task.Run(() => _busPump.Join(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _completion.TrySetResult(PipelineOutcome.Stopped);
        _events.Writer.TryComplete();
        _bus.Dispose();
        _pipeline.Dispose();
        _pumpCts.Dispose();
        _activity?.Dispose();
    }
}

public sealed class PipelineFailedException(string message, PipelineError? error = null) : Exception(message)
{
    public PipelineError? Error { get; } = error;
}
