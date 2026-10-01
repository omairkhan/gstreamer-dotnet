using System.Net.ServerSentEvents;
using System.Text.Json.Serialization;
using MediaFlow.Core;
using MediaFlow.Core.Diagnostics;
using MediaFlow.Core.Recipes;
using MediaFlow.Server;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

GstRuntime.EnsureInitialized();

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection("MediaFlow").Get<MediaFlowSettings>() ?? new MediaFlowSettings();
Directory.CreateDirectory(settings.RecordingsPath);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CameraRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CameraRegistry>());
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHealthChecks().AddCheck<CamerasHealthCheck>("cameras");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

var app = builder.Build();
var registry = app.Services.GetRequiredService<CameraRegistry>();

app.MapOpenApi();
app.MapHealthChecks("/health");
app.UseDefaultFiles();
app.UseStaticFiles();

// HLS playlists/segments written by hlssink2, served with the right MIME types and no caching of the playlist.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
contentTypes.Mappings[".ts"] = "video/mp2t";
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/hls",
    FileProvider = new PhysicalFileProvider(registry.RecordingsPath),
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".m3u8", StringComparison.Ordinal)) ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

var api = app.MapGroup("/api");

api.MapGet("/info", () => new { gstreamer = GstRuntime.Version, dotnet = Environment.Version.ToString() });

api.MapGet("/cameras", () => registry.All.Select(CameraRegistry.Summarise));

api.MapGet("/cameras/{id}", Results<Ok<CameraSummary>, NotFound> (string id) =>
    registry.Find(id) is { } c ? TypedResults.Ok(CameraRegistry.Summarise(c)) : TypedResults.NotFound());

api.MapPost("/cameras", Results<Created<CameraSummary>, ValidationProblem, Conflict<string>> (CameraRequest request) =>
{
    if (CameraRegistry.Validate(request) is { } error)
    {
        return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["camera"] = [error] });
    }
    try
    {
        var camera = registry.Add(request);
        return TypedResults.Created($"/api/cameras/{request.Id}", CameraRegistry.Summarise(camera));
    }
    catch (InvalidOperationException ex)
    {
        return TypedResults.Conflict(ex.Message);
    }
});

api.MapDelete("/cameras/{id}", async Task<Results<NoContent, NotFound>> (string id) =>
    await registry.RemoveAsync(id) ? TypedResults.NoContent() : TypedResults.NotFound());

// Live event stream (.NET 10 native Server-Sent Events): motion, recordings, snapshots, reconnects.
api.MapGet("/cameras/{id}/events", Results<ServerSentEventsResult<CameraEvent>, NotFound> (string id, CancellationToken ct) =>
    registry.Find(id) is { } c
        ? TypedResults.ServerSentEvents(Stream(c, registry.RecordingsPath, ct))
        : TypedResults.NotFound());

api.MapGet("/cameras/{id}/events/recent", Results<Ok<IEnumerable<CameraEvent>>, NotFound> (string id) =>
    registry.Find(id) is { } c ? TypedResults.Ok(c.RecentEvents.Reverse().Select(e => Relative(e, registry.RecordingsPath))) : TypedResults.NotFound());

api.MapGet("/cameras/{id}/snapshot", Results<PhysicalFileHttpResult, NotFound> (string id) =>
    registry.Find(id)?.LatestSnapshot is { } path && File.Exists(path)
        ? TypedResults.PhysicalFile(Path.GetFullPath(path), "image/jpeg")
        : TypedResults.NotFound());

app.Logger.LogInformation("MediaFlow server on {Gst}. Metrics meter: {Meter}", GstRuntime.Version, MediaFlowTelemetry.Name);
app.Run();

static async IAsyncEnumerable<SseItem<CameraEvent>> Stream(CameraSession camera, string root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var e in camera.SubscribeAsync(ct))
    {
        yield return new SseItem<CameraEvent>(Relative(e, root), e.Kind.ToString());
    }
}

// Never leak absolute server paths to clients.
static CameraEvent Relative(CameraEvent e, string root) =>
    e.Path is null ? e : e with { Path = Path.GetRelativePath(root, Path.GetFullPath(e.Path)) };
