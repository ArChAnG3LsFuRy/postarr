using Postarr.Data;
using Postarr.Hubs;
using Postarr.Models;
using Microsoft.EntityFrameworkCore;

namespace Postarr.Services;

public class ActivityLogger
{
    private readonly IDbContextFactory<PostarrDbContext> _dbFactory;
    private readonly ILogger<ActivityLogger> _logger;
    private readonly IScanNotifier _notifier;

    public ActivityLogger(IDbContextFactory<PostarrDbContext> dbFactory,
        ILogger<ActivityLogger> logger, IScanNotifier notifier)
    { _dbFactory = dbFactory; _logger = logger; _notifier = notifier; }

    public async Task LogAsync(string message, string level = "Info")
    {
        _logger.LogInformation("[{Level}] {Message}", level, message);
        using var db = await _dbFactory.CreateDbContextAsync();
        db.ActivityLog.Add(new ActivityLogEntry { Message = message, Level = level });
        await db.SaveChangesAsync();
        await _notifier.LogAsync(level, message);

        var count = await db.ActivityLog.CountAsync();
        if (count > 2000)
        {
            var old = await db.ActivityLog.OrderBy(e => e.TimestampUtc).Take(count - 2000).ToListAsync();
            db.ActivityLog.RemoveRange(old);
            await db.SaveChangesAsync();
        }
    }
}
