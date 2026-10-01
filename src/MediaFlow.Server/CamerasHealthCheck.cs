using MediaFlow.Core.Resilience;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaFlow.Server;

/// <summary>Healthy when every camera is streaming; Degraded while any is reconnecting; Unhealthy if one gave up.</summary>
public sealed class CamerasHealthCheck(CameraRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var states = registry.All.ToDictionary(c => c.Options.Id, c => (object)c.Status.State.ToString());
        var cameras = registry.All.ToList();

        var result = cameras switch
        {
            _ when cameras.Any(c => c.Status.State == SupervisorState.Faulted) => HealthCheckResult.Unhealthy("camera faulted", data: states),
            _ when cameras.Any(c => c.Status.State != SupervisorState.Running) => HealthCheckResult.Degraded("camera(s) reconnecting", data: states),
            _ => HealthCheckResult.Healthy($"{cameras.Count} camera(s) streaming", states),
        };
        return Task.FromResult(result);
    }
}
