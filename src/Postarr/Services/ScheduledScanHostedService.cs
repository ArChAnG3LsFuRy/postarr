using Postarr.Data;

namespace Postarr.Services;

/// <summary>
/// Runs in the background for the lifetime of the app/service, triggering a full
/// library scan on the interval configured in Settings. This is what makes Postarr
/// notice new Plex content even without webhooks configured.
/// </summary>
public class ScheduledScanHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledScanHostedService> _logger;

    public ScheduledScanHostedService(IServiceScopeFactory scopeFactory, ILogger<ScheduledScanHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a few seconds to finish starting up before the first scan
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // A restart shouldn't trigger a fresh pass when one already ran recently. Previously every
        // service restart / reinstall kicked off a scan ~10s later, which made scanning feel
        // constant. Wait out whatever remains of the configured interval instead.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsRepository>().Get();
            var interval = Math.Clamp(settings.ScanIntervalMinutes, 5, 10080);
            if (settings.LastFullScanCompletedUtc is { } last)
            {
                var wait = last.AddMinutes(interval) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    _logger.LogInformation(
                        "Last scan was {Ago:F0} min ago — deferring the startup scan by {Wait:F0} min.",
                        (DateTime.UtcNow - last).TotalMinutes, wait.TotalMinutes);
                    await Task.Delay(wait, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) { return; }   // shutting down
        catch (Exception ex) { _logger.LogError(ex, "Startup scan deferral check failed; scanning now."); }

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalMinutes = 15;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var settingsRepo = scope.ServiceProvider.GetRequiredService<SettingsRepository>();
                // Floor avoids hammering APIs; ceiling keeps Task.Delay in range (it throws above
                // ~24.8 days, which used to escape this loop and kill scanning until a restart).
                intervalMinutes = Math.Clamp(settingsRepo.Get().ScanIntervalMinutes, 5, 10080);

                var scanService = scope.ServiceProvider.GetRequiredService<LibraryScanService>();
                // Scheduled scans honour the "Incremental scan only" setting (forceFull:false) so a
                // background pass stays cheap; only the manual Scan button forces a full re-enrich.
                var result = await scanService.RunFullScanAsync(forceFull: false, externalCt: stoppingToken);
                _logger.LogInformation("Scheduled scan finished: {Result}", result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // app shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled scan failed.");
            }

            // Never let a delay failure terminate the loop — if this throws, scanning stops for
            // the lifetime of the process.
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scan scheduler delay failed; retrying in 15 minutes.");
                try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
