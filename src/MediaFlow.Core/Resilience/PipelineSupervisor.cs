using MediaFlow.Core.Diagnostics;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Core.Resilience;

public sealed record SupervisorOptions
{
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A run that lasted this long is considered healthy and resets the backoff.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>For files EOS means "done"; for cameras it means "the connection dropped".</summary>
    public bool StopOnEndOfStream { get; init; }

    /// <summary>Give up after this many consecutive failures (null = never, typical for 24/7 cameras).</summary>
    public int? MaxConsecutiveFailures { get; init; }
}

public enum SupervisorState
{
    Starting,
    Running,
    BackingOff,
    Stopped,
    Faulted,
}

public sealed record SupervisorStatus(string Name, SupervisorState State, int Restarts, string? LastError, TimeSpan? NextRetryIn);

/// <summary>
/// Keeps a pipeline alive 24/7: IP cameras reboot, Wi-Fi drops, RTSP servers time out.
/// The supervisor rebuilds the pipeline on error or unexpected EOS using exponential
/// backoff with jitter (so 200 cameras do not reconnect in lock-step), resets the backoff
/// after a stable run and reports its state for health checks and dashboards.
/// </summary>
public sealed class PipelineSupervisor(
    string name,
    Func<PipelineHost> factory,
    SupervisorOptions? options = null,
    Func<PipelineHost, CancellationToken, Task>? onStarted = null,
    TimeProvider? timeProvider = null)
{
    private readonly SupervisorOptions _options = options ?? new SupervisorOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Name => name;

    public SupervisorStatus Status { get; private set; } = new(name, SupervisorState.Starting, 0, null, null);

    public event Action<SupervisorStatus>? StatusChanged;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var restarts = 0;
        string? lastError = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = _time.GetUtcNow();
            Publish(SupervisorState.Starting, restarts, lastError, null);

            var (finished, error) = await RunOnceAsync(restarts, cancellationToken).ConfigureAwait(false);
            if (finished || cancellationToken.IsCancellationRequested) break;

            lastError = error;
            consecutiveFailures = _time.GetUtcNow() - startedAt >= _options.StableAfter ? 1 : consecutiveFailures + 1;

            if (_options.MaxConsecutiveFailures is { } max && consecutiveFailures >= max)
            {
                Publish(SupervisorState.Faulted, restarts, lastError, null);
                return;
            }

            var delay = ComputeBackoff(consecutiveFailures - 1, _options);
            Publish(SupervisorState.BackingOff, restarts, lastError, delay);

            try
            {
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            restarts++;
            MediaFlowTelemetry.PipelineRestarts.Add(1, new KeyValuePair<string, object?>("pipeline", name));
        }

        Publish(SupervisorState.Stopped, restarts, lastError, null);
    }

    /// <returns>finished = true when the pipeline completed normally and should not be restarted.</returns>
    private async Task<(bool Finished, string? Error)> RunOnceAsync(int restarts, CancellationToken cancellationToken)
    {
        PipelineHost? host = null;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            host = factory();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            Publish(SupervisorState.Running, restarts, null, null);

            var work = onStarted?.Invoke(host, runCts.Token) ?? Task.CompletedTask;
            try
            {
                var outcome = await host.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (outcome == PipelineOutcome.EndOfStream && _options.StopOnEndOfStream) return (true, null);
                return (false, "unexpected end-of-stream (source disconnected?)");
            }
            finally
            {
                await runCts.CancelAsync().ConfigureAwait(false);
                try { await work.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (host is not null)
            {
                // Graceful EOS drain on shutdown so recordings are playable.
                if (cancellationToken.IsCancellationRequested) await host.StopAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static TimeSpan ComputeBackoff(int attempt, SupervisorOptions options, double? jitter = null)
    {
        var exponential = options.InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt, 0, 20));
        var capped = Math.Min(exponential, options.MaxBackoff.TotalMilliseconds);
        var factor = jitter ?? (0.8 + (Random.Shared.NextDouble() * 0.4));
        return TimeSpan.FromMilliseconds(capped * factor);
    }

    private void Publish(SupervisorState state, int restarts, string? error, TimeSpan? next)
    {
        Status = new SupervisorStatus(name, state, restarts, error, next);
        StatusChanged?.Invoke(Status);
    }
}
