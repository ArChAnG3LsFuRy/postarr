using System.Text.Json;
using System.Text.Json.Serialization;
using Postarr.Models;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Data;

public class SettingsRepository
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private AppSettings? _cached;
    private readonly object _lock = new();

    // Use string enums so BadgePosition, ApplyMode etc. are human-readable in the
    // stored JSON and survive any future enum reordering without corruption.
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsRepository(IDbContextFactory<PostarrDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public AppSettings Get()
    {
        lock (_lock) { if (_cached != null) return _cached; }

        using var db = _dbFactory.CreateDbContext();
        var record   = db.Settings.FirstOrDefault(s => s.Id == 1);
        AppSettings settings;
        if (record == null)
        {
            settings = new AppSettings();
            db.Settings.Add(new SettingsRecord { Id = 1, JsonPayload = JsonSerializer.Serialize(settings, _opts) });
            db.SaveChanges();
        }
        else
        {
            settings = JsonSerializer.Deserialize<AppSettings>(record.JsonPayload, _opts) ?? new AppSettings();
        }

        lock (_lock) { _cached = settings; }
        return settings;
    }

    public void Save(AppSettings settings)
    {
        using var db = _dbFactory.CreateDbContext();
        var record   = db.Settings.FirstOrDefault(s => s.Id == 1);
        var json     = JsonSerializer.Serialize(settings, _opts);
        if (record == null)
            db.Settings.Add(new SettingsRecord { Id = 1, JsonPayload = json });
        else
            record.JsonPayload = json;
        db.SaveChanges();

        lock (_lock) { _cached = settings; }
    }
}
