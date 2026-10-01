using MediaFlow.Core.Pipelines;
using MediaFlow.Core.Resilience;

namespace MediaFlow.Tests;

public class SupervisorTests
{
    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var options = new SupervisorOptions { InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(10) };

        Assert.Equal(TimeSpan.FromSeconds(1), PipelineSupervisor.ComputeBackoff(0, options, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(4), PipelineSupervisor.ComputeBackoff(2, options, jitter: 1));
        Assert.Equal(TimeSpan.FromSeconds(10), PipelineSupervisor.ComputeBackoff(10, options, jitter: 1));
        Assert.InRange(PipelineSupervisor.ComputeBackoff(1, options).TotalSeconds, 1.6, 2.4);
    }

    [GStreamerFact]
    public async Task Restarts_a_failing_pipeline_then_gives_up()
    {
        var attempts = 0;
        var supervisor = new PipelineSupervisor(
            "broken",
            () =>
            {
                attempts++;
                return PipelineHost.Parse("filesrc location=/definitely/not/here.mp4 ! fakesink");
            },
            new SupervisorOptions { InitialBackoff = TimeSpan.FromMilliseconds(20), MaxConsecutiveFailures = 3 });

        await supervisor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        Assert.Equal(SupervisorState.Faulted, supervisor.Status.State);
        Assert.Contains("No such file", supervisor.Status.LastError);
    }

    [GStreamerFact]
    public async Task Finite_source_completes_without_restart_when_eos_means_done()
    {
        var attempts = 0;
        var supervisor = new PipelineSupervisor(
            "file",
            () => { attempts++; return PipelineHost.Parse("videotestsrc num-buffers=5 ! fakesink"); },
            new SupervisorOptions { StopOnEndOfStream = true });

        await supervisor.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, attempts);
        Assert.Equal(SupervisorState.Stopped, supervisor.Status.State);
    }
}
