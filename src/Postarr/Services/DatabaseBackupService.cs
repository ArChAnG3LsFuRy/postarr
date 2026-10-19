using Postarr.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Postarr.Services;

/// <summary>
/// Backups of Postarr's own database (postarr.db): every chosen poster, setting and collection lives there.
/// - daily, while Postarr runs (kept: the newest <c>DatabaseBackupKeep</c>);
/// - on the first start of a new version, before the database is upgraded (see <see cref="BackupBeforeUpgrade"/>);
/// - on demand ("Back up now"), and as a safety copy before a restore.
/// Copies are taken with SQLite's online backup, so they're consistent while Postarr is running. They live in
/// {data}/Backups/Database. A restore is staged (restore-pending.txt) and applied by Program.cs on the next start,
/// before the database is opened.
/// </summary>
public class DatabaseBackupService : BackgroundService
{
    public const string DbFileName  = "postarr.db";
    const string PendingRestoreFile = "restore-pending.txt";
    const string VersionMarkerFile  = "last-version.txt";
    static readonly Regex NamePattern = new(@"^postarr-(\d{8}-\d{6})-([a-z0-9.\-]+)\.db$", RegexOptions.Compiled);

    private readonly string               _dataDir;
    private readonly SettingsRepository   _settings;
    private readonly IServiceScopeFactory _scopes;

    public DatabaseBackupService(string dataDir, SettingsRepository settings, IServiceScopeFactory scopes)
    { _dataDir = dataDir; _settings = settings; _scopes = scopes; }

    // ActivityLogger is scoped; this service lives for the whole app.
    private async Task LogAsync(string message, string level = "Info")
    {
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ActivityLogger>().LogAsync(message, level);
    }

    public static string BackupDir(string dataDir) => Path.Combine(dataDir, "Backups", "Database");
    public string BackupDirectory => BackupDir(_dataDir);

    public record BackupFile(string Name, long SizeBytes, DateTime CreatedUtc, string Kind);

    public static string AppVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var v = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                ?? asm?.GetName().Version?.ToString() ?? "unknown";
        return v.Split('+')[0];
    }

    // ── Static helpers used by Program.cs before the app (and the database) starts ─────────────────────

    /// <summary>Copies the database with SQLite's online backup (consistent even while it is in use).</summary>
    public static string CreateBackupFile(string dataDir, string kind)
    {
        var dir = BackupDir(dataDir);
        Directory.CreateDirectory(dir);
        var name = $"postarr-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Regex.Replace(kind.ToLowerInvariant(), "[^a-z0-9.\\-]", "-")}.db";
        var dest = Path.Combine(dir, name);
        var tmp  = dest + ".tmp";
        using (var src = new SqliteConnection($"Data Source={Path.Combine(dataDir, DbFileName)};Mode=ReadOnly;Pooling=False"))
        using (var dst = new SqliteConnection($"Data Source={tmp};Pooling=False"))
        {
            src.Open(); dst.Open();
            src.BackupDatabase(dst);
        }
        File.Move(tmp, dest, overwrite: true);
        return name;
    }

    /// <summary>First start of a new version: back the database up before it is upgraded. Returns the file name, if any.</summary>
    public static string? BackupBeforeUpgrade(string dataDir)
    {
        var marker  = Path.Combine(dataDir, VersionMarkerFile);
        var current = AppVersion();
        var last    = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        string? made = null;
        if (last != current && File.Exists(Path.Combine(dataDir, DbFileName)))
        {
            try { made = CreateBackupFile(dataDir, $"before-{current}"); }
            catch (Exception ex) { Console.WriteLine($"[Postarr] Pre-update database backup failed: {ex.Message}"); return null; }
        }
        File.WriteAllText(marker, current);
        return made;
    }

    /// <summary>Applies a restore staged by <see cref="StageRestore"/>. Runs before the database is opened.</summary>
    public static string? ApplyPendingRestore(string dataDir)
    {
        var pending = Path.Combine(dataDir, PendingRestoreFile);
        if (!File.Exists(pending)) return null;
        var name = File.ReadAllText(pending).Trim();
        File.Delete(pending);   // one attempt only: a bad file must never stop Postarr from starting
        var src = SafePath(dataDir, name);
        if (src == null || !File.Exists(src)) { Console.WriteLine($"[Postarr] Restore skipped: backup '{name}' not found."); return null; }
        var db = Path.Combine(dataDir, DbFileName);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(db + suffix)) File.Delete(db + suffix);
        File.Copy(src, db, overwrite: true);
        Console.WriteLine($"[Postarr] Database restored from {name}.");
        return name;
    }

    static string? SafePath(string dataDir, string name)
        => NamePattern.IsMatch(name) && Path.GetFileName(name) == name ? Path.Combine(BackupDir(dataDir), name) : null;

    // ── Instance API (controller) ────────────────────────────────────────────────────────────────────────

    public List<BackupFile> List()
    {
        var dir = BackupDirectory;
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "postarr-*.db")
            .Select(f => new FileInfo(f))
            .Select(fi => (fi, m: NamePattern.Match(fi.Name)))
            .Where(x => x.m.Success)
            .Select(x => new BackupFile(x.fi.Name, x.fi.Length,
                DateTime.SpecifyKind(DateTime.ParseExact(x.m.Groups[1].Value, "yyyyMMdd-HHmmss", null), DateTimeKind.Utc),
                x.m.Groups[2].Value))
            .OrderByDescending(b => b.CreatedUtc)
            .ToList();
    }

    public string? PathFor(string name) => SafePath(_dataDir, name);

    public async Task<string> BackUpNowAsync(string kind, CancellationToken ct = default)
    {
        var name = await Task.Run(() => CreateBackupFile(_dataDir, kind), ct);
        Prune();
        await LogAsync($"Database backed up: {name}");
        return name;
    }

    /// <summary>Takes a safety copy of the current database, then stages the restore for the next start.</summary>
    public async Task<string?> StageRestoreAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (path == null || !File.Exists(path)) return null;
        var safety = await BackUpNowAsync("before-restore", ct);
        await File.WriteAllTextAsync(Path.Combine(_dataDir, PendingRestoreFile), name, ct);
        await LogAsync($"Database restore from {name} staged - Postarr is restarting to apply it (current database saved as {safety}).", "Warning");
        return safety;
    }

    // Keeps the newest N backups, plus the newest pre-update and pre-restore copies whatever their age.
    void Prune()
    {
        var keep = Math.Clamp(_settings.Get().Backup?.DatabaseBackupKeep ?? 14, 1, 365);
        var all  = List();
        var protect = new HashSet<string>(new[]
        {
            all.FirstOrDefault(b => b.Kind.StartsWith("before-") && !b.Kind.Equals("before-restore"))?.Name,
            all.FirstOrDefault(b => b.Kind.Equals("before-restore"))?.Name,
        }.OfType<string>());
        foreach (var old in all.Skip(keep).Where(b => !protect.Contains(b.Name)))
        {
            try { File.Delete(Path.Combine(BackupDirectory, old.Name)); } catch { /* try again next time */ }
        }
    }

    // Daily backup: checked hourly, taken when the newest backup is a day old (so a restart doesn't add extra copies).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Get().Backup?.DatabaseBackupsEnabled != false)
                {
                    var newest = List().FirstOrDefault();
                    if (newest == null || newest.CreatedUtc < DateTime.UtcNow.AddHours(-24))
                        await BackUpNowAsync("daily", stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await LogAsync($"Daily database backup failed: {ex.Message}", "Error");
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
