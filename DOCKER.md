# Running Postarr on Linux / Docker / NAS

Postarr is a cross-platform ASP.NET Core app, so it runs anywhere .NET 8 runs. The Windows installer
adds a service + tray icon; on Linux the simplest path is the container below. The web UI, scanning,
poster generation and Plex integration are all identical to the Windows build.

## Quick start (pre-built image)

Images for PCs/servers (amd64) and ARM (arm64) are published to GitHub Container Registry with each release:

```bash
docker run -d --name postarr   -p 5286:5286   -v /path/on/host/postarr-config:/config   -e PUID=1000 -e PGID=1000   --restart unless-stopped   ghcr.io/<github-user>/postarr:latest
```

Then open **http://<host-ip>:5286**. The first visit asks you to **create a login** — Postarr stays locked until
you do, because it stores your media server token and API keys.

## Quick start (Docker Compose, building from source)

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

## User and permissions (PUID / PGID)

Postarr never runs as root inside the container. On start it makes `/config` belong to the user given by
`PUID`/`PGID` and then runs as that user. The default (1654) is the .NET image's built-in `app` user; set them
to your own IDs (run `id` on the host) if you want to open the files in `/config` yourself. If you start the
container with Docker's `--user` option instead, that user must already be able to write to `/config`.

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

The published image includes arm64 (tested: the database and poster/badge drawing work on arm64). The
`mcr.microsoft.com/dotnet` base images and the SkiaSharp Linux native assets are multi-arch
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
- Postarr talks to Plex, Jellyfin or Emby over their HTTP APIs, so it does **not** need access to your media
  files — only network access to your media server and the internet (TMDB / FanArt.tv / TheTVDB).
- If your media server runs on the same machine as Docker Desktop, use `http://host.docker.internal:<port>` as its
  address in Settings (inside a container, `localhost` means the container itself).
