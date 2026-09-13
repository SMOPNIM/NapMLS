using System.Text.Json;
using System.Text.Json.Serialization;

namespace NapMLS.NapCat;

// ── OneBot 11 Event Models (array messagePostFormat) ──

public sealed class OneBotEvent
{
    [JsonPropertyName("time")]
    public long Time { get; set; }

    [JsonPropertyName("self_id")]
    public long SelfId { get; set; }

    [JsonPropertyName("post_type")]
    public string PostType { get; set; } = "";

    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    [JsonPropertyName("sub_type")]
    public string? SubType { get; set; }

    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("group_id")]
    public long? GroupId { get; set; }

    [JsonPropertyName("message")]
    public JsonElement Message { get; set; }

    [JsonPropertyName("raw_message")]
    public string? RawMessage { get; set; }

    [JsonPropertyName("sender")]
    public JsonElement Sender { get; set; }

    [JsonPropertyName("meta_event_type")]
    public string? MetaEventType { get; set; }

    [JsonPropertyName("status")]
    public JsonElement? Status { get; set; }

    [JsonPropertyName("interval")]
    public long? Interval { get; set; }
}

// ── Message Segment (array format) ──

public sealed class MessageSegment
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    /// <summary>Extract text from a "text" segment.</summary>
    public string? Text => Type == "text" && Data.TryGetProperty("text", out var t)
        ? t.GetString()
        : null;

    /// <summary>Extract QQ from an "at" segment.</summary>
    public long? AtQq => Type == "at" && Data.TryGetProperty("qq", out var qq)
        ? qq.GetInt64()
        : null;
}

// ── Sender Info ──

public sealed class SenderInfo
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("card")]
    public string? Card { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

// ── API Request Models ──

public sealed class SendGroupMsgRequest
{
    [JsonPropertyName("group_id")]
    public long GroupId { get; set; }

    [JsonPropertyName("message")]
    public List<MessageSegment> Message { get; set; } = [];

    [JsonPropertyName("auto_escape")]
    public bool AutoEscape { get; set; }
}

public sealed class SendPrivateMsgRequest
{
    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("message")]
    public List<MessageSegment> Message { get; set; } = [];

    [JsonPropertyName("auto_escape")]
    public bool AutoEscape { get; set; }
}

// ── API Response ──

public sealed class OneBotResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("retcode")]
    public int RetCode { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("wording")]
    public string? Wording { get; set; }
}

// ── API Call Wrapper ──

public sealed class OneBotApiCall
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("echo")]
    public string? Echo { get; set; }
}
