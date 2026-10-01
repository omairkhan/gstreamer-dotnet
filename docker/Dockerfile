# syntax=docker/dockerfile:1
# Build:  docker build -f docker/Dockerfile -t mediaflow .
# Run:    docker run --rm -p 8080:8080 -v $PWD/recordings:/app/recordings mediaflow
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/MediaFlow.Server -c Release -o /out \
 && dotnet publish src/MediaFlow.Cli -c Release -o /out/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The native side: GStreamer core + plugin sets used by the pipelines + GObject-Introspection data.
RUN apt-get update && apt-get install -y --no-install-recommends \
      gstreamer1.0-tools gstreamer1.0-plugins-base gstreamer1.0-plugins-good \
      gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav gstreamer1.0-x \
      gir1.2-gstreamer-1.0 gir1.2-gst-plugins-base-1.0 fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 \
    MediaFlow__RecordingsPath=/app/recordings \
    GST_DEBUG=1
VOLUME /app/recordings
EXPOSE 8080
# SIGTERM → CameraRegistry.StopAsync → EOS drain → playable MP4s
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "MediaFlow.Server.dll"]
