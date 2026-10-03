using System.Text.Json;
using System.Text.Json.Serialization;

namespace Postarr.Plex;

/// <summary>
/// Plex sends webhook payloads as multipart/form-data with a "payload" field containing JSON.
/// Relevant events for us: library.new (new movie/episode added).
/// Docs: https://support.plex.tv/articles/115002267687-webhooks/
/// </summary>
public class PlexWebhookPayload
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("Metadata")]
    public PlexWebhookMetadata? Metadata { get; set; }

    public static PlexWebhookPayload? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PlexWebhookPayload>(json);
        }
        catch
        {
            return null;
        }
    }
}

public class PlexWebhookMetadata
{
    [JsonPropertyName("librarySectionType")]
    public string? LibrarySectionType { get; set; }

    [JsonPropertyName("ratingKey")]
    public string? RatingKey { get; set; }

    [JsonPropertyName("grandparentRatingKey")]
    public string? GrandparentRatingKey { get; set; } // for episodes, points to the show

    [JsonPropertyName("type")]
    public string? Type { get; set; } // movie, episode, season, show

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
