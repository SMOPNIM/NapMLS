using System.Text.Json.Serialization;

namespace NapMLS.UI.Models;

/// <summary>
/// Persistent app config stored in config.json.
/// Contains NapCat connection info and identity fingerprint.
/// Does NOT contain secrets (master key, private keys).
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("risk_warning_accepted")]
    public bool RiskWarningAccepted { get; set; }

    [JsonPropertyName("napcat_host")]
    public string NapCatHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("napcat_port")]
    public int NapCatPort { get; set; } = 8080;

    [JsonPropertyName("napcat_token")]
    public string? NapCatToken { get; set; }

    [JsonPropertyName("identity_username")]
    public string? IdentityUsername { get; set; }

    [JsonPropertyName("identity_fingerprint")]
    public string? IdentityFingerprint { get; set; }

    [JsonPropertyName("identity_safety_code")]
    public string? IdentitySafetyCode { get; set; }
}
