using Postarr.Plex;
using Postarr.Services;
using Microsoft.AspNetCore.Mvc;

namespace Postarr.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly LibraryScanService _scanService;
    private readonly ActivityLogger _activityLogger;

    public WebhooksController(LibraryScanService scanService, ActivityLogger activityLogger)
    {
        _scanService = scanService;
        _activityLogger = activityLogger;
    }

    /// <summary>
    /// Plex webhook endpoint. Configure this URL (http://&lt;this-machine&gt;:5286/api/webhooks/plex)
    /// under Plex Settings > Webhooks (requires Plex Pass). Plex POSTs multipart/form-data
    /// with a "payload" field containing JSON - it does not send a plain JSON body.
    /// </summary>
    [HttpPost("plex")]
    public async Task<ActionResult> ReceivePlexWebhook()
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest("Expected multipart/form-data payload from Plex.");
        }

        var form = await Request.ReadFormAsync();
        var payloadJson = form["payload"].ToString();
        if (string.IsNullOrEmpty(payloadJson))
        {
            return BadRequest("Missing payload field.");
        }

        var payload = PlexWebhookPayload.Parse(payloadJson);
        if (payload == null)
        {
            return BadRequest("Could not parse webhook payload.");
        }

        // We only care about new library content; ignore playback events (media.play, etc.)
        if (payload.Event != "library.new" || payload.Metadata == null)
        {
            return Ok();
        }

        var meta = payload.Metadata;
        await _activityLogger.LogAsync($"Webhook received: library.new for '{meta.Title}' ({meta.Type})");

        // For new episodes, Plex's ratingKey points to the episode itself; what we actually
        // want to refresh is the parent show, identified by grandparentRatingKey.
        var isShow = meta.LibrarySectionType == "show";
        var targetRatingKey = (meta.Type == "episode" && meta.GrandparentRatingKey != null)
            ? meta.GrandparentRatingKey
            : meta.RatingKey;

        if (targetRatingKey != null)
        {
            // Don't block Plex's webhook delivery waiting on a scan; run it in the background.
            _ = Task.Run(() => _scanService.ScanSingleItemAsync(targetRatingKey, isShow));
        }

        return Ok();
    }
}
