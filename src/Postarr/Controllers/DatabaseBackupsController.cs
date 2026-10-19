using Postarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting.WindowsServices;
using Postarr.Services;

namespace Postarr.Controllers;

/// <summary>Settings → Backup → Database backups: list, back up now, download, restore.</summary>
[ApiController]
[Route("api/backups/database")]
public class DatabaseBackupsController : ControllerBase
{
    private readonly DatabaseBackupService _backups;
    private readonly SettingsRepository    _settings;

    public DatabaseBackupsController(DatabaseBackupService backups, SettingsRepository settings)
    { _backups = backups; _settings = settings; }

    // A restore is applied when Postarr starts again. The Windows service and Docker restart it automatically after it
    // exits; a copy started by hand has to be started again.
    private static bool RestartsItself =>
        WindowsServiceHelpers.IsWindowsService()
        || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    [HttpGet]
    public IActionResult List()
    {
        var b = _settings.Get().Backup;
        return Ok(new
        {
            enabled   = b?.DatabaseBackupsEnabled != false,
            keep      = b?.DatabaseBackupKeep ?? 14,
            directory = _backups.BackupDirectory,
            restartsItself = RestartsItself,
            items     = _backups.List(),
        });
    }

    [HttpPost]
    public async Task<IActionResult> BackUpNow()
    {
        var name = await _backups.BackUpNowAsync("manual");
        return Ok(new { name });
    }

    [HttpGet("{name}/download")]
    public IActionResult Download(string name)
    {
        var path = _backups.PathFor(name);
        if (path == null || !System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, "application/vnd.sqlite3", name);
    }

    [HttpPost("{name}/restore")]
    public async Task<IActionResult> Restore(string name)
    {
        var safety = await _backups.StageRestoreAsync(name);
        if (safety == null) return NotFound(new { error = "That backup doesn't exist." });
        // Exit with a failure code once the response is on its way: the Windows service's recovery settings (and
        // Docker's restart policy) start Postarr again, and the staged restore is applied before the database opens.
        _ = Task.Run(async () => { await Task.Delay(1500); Environment.Exit(1); });
        return Ok(new { safetyBackup = safety, restartsItself = RestartsItself });
    }
}
