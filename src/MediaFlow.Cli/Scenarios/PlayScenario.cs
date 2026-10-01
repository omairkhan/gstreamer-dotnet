using MediaFlow.Core;
using MediaFlow.Core.Pipelines;

namespace MediaFlow.Cli.Scenarios;

/// <summary>
/// Level 1: playbin, the "everything" element. It auto-plugs demuxers/decoders for any
/// container, codec or protocol. We add the controls a real player needs:
/// buffering, position/duration queries, seeking and trick-play (rate changes).
/// </summary>
internal static class PlayScenario
{
    public static async Task<int> RunAsync(CliArgs args, CancellationToken cancellationToken)
    {
        var uri = CliArgs.ToUri(args.Arg(0, "file|uri"));
        var headless = args.Flag("headless");

        var description = PipelineDescription.Create().Element("playbin",
            ("uri", uri),
            // Object-valued properties accept a bin description: swap sinks without touching code paths.
            ("video-sink", headless ? "fakesink sync=true" : null),
            ("audio-sink", headless ? "fakesink sync=true" : null));

        Out.Title("play: playbin + queries + seeking");
        Out.Pipeline(description.ToString());

        await using var host = PipelineHost.Parse(description);
        await host.PauseAsync(cancellationToken); // preroll: duration becomes known
        Out.Info($"duration: {Out.Time(host.Duration)}");

        if (args.GetDouble("seek", -1) is var seek and >= 0)
        {
            host.Seek(TimeSpan.FromSeconds(seek));
            Out.Event("seek", $"-> {seek}s");
        }

        await host.StartAsync(cancellationToken);

        if (args.GetDouble("rate", 1) is var rate and not 1)
        {
            Out.Event("rate", host.SetRate(rate) ? $"playing at {rate}x" : "rate change refused");
        }

        var events = PrintEventsAsync(host, cancellationToken);
        while (!host.Completion.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            Console.Write($"\r  {Out.Time(host.Position)} / {Out.Time(host.Duration)}   ");
            await Task.WhenAny(host.Completion, Task.Delay(500, CancellationToken.None));
        }
        Console.WriteLine();

        if (cancellationToken.IsCancellationRequested) await host.StopAsync(cancellationToken: CancellationToken.None);
        Out.Event("bus", $"finished: {await host.Completion}", ConsoleColor.Green);
        await events;
        return 0;
    }

    private static async Task PrintEventsAsync(PipelineHost host, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in host.Events.ReadAllAsync(cancellationToken))
            {
                switch (e)
                {
                    case Buffering b when b.Percent < 100: Out.Event("buffering", $"{b.Percent}%"); break;
                    case PipelineWarning w: Out.Event("warning", w.Message, ConsoleColor.DarkYellow); break;
                    case PipelineError err: Out.Event("error", err.Message, ConsoleColor.Red); break;
                }
                if (host.Completion.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
    }
}
