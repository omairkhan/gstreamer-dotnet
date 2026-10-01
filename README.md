# MediaFlow: GStreamer × .NET 10, from `playbin` to production

[![ci](https://github.com/omairkhan/gstreamer-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/omairkhan/gstreamer-dotnet/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![C# 14](https://img.shields.io/badge/C%23-14-239120) ![GStreamer 1.24+](https://img.shields.io/badge/GStreamer-1.24%2B-red) ![License: MIT](https://img.shields.io/badge/license-MIT-blue)

In 2024 I wrote **"Bridging the Gap: GStreamer Integration for .NET Core 8 on Windows"**. It covered how to install
GStreamer, set up `PATH`, add `gstreamer-sharp-netcore` and play a video with `playbin` in ~10 lines of C#.

This repository is the sequel: **what it takes to go from that first `playbin` to a real, 24/7 video system in modern C#.**
The same few GStreamer calls grow into a supervised multi-camera recorder with live HLS, a rotating MP4 archive,
snapshots, SIMD motion detection in managed code, a REST/SSE API, health checks, OpenTelemetry metrics, tests and CI.

![MediaFlow dashboard](docs/dashboard.png)

📚 **Blog series (Word):** [docs/blog/GStreamer-dotnet-blog-series.docx](docs/blog/GStreamer-dotnet-blog-series.docx)
Part 1: fundamentals · Part 2: C# in the media path · Part 3: production

---

## The learning path

Every level is a runnable command of one CLI (`mediaflow`) backed by a reusable library (`MediaFlow.Core`).

| Level | Command | What you learn | GStreamer concepts |
|---|---|---|---|
| **0** | `classic` | The 2024 article's program, ported 1:1 to .NET 10 | `Init`, `ParseLaunch`, `playbin`, bus, states |
| **1** | `doctor` | Is the runtime there? Which plugins can I use? | registry, element factories |
| **1** | `hello` | Anatomy of a pipeline: source → caps → overlay → encoder → muxer → sink | elements, pads, caps negotiation, EOS |
| **1** | `play` | A real player: preroll, duration/position, frame-accurate seek, 2× / slow-mo / reverse | queries, seek events, trick-play |
| **1→2** | `transcode` | Building pipelines **by hand**; reacting to streams discovered at runtime | `ElementFactory.Make`, `pad-added`, *sometimes* pads, request pads, `ParseBinFromDescription`, DOT graphs |
| **2** | `analyze` | Pull decoded frames into C# as `IAsyncEnumerable<VideoFrame>`; SIMD motion detection; change properties live | `appsink`, leaky queues, `videorate`/`videoscale`, runtime property changes |
| **2** | `synth` | Push frames rendered in C# into a hardware/software encoder with exact timestamps | `appsrc`, PTS/duration, back-pressure |
| **3** | `nvr` | A supervised 24/7 camera: HLS + segmented MP4 archive + snapshots + motion events, auto-reconnect | `tee`, `hlssink2`, `splitmuxsink`, `multifilesink`, element messages, graceful EOS |
| **3** | `MediaFlow.Server` | Multi-camera web service: REST, Server-Sent Events, HLS, health checks, OpenAPI, Docker | everything above, behind an API |

```text
$ mediaflow analyze
[17:24:03] scene      scene idle
[17:24:05] scene      object moving
[17:24:05] MOTION     started  frame #23 score 0.5 %
[17:24:09] scene      scene idle
[17:24:10] motion     ended    after 4.1s
analysed 140 frames, dropped 0 (consumer too slow), 10.0 fps
```

```text
$ mediaflow nvr --seconds 20 --segment 6 --snapshot 3
[17:28:05] Status     Running
[17:28:05] MotionStarted Motion detected (score 0.1 %): recordings/cam-1/snapshots/snap-00000.jpg
[17:28:10] health     Running, restarts 0, motion score 0.27 % (MOTION)
[17:28:11] SegmentClosed Recording segment finalised: recordings/cam-1/archive/segment-00000.mp4

$ mediaflow nvr --source rtsp://127.0.0.1:8554/offline          # camera unreachable → supervised backoff
[17:28:22] Status     BackingOff (retry in 1.0s: Could not change state to Playing: Failed to connect. (Generic error) (/GstPipeline:pipeline0/GstURIDecodeBin:uridecodebin0/GstRTSPSrc:source))
[17:28:23] Status     BackingOff (retry in 2.1s: ...)
[17:28:25] Status     BackingOff (retry in 4.7s: ...)
```

---

## From the 2024 article to here

```csharp
// 2024: .NET 8, gstreamer-sharp-netcore             // 2026: .NET 10, GirCore (this repo: ClassicScenario.cs)
Application.Init(ref args);                           Gst.Module.Initialize(); Gst.Functions.Init(ref args);
var pipeline = Parse.Launch("playbin uri=...");       var pipeline = Gst.Functions.ParseLaunch("playbin uri=...");
pipeline.SetState(State.Playing);                     pipeline.SetState(Gst.State.Playing);
var bus = pipeline.Bus;                               var bus = pipeline.GetBus();
bus.TimedPopFiltered(Constants.CLOCK_TIME_NONE,       bus.TimedPopFiltered(new Gst.ClockTime(Gst.Constants.CLOCK_TIME_NONE),
    MessageType.Eos | MessageType.Error);                 Gst.MessageType.Eos | Gst.MessageType.Error);
pipeline.SetState(State.Null);                        pipeline.SetState(Gst.State.Null);
```

What changed, and why:

* **Bindings.** `gstreamer-sharp-netcore` was a community repackaging that has not been updated for years.
  [GirCore](https://gircore.github.io) generates bindings from GObject-Introspection for current GStreamer, ships on
  NuGet (`GirCore.Gst-1.0`, `GirCore.GstApp-1.0`), targets modern .NET and runs on Windows, Linux and macOS.
* **Blocking → async.** `TimedPopFiltered(CLOCK_TIME_NONE)` blocks the calling thread forever. `PipelineHost` pumps the
  bus on a dedicated thread into a `Channel<PipelineEvent>` and exposes `await host.Completion`, `StartAsync`,
  `StopAsync` (with EOS drain) and typed events.
* **Strings → builders.** Hand-concatenated pipeline strings break on the first path with a space. `PipelineDescription`
  renders valid, quoted gst-launch syntax and is unit-tested.
* **One pipeline → a system.** Supervision, back-pressure, telemetry, health, tests, containers.

---

## Architecture

<p align="center">
  <img src="docs/architecture.svg" alt="MediaFlow architecture: a GStreamer pipeline fans out to live HLS, an MP4 archive, snapshots and analytics; .NET components host, supervise and expose it" width="100%">
</p>

1. **Encode once → live + archive.** One `x264enc` feeds both HLS and the rotating MP4 archive, each with its own `h264parse`.
2. **Evidence snapshots.** A leaky branch writes a timestamped JPEG every N seconds.
3. **Analytics.** A leaky branch hands small greyscale frames to C# (`FrameReader` → `MotionDetector`), so it can never slow down recording.
4. **Control plane (.NET).** `PipelineHost` turns the bus into typed events, `PipelineSupervisor` rebuilds the pipeline on failure, `CameraSession` fans events out and ASP.NET Core exposes REST, SSE, HLS and health.

| Component | File | Responsibility |
|---|---|---|
| `GstRuntime` | `src/MediaFlow.Core/GstRuntime.cs` | Thread-safe one-time init; plugin availability checks with actionable errors |
| `PipelineDescription` | `Pipelines/PipelineDescription.cs` | Fluent, quoted, testable pipeline syntax incl. `tee` branches |
| `PipelineHost` | `Pipelines/PipelineHost.cs` | Async lifecycle, bus → `Channel`, root-cause errors, seek/rate, graceful EOS stop, DOT dumps, tracing |
| `FrameReader` | `Media/FrameReader.cs` | `appsink` → `IAsyncEnumerable<VideoFrame>`, pooled buffers, **drop-oldest (live)** or **lossless back-pressure (files)** |
| `FrameWriter` | `Media/FrameWriter.cs` | C# frames → `appsrc` with exact PTS/duration |
| `MotionDetector` | `Analytics/MotionDetector.cs` | `Vector256`/`Vector128` frame differencing, smoothing, hysteresis, cooldown |
| `PipelineSupervisor` | `Resilience/PipelineSupervisor.cs` | Restart on error/unexpected EOS; exponential backoff + jitter; stable-run reset; give-up policy |
| `CameraPipeline` / `CameraSession` | `Recipes/` | The production recipe and a supervised, observable camera with fan-out event subscriptions |
| `MediaFlowTelemetry` | `Diagnostics/MediaFlowTelemetry.cs` | `Meter` + `ActivitySource` "MediaFlow" (frames, drops, errors, restarts, motion, analysis latency) |
| `MediaFlow.Server` | `src/MediaFlow.Server` | Minimal APIs, .NET 10 native SSE, HLS static hosting, health checks, OpenAPI, graceful shutdown |

### Production lessons baked into the code

* **Encode once, fan out the compressed stream.** One `x264enc` feeds both HLS and the archive.
* **One `h264parse` per output, after the tee.** MPEG-TS wants `byte-stream`, MP4 wants `avc`. A shared parser fails with
  `not-negotiated`. (Found by running it; see `CameraPipeline.cs`.)
* **Every tee branch needs a `queue`** (its own streaming thread), and **analytics queues are leaky**: analytics may fall
  behind, recording must never do so.
* **Never burn a clock into the frames you analyse.** A ticking timestamp is "motion". Overlays go on the recording and
  snapshot branches only.
* **Stop with EOS, not by killing the pipeline.** Otherwise the MP4 `moov` atom is never written and the file is unplayable.
  `PipelineHost.StopAsync`, the supervisor and the server's `IHostedService.StopAsync` all drain first, so `docker stop`
  leaves playable recordings.
* **Keep the first error.** GStreamer often posts a cascade; the first one names the root cause ("No such file"), later
  ones are fallout ("Failed to start").
* **`appsrc` + preroll = chicken-and-egg.** Don't wait for PAUSED before pushing the first buffer
  (`StartAsync(waitForPreroll: false)`).
* **Validate untrusted input before it reaches a pipeline string or the file system.** The API allowlists URI schemes
  (no `file://`), constrains ids to `[a-z0-9-]` and never returns absolute server paths.
* **Jitter your reconnects**, or 200 cameras hammer the VMS in lock-step after a network blip.

### Modern C# on display

C# 14 **extension members** (`buffer.Pts`, `span.ToClockTime()`, `element.Set(...)` on types we don't own),
primary constructors, collection expressions, `params ReadOnlySpan<T>`, `System.Threading.Lock`, `SearchValues`,
`Vector256`/`Vector128` SIMD, `ArrayPool`, `Channel<T>`, `IAsyncEnumerable`, `TimeProvider`,
`[GeneratedRegex]`, `TypedResults.ServerSentEvents`, `.slnx` solution, central package management,
xUnit v3 on Microsoft.Testing.Platform.

---

## Getting started

### 1. Install GStreamer

**Linux (Ubuntu/Debian)**
```bash
sudo apt install gstreamer1.0-tools gstreamer1.0-plugins-{base,good,bad,ugly} gstreamer1.0-libav gstreamer1.0-x \
                 gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0
```

**Windows** (as in the original article, updated)
1. Download the **runtime** installer from <https://gstreamer.freedesktop.org/download/> (choose *Complete*).
   Both the MSVC and MinGW 64-bit builds work; MSVC is the default recommendation today.
2. Add the `bin` folder to `PATH`, e.g. `C:\Program Files\gstreamer\1.0\msvc_x86_64\bin`
   (or `C:\gstreamer\1.0\mingw_x86_64\bin` for MinGW, exactly as in the 2024 article).
3. Optionally set `GST_PLUGIN_PATH` to `...\lib\gstreamer-1.0`. Open a **new** terminal afterwards.

**macOS:** `brew install gstreamer`

### 2. Run it

```bash
git clone https://github.com/omairkhan/gstreamer-dotnet && cd gstreamer-dotnet
dotnet run --project src/MediaFlow.Cli -- doctor
dotnet run --project src/MediaFlow.Cli -- classic                       # the 2024 article, plays Sintel
dotnet run --project src/MediaFlow.Cli -- hello                         # writes hello.mp4
dotnet run --project src/MediaFlow.Cli -- play hello.mp4 --seek 1 --rate 2
dotnet run --project src/MediaFlow.Cli -- transcode hello.mp4 small.mp4 --height 360 --dot graph.dot
dotnet run --project src/MediaFlow.Cli -- analyze                       # or --source webcam | rtsp://user:pass@cam/stream
dotnet run --project src/MediaFlow.Cli -- synth
dotnet run --project src/MediaFlow.Cli -- nvr --seconds 60              # Ctrl+C stops gracefully
```

### Use your USB camera

Any webcam works as a source: `webcam` is the system default camera, `webcam:N` picks camera number N
(Windows: Media Foundation, macOS: AVFoundation, Linux: `/dev/videoN`).

```bash
gst-device-monitor-1.0 Video/Source                                  # list the cameras on this machine
dotnet run --project src/MediaFlow.Cli -- analyze --source webcam    # wave at the camera → motion events
dotnet run --project src/MediaFlow.Cli -- nvr --source webcam:0 --seconds 60
curl -X POST localhost:5080/api/cameras -H 'content-type: application/json' -d '{"id":"desk","source":"webcam"}'
```

Close other apps that use the camera (Teams, Zoom, the browser) first. On Linux, add your user to the `video`
group if `/dev/video0` is not readable. On macOS, allow camera access for your terminal in System Settings → Privacy.
USB cameras that deliver MJPEG are decoded automatically.

### 3. The server

```bash
dotnet run --project src/MediaFlow.Server        # http://localhost:5080
curl -X POST localhost:5080/api/cameras -H 'content-type: application/json' \
     -d '{"id":"gate-1","source":"rtsp://user:pass@10.0.0.42:554/stream1"}'
curl -N localhost:5080/api/cameras/gate-1/events  # live Server-Sent Events
```

| Endpoint | |
|---|---|
| `GET /` | Dashboard (HLS players, health, motion meter, live event log) |
| `GET/POST /api/cameras`, `GET/DELETE /api/cameras/{id}` | Manage cameras |
| `GET /api/cameras/{id}/events` | SSE stream: `MotionStarted`, `MotionEnded`, `SegmentClosed`, `Snapshot`, `Status`, `Warning`, `Error` |
| `GET /api/cameras/{id}/snapshot` | Latest JPEG |
| `GET /hls/{id}/hls/index.m3u8` | Live HLS |
| `GET /health` | Healthy / Degraded (reconnecting) / Unhealthy (gave up) |
| `GET /openapi/v1.json` | OpenAPI document |

**Docker**
```bash
docker build -f docker/Dockerfile -t mediaflow .
docker run --rm -p 8080:8080 -v "$PWD/recordings:/app/recordings" mediaflow
```

### 4. Tests

```bash
dotnet test     # 35 tests: builder, SIMD vs scalar, motion hysteresis, backoff, and real pipelines
```

Integration tests run real GStreamer pipelines (appsink frame streaming, appsrc→appsink round trip with timestamps,
error propagation, MP4 finalisation on graceful stop, supervisor restarts). They skip automatically when GStreamer is
not installed. CI runs all of it plus a smoke test of every CLI scenario on Ubuntu 24.04.

---

## Where to take it next

* **Hardware acceleration:** swap `x264enc` for `nvh264enc` (NVIDIA), `vah264enc` (Intel/AMD VA-API), `qsvh264enc`
  or `d3d11h264enc`/`mfh264enc` on Windows. The pipeline shape stays identical.
* **AI inference:** hand the `VideoFrame` from `FrameReader` to ONNX Runtime (YOLO etc.), or run inference inside the
  pipeline with DeepStream / `onnx` / `tensor_filter` elements.
* **Sub-second latency to browsers:** add a `webrtcbin` branch next to HLS.
* **Contribution/ingest over the internet:** `srtsink` / `srtsrc` with encryption.
* **Scale out:** one `CameraSession` per camera is independent, so shard cameras across pods and keep the SSE/REST
  contract.

## License

MIT © Umair Akhter
