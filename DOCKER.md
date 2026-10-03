# Running Postarr on Linux / Docker / NAS

Postarr is a cross-platform ASP.NET Core app, so it runs anywhere .NET 8 runs. The Windows installer
adds a service + tray icon; on Linux the simplest path is the container below. The web UI, scanning,
poster generation and Plex integration are all identical to the Windows build.

## Quick start (Docker Compose)

```bash
docker compose up -d --build
```

Then open **http://<host-ip>:5286**.

State (SQLite database, image cache, poster backups) is stored in `./config` next to the compose
file — back that folder up and your library, settings and backups survive any container recreation.

## Quick start (plain Docker)

```bash
docker build -t postarr .
docker run -d --name postarr \
  -p 5286:5286 \
  -v /path/on/host/postarr-config:/config \
  --restart unless-stopped \
  postarr
```

## Persistence

Everything mutable lives under `/config` inside the container (set via the `POSTARR_DATA_DIR`
environment variable, which the image sets for you):

| Path                | Contents                          |
|---------------------|-----------------------------------|
| `/config/postarr.db`| SQLite database (library, settings, API keys) |
| `/config/imagecache`| Downloaded artwork cache          |
| `/config/Backups`   | Original Plex posters, for restore |

Mount a host volume at `/config` (as shown above) so it persists.

## Changing the port

Set `ASPNETCORE_URLS` and update the published port to match, e.g. to serve on 8080:

```bash
docker run -d --name postarr -p 8080:8080 \
  -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
  -v /path/on/host/postarr-config:/config postarr
```

## ARM (Synology / QNAP / Raspberry Pi)

The `mcr.microsoft.com/dotnet` base images and the SkiaSharp Linux native assets are multi-arch
(x64, arm64 and 32-bit arm are all bundled), so the same Dockerfile builds for ARM. On an ARM host
`docker build` just works; to build an arm64 image from an x86 machine use buildx:

```bash
docker buildx build --platform linux/arm64 -t postarr:arm64 --load .
```

A 64-bit OS is recommended (arm64); 32-bit ARM (armhf) also has a native lib but is slower and
memory-constrained for image work.

## Migrating an existing Windows install

Copy your Windows data folder contents into the mounted `/config` volume before first start:

- From `C:\ProgramData\Postarr\` (service install) — `postarr.db`, `imagecache\`, `Backups\`.

Postarr opens the existing `postarr.db` in place, so your whole library, settings and API keys carry
over. (The legacy `curatarr.db` name is auto-migrated too.)

## Notes

- The Windows system-tray helper (`PostarrTray`) is Windows-only and intentionally not part of the
  container — a container has no tray. Manage the container with `docker` instead.
- Talking to Plex is over its HTTP API, so Postarr does **not** need access to your media files —
  only network access to your Plex server and the internet (TMDB / FanArt / TVDB).
