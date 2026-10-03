using Microsoft.AspNetCore.SignalR;

namespace Postarr.Hubs;

/// <summary>
/// SignalR hub that pushes real-time scan progress to every connected browser tab.
/// The UI connects on load and receives events without polling.
/// </summary>
public class ScanHub : Hub
{
    // Clients subscribe to this hub; the server pushes via IScanNotifier (below).
    // No client→server methods needed: the UI only listens, never sends via SignalR.
}

/// <summary>
/// Injected into services that produce progress events; decouples them from SignalR directly.
/// </summary>
public interface IScanNotifier
{
    Task ItemScannedAsync(string title, string status, int processed, int total);
    Task PosterAppliedAsync(string title, string source, bool queued);
    Task BackgroundAppliedAsync(string title, string source, bool queued);
    Task ScanStartedAsync(int totalItems);
    Task ScanCompletedAsync(int newItems, int updatedItems, int errors);
    Task LogAsync(string level, string message);
}

public class SignalRScanNotifier : IScanNotifier
{
    private readonly IHubContext<ScanHub> _hub;
    public SignalRScanNotifier(IHubContext<ScanHub> hub) => _hub = hub;

    public Task ItemScannedAsync(string title, string status, int processed, int total) =>
        _hub.Clients.All.SendAsync("ItemScanned", new { title, status, processed, total });

    public Task PosterAppliedAsync(string title, string source, bool queued) =>
        _hub.Clients.All.SendAsync("PosterApplied", new { title, source, queued });

    public Task BackgroundAppliedAsync(string title, string source, bool queued) =>
        _hub.Clients.All.SendAsync("BackgroundApplied", new { title, source, queued });

    public Task ScanStartedAsync(int totalItems) =>
        _hub.Clients.All.SendAsync("ScanStarted", new { totalItems });

    public Task ScanCompletedAsync(int newItems, int updatedItems, int errors) =>
        _hub.Clients.All.SendAsync("ScanCompleted", new { newItems, updatedItems, errors });

    public Task LogAsync(string level, string message) =>
        _hub.Clients.All.SendAsync("LogEntry", new { level, message, timestamp = DateTime.UtcNow });
}
