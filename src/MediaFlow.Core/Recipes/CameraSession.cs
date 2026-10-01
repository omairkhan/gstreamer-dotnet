using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MediaFlow.Core.Analytics;
using MediaFlow.Core.Media;
using MediaFlow.Core.Pipelines;
using MediaFlow.Core.Resilience;

namespace MediaFlow.Core.Recipes;

public enum CameraEventKind
{
    Status,
    MotionStarted,
    MotionEnded,
    SegmentClosed,
    Snapshot,
    Warning,
    Error,
}

public sealed record CameraEvent(string CameraId, CameraEventKind Kind, string Message, DateTimeOffset At, string? Path = null, double? Score = null);

/// <summary>
/// A supervised, observable camera: runs <see cref="CameraPipeline"/> under a
/// <see cref="PipelineSupervisor"/>, feeds frames to a <see cref="MotionDetector"/>
/// and fans out typed events to any number of subscribers (SSE clients, loggers, webhooks).
/// </summary>
public sealed class CameraSession : IAsyncDisposable
{
    private readonly PipelineSupervisor _supervisor;
    private readonly ConcurrentDictionary<Guid, Channel<CameraEvent>> _subscribers = new();
    private readonly ConcurrentQueue<CameraEvent> _recent = new();
    private readonly MotionDetector _detector;
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    public CameraSession(CameraOptions options, SupervisorOptions? supervisorOptions = null, MotionOptions? motionOptions = null)
    {
        Options = options;
        _detector = new MotionDetector(options.Id, motionOptions);
        GstRuntime.Require([.. CameraPipeline.RequiredElements(options)]);
        CameraPipeline.PrepareOutput(options);
        Description = CameraPipeline.Build(options).ToString();

        _supervisor = new PipelineSupervisor(
            options.Id,
            () => PipelineHost.Parse(Description),
            supervisorOptions ?? new SupervisorOptions { StopOnEndOfStream = options.Source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) },
            OnPipelineStartedAsync);

        _supervisor.StatusChanged += s => Publish(new CameraEvent(
            options.Id, CameraEventKind.Status,
            s.NextRetryIn is { } retry ? $"{s.State} (retry in {retry.TotalSeconds:F1}s: {s.LastError})" : s.State.ToString(),
            DateTimeOffset.UtcNow));
    }

    public CameraOptions Options { get; }

    public string Description { get; }

    public SupervisorStatus Status => _supervisor.Status;

    public double MotionScore => _detector.CurrentScore;

    public bool InMotion => _detector.InMotion;

    public string? LatestSnapshot { get; private set; }

    public IReadOnlyCollection<CameraEvent> RecentEvents => _recent;

    public void Start() => _run ??= Task.Run(() => _supervisor.RunAsync(_cts.Token));

    /// <summary>Every subscriber gets its own bounded channel so one slow client cannot block others.</summary>
    public async IAsyncEnumerable<CameraEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<CameraEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private async Task OnPipelineStartedAsync(PipelineHost host, CancellationToken cancellationToken)
    {
        var tasks = new List<Task> { PumpBusEventsAsync(host, cancellationToken) };
        if (Options.EnableAnalytics)
        {
            tasks.Add(AnalyseAsync(host.GetElement<GstApp.AppSink>(CameraPipeline.AnalyticsSinkName), cancellationToken));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task AnalyseAsync(GstApp.AppSink sink, CancellationToken cancellationToken)
    {
        var reader = new FrameReader(sink);
        await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            using (frame)
            {
                switch (_detector.Process(frame))
                {
                    case MotionStarted m:
                        Publish(new CameraEvent(Options.Id, CameraEventKind.MotionStarted, $"Motion detected (score {m.Score:P1})", m.At, LatestSnapshot, m.Score));
                        break;
                    case MotionEnded m:
                        Publish(new CameraEvent(Options.Id, CameraEventKind.MotionEnded, $"Motion ended after {m.Length.TotalSeconds:F1}s", m.At, null, m.Score));
                        break;
                }
            }
        }
    }

    private async Task PumpBusEventsAsync(PipelineHost host, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in host.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var mapped = evt switch
                {
                    // hlssink2 uses a splitmuxsink internally too; only the archive recorder reports a file location.
                    ElementMessage { Name: "splitmuxsink-fragment-closed", Location: not null } m =>
                        new CameraEvent(Options.Id, CameraEventKind.SegmentClosed, "Recording segment finalised", m.At, m.Location),
                    ElementMessage { Name: "GstMultiFileSink" } m =>
                        new CameraEvent(Options.Id, CameraEventKind.Snapshot, "Snapshot written", m.At, m.Location),
                    PipelineWarning w => new CameraEvent(Options.Id, CameraEventKind.Warning, w.Message, w.At),
                    PipelineError e => new CameraEvent(Options.Id, CameraEventKind.Error, e.Message, e.At),
                    _ => null,
                };

                if (mapped is null) continue;
                if (mapped.Kind == CameraEventKind.Snapshot) LatestSnapshot = mapped.Path;
                Publish(mapped);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Publish(CameraEvent e)
    {
        _recent.Enqueue(e);
        while (_recent.Count > 100) _recent.TryDequeue(out _);
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(e);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_run is not null) await _run.ConfigureAwait(false);
        foreach (var s in _subscribers.Values) s.Writer.TryComplete();
        _cts.Dispose();
    }
}
