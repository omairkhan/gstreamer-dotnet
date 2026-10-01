using MediaFlow.Cli;
using MediaFlow.Cli.Scenarios;
using MediaFlow.Core;

// A single binary, from "hello pipeline" to a supervised NVR. Run without arguments for help.
var cli = CliArgs.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let pipelines drain (EOS) instead of killing the process
    cts.Cancel();
};

if (cli.Command is null or "help" or "--help" or "-h")
{
    Console.WriteLine(CliArgs.Help);
    return 0;
}

try
{
    GstRuntime.EnsureInitialized();
    return cli.Command switch
    {
        // Level 1: fundamentals
        // Level 0: the 2024 article, modernised
        "classic" => ClassicScenario.Run(cli),
        "doctor" => DoctorScenario.Run(),
        "hello" => await HelloScenario.RunAsync(cli, cts.Token),
        "play" => await PlayScenario.RunAsync(cli, cts.Token),
        "transcode" => await TranscodeScenario.RunAsync(cli, cts.Token),
        // Level 2: C# in the media path
        "analyze" => await AnalyzeScenario.RunAsync(cli, cts.Token),
        "synth" => await SynthScenario.RunAsync(cli, cts.Token),
        // Level 3: production
        "nvr" => await NvrScenario.RunAsync(cli, cts.Token),
        _ => Fail($"Unknown command '{cli.Command}'.\n\n{CliArgs.Help}"),
    };
}
catch (MissingGstPluginException ex)
{
    return Fail(ex.Message);
}
catch (GLib.GException ex)
{
    return Fail($"Invalid pipeline: {ex.Message}");
}
catch (MediaFlow.Core.Pipelines.PipelineFailedException ex)
{
    var hint = args.Any(a => a.StartsWith("webcam", StringComparison.Ordinal))
        ? "\nTip: is the camera connected and not in use by another app? List cameras with: gst-device-monitor-1.0 Video/Source"
        : "";
    return Fail($"Pipeline failed: {ex.Message}{hint}");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
