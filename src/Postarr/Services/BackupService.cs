using Postarr.Data;
using Postarr.MediaServers;
using Postarr.Plex;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Postarr.Services;

/// <summary>
/// Downloads and stores the current Plex poster/background for an item before
/// Postarr overwrites it, so it can be restored later.
/// Files are stored under: {BackupDirectory}\{ratingKey}\poster-original.jpg
/// </summary>
public class BackupService
{
    // Must match Program.cs, which moves %ProgramData%\Curatarr → \Postarr on first run. This only
    // falls back to the old folder if that move couldn't happen, so backups always sit next to the
    // database rather than being split across both folders.
    private static string ServiceDataDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var current = Path.Combine(appData, "Postarr");
        var legacy  = Path.Combine(appData, "Curatarr");
        return !Directory.Exists(current) && Directory.Exists(legacy) ? legacy : current;
    }

    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly SettingsRepository _settings;
    private readonly IHttpClientFactory _http;

    public BackupService(
        IDbContextFactory<PostarrDbContext> dbFactory,
        SettingsRepository settings,
        IHttpClientFactory http)
    {
        _dbFactory = dbFactory;
        _settings  = settings;
        _http      = http;
    }

    public string GetBackupDirectory()
    {
        var dir = _settings.Get().Backup.BackupDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            // Mirror Program.cs so backups land in the same (optionally container-mounted) data dir.
            var baseDir = Environment.GetEnvironmentVariable("POSTARR_DATA_DIR");
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                var isService = WindowsServiceHelpers.IsWindowsService();
                baseDir = isService ? ServiceDataDir() : Path.Combine(AppContext.BaseDirectory, "data");
            }
            dir = Path.Combine(baseDir, "Backups");
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Backs up poster if backup is enabled and no backup yet exists for this item.</summary>
    public async Task BackupPosterIfNeededAsync(
        string ratingKey, string? currentPosterUrl, string itemDir, CancellationToken ct = default)
    {
        if (!_settings.Get().Backup.BackupEnabled) return;

        var dest = Path.Combine(itemDir, "poster-original.jpg");
        if (File.Exists(dest)) return; // already backed up; don't overwrite with a previously-modified version

        if (string.IsNullOrEmpty(currentPosterUrl)) return;

        try
        {
            var http = _http.CreateClient("imagedownload");
            var bytes = await http.GetByteArrayAsync(currentPosterUrl, ct);
            await File.WriteAllBytesAsync(dest, bytes, ct);
        }
        catch { /* non-fatal; backup failure should not block poster apply */ }
    }

    public async Task BackupBackgroundIfNeededAsync(
        string? currentBackgroundUrl, string itemDir, CancellationToken ct = default)
    {
        if (!_settings.Get().Backup.BackupEnabled) return;

        var dest = Path.Combine(itemDir, "background-original.jpg");
        if (File.Exists(dest)) return;
        if (string.IsNullOrEmpty(currentBackgroundUrl)) return;

        try
        {
            var http = _http.CreateClient("imagedownload");
            var bytes = await http.GetByteArrayAsync(currentBackgroundUrl, ct);
            await File.WriteAllBytesAsync(dest, bytes, ct);
        }
        catch { }
    }

    public string GetItemBackupDir(string ratingKey)
    {
        var dir = Path.Combine(GetBackupDirectory(), SanitiseKey(ratingKey));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deletes an item's backup folder. Called when the item is pruned from the library (deleted in
    /// Plex/Sonarr/Radarr) so its original-poster backup doesn't linger as orphaned disk cruft.
    /// Non-fatal: a leftover backup is harmless, so failures are swallowed.
    /// </summary>
    public void DeleteItemBackup(string ratingKey)
    {
        try
        {
            // NB: build the path directly, not via GetItemBackupDir, which would re-create the folder.
            var dir = Path.Combine(GetBackupDirectory(), SanitiseKey(ratingKey));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* leftover backup files are harmless */ }
    }

    private static string SanitiseKey(string key) =>
        string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Returns true and restores the backed-up poster to Plex if a backup exists.</summary>
    public async Task<bool> RestorePosterAsync(
        string ratingKey, IMediaServerClient plex, CancellationToken ct = default)
    {
        var path = Path.Combine(GetItemBackupDir(ratingKey), "poster-original.jpg");
        if (!File.Exists(path)) return false;

        var bytes = await File.ReadAllBytesAsync(path, ct);
        await plex.UploadPosterAsync(ratingKey, bytes, "image/jpeg", ct);
        return true;
    }

    public async Task<bool> RestoreBackgroundAsync(
        string ratingKey, IMediaServerClient plex, CancellationToken ct = default)
    {
        var path = Path.Combine(GetItemBackupDir(ratingKey), "background-original.jpg");
        if (!File.Exists(path)) return false;

        var bytes = await File.ReadAllBytesAsync(path, ct);
        await plex.UploadBackgroundAsync(ratingKey, bytes, "image/jpeg", ct);
        return true;
    }

    public record BackupInfo(bool HasPoster, bool HasBackground, long PosterBytes, long BackgroundBytes);

    public BackupInfo GetBackupInfo(string ratingKey)
    {
        var dir = Path.Combine(GetBackupDirectory(), SanitiseKey(ratingKey));
        var posterPath = Path.Combine(dir, "poster-original.jpg");
        var bgPath     = Path.Combine(dir, "background-original.jpg");
        return new BackupInfo(
            File.Exists(posterPath),    File.Exists(bgPath),
            File.Exists(posterPath)     ? new FileInfo(posterPath).Length : 0,
            File.Exists(bgPath)         ? new FileInfo(bgPath).Length     : 0);
    }
}
