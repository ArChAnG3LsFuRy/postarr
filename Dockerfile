# syntax=docker/dockerfile:1

# ----- build stage -----
# The build stage always runs on the build machine's own architecture: the publish is framework-dependent and
# portable (it carries the SQLite/SkiaSharp native libraries for x64, arm64 and arm), so its output is the same
# for every target. Only the runtime stage below is per-architecture — fast multi-arch builds, no emulated SDK.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (cached unless the csproj changes)
COPY src/Postarr/Postarr.csproj src/Postarr/
RUN dotnet restore src/Postarr/Postarr.csproj

# Then build + publish the app (framework-dependent; the runtime lives in the base image below).
COPY src/Postarr/ src/Postarr/
RUN dotnet publish src/Postarr/Postarr.csproj -c Release -o /app -p:UseAppHost=false

# ----- runtime stage -----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# SkiaSharp's native libSkiaSharp.so links fontconfig; without libfontconfig1 every poster render
# throws a DllNotFound, and fonts-dejavu-core gives a real system font for the "Arial" family
# fallback (the badge font itself is bundled as Inter-Medium.ttf and loaded straight from file).
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# All mutable state — SQLite database, downloaded image cache, and original-poster backups — lives
# under this one directory. Mount a volume here to persist it across container recreations.
ENV POSTARR_DATA_DIR=/config
VOLUME /config

ENV ASPNETCORE_URLS=http://0.0.0.0:5286
EXPOSE 5286

# Licence and third-party notices travel with the image.
COPY LICENSE THIRD-PARTY-NOTICES.md /app/

# Runs as an unprivileged user (see the script); PUID/PGID pick which one. Default: the image's "app" user (1654).
COPY docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod 755 /docker-entrypoint.sh
ENV PUID=1654 PGID=1654
ENTRYPOINT ["/docker-entrypoint.sh"]
