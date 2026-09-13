using System.Text.Json;

namespace NapMLS.NapCat;

/// <summary>
/// Parses OneBot 11 events with messagePostFormat = "array".
/// Extracts plain text by concatenating all "text" segments.
/// </summary>
public static class OneBotParser
{
    /// <summary>Parse a raw JSON string into a OneBotEvent.</summary>
    public static OneBotEvent? ParseEvent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OneBotEvent>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse the message field into a list of MessageSegments.</summary>
    public static List<MessageSegment> ParseMessage(JsonElement messageElement)
    {
        var segments = new List<MessageSegment>();
        if (messageElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in messageElement.EnumerateArray())
            {
                try
                {
                    var seg = JsonSerializer.Deserialize<MessageSegment>(item, JsonOptions);
                    if (seg != null) segments.Add(seg);
                }
                catch { /* skip malformed segment */ }
            }
        }
        else if (messageElement.ValueKind == JsonValueKind.String)
        {
            // Fallback: string format (shouldn't happen with array mode, but handle gracefully)
            segments.Add(new MessageSegment
            {
                Type = "text",
                Data = JsonSerializer.SerializeToElement(new { text = messageElement.GetString() })
            });
        }
        return segments;
    }

    /// <summary>Extract concatenated plain text from segments.</summary>
    public static string ExtractText(List<MessageSegment> segments)
    {
        return string.Concat(segments
            .Where(s => s.Type == "text")
            .Select(s => s.Text ?? ""));
    }

    /// <summary>Check if text starts with [MLS:...] prefix.</summary>
    public static bool IsMlsMessage(string text)
    {
        return text.StartsWith("[MLS:", StringComparison.Ordinal);
    }

    /// <summary>Parse sender info from event.</summary>
    public static SenderInfo? ParseSender(JsonElement senderElement)
    {
        try
        {
            return JsonSerializer.Deserialize<SenderInfo>(senderElement, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
