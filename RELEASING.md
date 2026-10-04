# Releasing Postarr

Releases are built by GitHub Actions from a version tag:

```bash
git tag v1.7.0
git push origin v1.7.0
```

That runs two workflows:

| Workflow | What it produces |
|---|---|
| `.github/workflows/release.yml` | `Postarr-Setup-<version>.exe`, attached to a GitHub release (code-signed once SignPath is set up) |
| `.github/workflows/docker.yml` | `ghcr.io/<owner>/postarr:<version>` and `:latest`, for amd64 and arm64 |

Both can also be started by hand from the repository's **Actions** tab.

## One-time setup

### 1. The GitHub repository

1. Create a public repository on GitHub (for example `postarr`) — open source is required for free signing.
2. Point this folder at it and push:

   ```bash
   git remote add origin https://github.com/<you>/postarr.git
   git push -u origin main
   ```

3. After the first Docker build, open the repository's **Packages** → `postarr` → **Package settings** and set the
   visibility to **Public**, so people can `docker pull` it without signing in.

### 2. Free code signing with SignPath (removes the "Windows protected your PC" warning)

SignPath Foundation signs open-source projects for free, with a certificate issued to the SignPath Foundation.

1. Apply at https://signpath.org (open-source program). They review the project — it needs a public repository,
   an OSI licence (Postarr is MIT) and builds that run on GitHub Actions, which this repository has.
2. Once accepted, in SignPath create (or they create for you):
   - a **project** with the slug `postarr`,
   - a **signing policy** with the slug `release-signing`,
   - two **artifact configurations**:

   `programs` — signs Postarr's own programs inside the uploaded zip:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="Postarr/Postarr.exe"><authenticode-sign /></pe-file>
       <pe-file path="Postarr/Postarr.dll"><authenticode-sign /></pe-file>
       <pe-file path="Tray/PostarrTray.exe"><authenticode-sign /></pe-file>
     </zip-file>
   </artifact-configuration>
   ```

   `installer` — signs the setup program:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <pe-file path="Postarr-Setup-*.exe"><authenticode-sign /></pe-file>
     </zip-file>
   </artifact-configuration>
   ```

3. In the GitHub repository → **Settings** → **Secrets and variables** → **Actions**:
   - secret `SIGNPATH_API_TOKEN` — an API token of a SignPath CI user that may submit to the policy,
   - variable `SIGNPATH_ORGANIZATION_ID` — your SignPath organisation id,
   - (optional) variables `SIGNPATH_PROJECT_SLUG` / `SIGNPATH_POLICY_SLUG` if you used other slugs.

Until `SIGNPATH_API_TOKEN` exists the workflow simply skips signing and publishes an unsigned installer, with a
note in the release telling people to choose **More info → Run anyway**.

Note: even signed, a brand-new certificate builds "reputation" with Windows SmartScreen over the first downloads,
so a milder warning can still appear for a short while.

## Version numbers

The tag decides the version (`v1.8.0` → 1.8.0) for the installer, the program files and the Docker image.
`src/Postarr/Postarr.csproj` (`<Version>`) and `deploy/Postarr.iss` (`AppVersion`) only set the default for local
builds — keep them in step when you can.

## Building locally

```bash
dotnet publish src/Postarr/Postarr.csproj -c Release -r win-x64 --self-contained true -o dist/Postarr
dotnet publish src/PostarrTray/PostarrTray.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/Tray
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" deploy\Postarr.iss
docker build -t postarr .
```
