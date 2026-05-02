using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CS2FaceitLevels;

public sealed class CS2FaceitLevelsConfig : BasePluginConfig
{
    [JsonPropertyName("faceit_api_key")]
    public string FaceitApiKey { get; set; } = "PUT_YOUR_FACEIT_API_KEY_HERE";

    [JsonPropertyName("debug")]
    public bool Debug { get; set; } = false;

    [JsonPropertyName("cache_minutes")]
    public int CacheMinutes { get; set; } = 30;

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("reapply_cached_pin_seconds")]
    public float ReapplyCachedPinSeconds { get; set; } = 15.0f;

    [JsonPropertyName("clear_pin_when_no_faceit")]
    public bool ClearPinWhenNoFaceit { get; set; } = false;
}
