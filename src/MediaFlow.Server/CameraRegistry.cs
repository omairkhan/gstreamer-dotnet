using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaFlow.Core.Recipes;

namespace MediaFlow.Server;

public sealed record CameraRequest(string Id, string Source);

public sealed record CameraSummary(
    string Id, string Source, string State, int Restarts, string? LastError,
    double MotionScore, bool InMotion, string HlsUrl, string SnapshotUrl, string EventsUrl);

public sealed class MediaFlowSettings
{
    public string RecordingsPath { get; set; } = "recordings";
    public int MaxCameras { get; set; } = 16;
    public List<CameraRequest> Cameras { get; set; } = [];
}

/// <summary>
/// Owns all camera sessions for the lifetime of the web host. As an <see cref="IHostedService"/>
/// it starts the configured cameras on boot and drains every pipeline (EOS) on shutdown,
/// so SIGTERM from Kubernetes/Docker never leaves a corrupt MP4 behind.
/// </summary>
public sealed partial class CameraRegistry(MediaFlowSettings settings, ILogger<CameraRegistry> logger) : IHostedService
{
    private static readonly string[] AllowedSchemes = ["rtsp", "rtsps", "http", "https", "srt", "udp"];
    private readonly ConcurrentDictionary<string, CameraSession> _cameras = new(StringComparer.OrdinalIgnoreCase);

    public string RecordingsPath => Path.GetFullPath(settings.RecordingsPath);

    public IEnumerable<CameraSession> All => _cameras.Values.OrderBy(c => c.Options.Id, StringComparer.Ordinal);

    public CameraSession? Find(string id) => _cameras.GetValueOrDefault(id);

    /// <summary>Validates untrusted input before it ever reaches a pipeline string or the file system.</summary>
    public static string? Validate(CameraRequest request)
    {
        if (!IdPattern().IsMatch(request.Id)) return "id must match ^[a-z0-9][a-z0-9-]{0,31}$";
        if (request.Source == "test") return null;
        if (!Uri.TryCreate(request.Source, UriKind.Absolute, out var uri) || !AllowedSchemes.Contains(uri.Scheme))
        {
            return $"source must be 'test' or an absolute URI with scheme: {string.Join(", ", AllowedSchemes)}";
        }
        return null;
    }

    public CameraSession Add(CameraRequest request)
    {
        if (_cameras.ContainsKey(request.Id)) throw new InvalidOperationException($"Camera '{request.Id}' already exists.");
        if (_cameras.Count >= settings.MaxCameras) throw new InvalidOperationException($"Camera limit ({settings.MaxCameras}) reached.");

        var session = new CameraSession(new CameraOptions
        {
            Id = request.Id,
            Source = request.Source,
            OutputRoot = RecordingsPath,
        });

        if (!_cameras.TryAdd(request.Id, session))
        {
            _ = session.DisposeAsync().AsTask(); // never started: nothing to drain
            throw new InvalidOperationException($"Camera '{request.Id}' already exists.");
        }

        session.Start();
        logger.LogInformation("Camera {Id} started from {Source}", request.Id, request.Source);
        return session;
    }

    public async Task<bool> RemoveAsync(string id)
    {
        if (!_cameras.TryRemove(id, out var session)) return false;
        await session.DisposeAsync();
        logger.LogInformation("Camera {Id} stopped", id);
        return true;
    }

    public static CameraSummary Summarise(CameraSession c) => new(
        c.Options.Id, c.Options.Source, c.Status.State.ToString(), c.Status.Restarts, c.Status.LastError,
        Math.Round(c.MotionScore, 5), c.InMotion,
        $"/hls/{c.Options.Id}/hls/index.m3u8", $"/api/cameras/{c.Options.Id}/snapshot", $"/api/cameras/{c.Options.Id}/events");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var camera in settings.Cameras)
        {
            if (Validate(camera) is { } error)
            {
                logger.LogWarning("Skipping configured camera {Id}: {Error}", camera.Id, error);
                continue;
            }
            Add(camera);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Draining {Count} camera pipeline(s)...", _cameras.Count);
        await Task.WhenAll(_cameras.Keys.ToList().Select(RemoveAsync));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();
}
