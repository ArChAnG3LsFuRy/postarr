# Postarr

A Windows app for managing Plex poster art — pulls posters from TMDB, FanArt.tv,
and TheTVDB, burns in quality badges (resolution, HDR, audio codec) like Kometa's
overlay system, and either applies them automatically or queues them for your
approval. Runs as a background Windows Service with a Sonarr/Radarr-style web UI
at `http://localhost:5286`, plus a system-tray helper for opening the UI.

> Postarr was previously called **Curatarr**. Upgrading in place is supported and
> non-destructive — see [Upgrading from Curatarr](#upgrading-from-curatarr).

## What you need before building

1. **.NET 8 SDK** — download from https://dotnet.microsoft.com/download/dotnet/8.0
   (get the **SDK**, not just the Runtime). Verify it's installed:
   ```
   dotnet --version
   ```
   should print something starting with `8.`.

2. **(Optional, for building the installer)** Inno Setup 6 —
   https://jrsoftware.org/isdl.php. Only needed to produce `Postarr-Setup-*.exe`.
   You can skip this and run the app directly with `dotnet run` instead.

3. API keys, gathered ahead of time (the app runs fine with none configured yet —
   you can add them later in Settings — but you'll want at least one poster source):
   - **Plex token** — see https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/
   - **TMDB API key** — free at https://www.themoviedb.org/settings/api
   - **FanArt.tv API key** — free at https://fanart.tv/get-an-api-key/
   - **TheTVDB API key** — free at https://thetvdb.com/api-information
   - **MDBList API key** *(optional)* — enables the Audience Score badge

## Project layout

```
src/
  Postarr/                 <- the app itself (single ASP.NET Core project)
    Models/                 domain models (LibraryItem, PosterCandidate, Settings, etc.)
    Data/                   EF Core DbContext + SQLite storage, migrations
    Plex/                   Plex Media Server API client + webhook parsing
    Metadata/               TMDB / FanArt.tv / TVDB clients + aggregator
    Overlays/               badge-rendering engine (SkiaSharp)
    Services/               scan loop, poster-apply logic, backups, scheduling
    Controllers/            the HTTP API the web UI calls
    wwwroot/                the web UI itself (plain HTML/CSS/JS, no build step)
  PostarrTray/             <- system-tray helper that opens the web UI
deploy/
  Postarr.iss               Inno Setup script that builds the Windows installer
  service-install.bat       manual service registration helpers
  service-uninstall.bat
scripts/
  make-icons.ps1            generates favicon.ico / tray app.ico from source PNGs
```

## 1. Build and run it directly (fastest way to try it)

From the repository root:

```
dotnet run --project src\Postarr\Postarr.csproj
```

This starts Postarr as a normal console app on `http://localhost:5286`. Leave the
terminal window open — closing it stops the app. Good for testing before you commit
to installing it as a permanent background service. Press `Ctrl+C` to stop it.

In this mode the database and cache live in the app's own output folder
(`src\Postarr\bin\Debug\net8.0\data`), not in `ProgramData`.

## 2. Build the installer (recommended)

This gives you a normal double-click installer, like Sonarr's or Radarr's, that
handles service registration, the firewall rule, and the tray helper for you.

**Step 1 — publish both projects:**

```
dotnet publish src\Postarr\Postarr.csproj      -c Release -r win-x64 --self-contained true -o dist\Postarr
dotnet publish src\PostarrTray\PostarrTray.csproj -c Release -r win-x64 --self-contained true -o dist\Tray
```

Self-contained means the target machine needs **no .NET runtime installed** — the
installer ships everything required.

**Step 2 — compile the installer:**

```
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" deploy\Postarr.iss
```

(Adjust the path if you installed Inno Setup machine-wide, in which case it's
usually under `C:\Program Files (x86)\Inno Setup 6\`.)

This produces `dist\Postarr-Setup-1.0.0.exe`. Run it **as Administrator** on any
Windows machine — it installs Postarr to `Program Files`, registers it as an
auto-starting Windows Service, opens the firewall on port 5286, installs the tray
helper to run at login, and offers to open the web UI when done. Uninstalling via
"Add or Remove Programs" cleanly stops and removes the service and firewall rule.

## 3. Install as a service manually (without the installer)

After publishing to `dist\Postarr` as above, from an **Administrator** prompt:

```
sc create Postarr binPath= "\"C:\path\to\dist\Postarr\Postarr.exe\" --urls http://0.0.0.0:5286" start= auto DisplayName= "Postarr"
sc description Postarr "Postarr - Plex artwork manager"
netsh advfirewall firewall add rule name="Postarr" dir=in action=allow protocol=TCP localport=5286
sc start Postarr
```

(The spaces after `binPath=`, `start=`, and `DisplayName=` are required — `sc.exe`
is picky about that syntax.) To remove it later:

```
sc stop Postarr
sc delete Postarr
netsh advfirewall firewall delete rule name="Postarr"
```

When running as a service, data lives in `%ProgramData%\Postarr`.

## Upgrading from Curatarr

The installer keeps the same `AppId` as the old Curatarr releases, so it upgrades
in place rather than appearing as a second app. On upgrade it stops and removes the
old `Curatarr` service, deletes the old shortcuts, firewall rule and leftover
binaries, and registers everything under the new name.

Your data is migrated automatically on first run, not recreated:

- `%ProgramData%\Curatarr` is **moved** to `%ProgramData%\Postarr`
- `curatarr.db` is **renamed** to `postarr.db` (along with its `-wal`/`-shm` sidecars)

If either step can't complete (files locked, permissions), the app falls back to
using the existing location rather than starting on an empty database. Settings,
API keys, scan history and backups all carry over. You may be asked to sign in once
more if authentication is enabled, since session cookies are reissued.

## First-run setup, once Postarr is running

1. Go to `http://localhost:5286` (or `http://<server-ip>:5286` from another device
   on your network).
2. Open **Settings**.
3. Fill in your Plex server URL and token, then click **Test** to confirm it connects.
4. Fill in whichever of TMDB / FanArt.tv / TVDB / MDBList keys you have, testing each.
5. Choose **Apply automatically** or **Add to review queue** under Poster Selection
   — review queue is the safer starting choice since you can see what it picked
   before anything changes in Plex.
6. Click **Save changes**.
7. Go back to the sidebar and click **Scan Library** to do your first pass.

### Optional: instant updates via Plex webhooks

Without this, Postarr finds new content on its own scan schedule (default: every
60 minutes, configurable in Settings). For instant reaction the moment something's
added to Plex (requires Plex Pass):

1. In Plex, go to **Settings → Webhooks → Add Webhook**.
2. Enter `http://<this-machine's-IP>:5286/api/webhooks/plex`.
3. Save. New content will now trigger Postarr within a few seconds of being added.

## Notes on what's implemented

- **Poster sources**: TMDB, FanArt.tv, and TVDB are all wired with real API calls.
  Results are merged and ranked (textless posters first if you've turned that
  preference on, then by community rating/likes). Collections additionally surface
  any custom posters already uploaded to Plex.
- **Textless posters**: each provider has its own convention for marking an image
  as having no title text baked in; Postarr normalizes all three into a single
  "No text" flag you can filter on in the poster picker.
- **Overlay badges**: resolution, dynamic range (HDR/DV), audio codec, streaming
  provider, studio and award badges are drawn onto the poster before it's uploaded
  to Plex, positioned and sized however you configure in Settings. Source images are
  normalised to a 2:3 aspect first so Plex's own cropping can't clip a badge.
- **Two operating modes**: Auto-apply pushes the best match to Plex immediately;
  Review Queue holds every proposed change until you approve or dismiss it.
- **Per-item overrides**: any movie or show can be marked to prefer textless
  posters regardless of the global setting, and you can upload your own image.
- **Seasons**: TV seasons get their own poster search and picker; season badge
  overlays inherit the parent show's resolution/HDR info, since Plex doesn't track
  per-season media specs.
- **Backups**: the existing Plex poster/background is downloaded and stored before
  Postarr overwrites it, so any change can be rolled back.
- **Mobile**: the UI is responsive, with an A–Z jump bar on both desktop and mobile.

## Known limitations

- **Plex provides no API for deleting uploaded posters.** Postarr avoids re-uploading
  unchanged artwork so the list doesn't grow, but posters uploaded previously can only
  be removed from within Plex itself.
- **Kometa has no season banner overlay**, so there's no season-banner equivalent here.
- **ThePosterDB has no public API**, so it isn't available as a poster source.
