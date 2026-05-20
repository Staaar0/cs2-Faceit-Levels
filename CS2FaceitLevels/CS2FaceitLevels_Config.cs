using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace CS2FaceitLevels;

public sealed class CS2FaceitLevelsConfig : BasePluginConfig
{
    [JsonPropertyName("faceit_api_key")]
    public string FaceitApiKey { get; set; } = "PUT_YOUR_FACEIT_API_KEY_HERE";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

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

    [JsonPropertyName("enable_elo_commands")]
    public bool EnableEloCommands { get; set; } = true;
}

public sealed class CS2FaceitLevelsLang
{
    [JsonPropertyName("chat_prefix")]
    public string ChatPrefix { get; set; } = "[{RED}CS2FaceitLevels{DEFAULT}]";
    
    [JsonPropertyName("single_elo_chat_format")]
    public string SingleEloChatFormat { get; set; } = "- {PLAYER_COLOR}{PLAYER}{DEFAULT} {LABEL_COLOR}Elo{DEFAULT}: {ELO_COLOR}{ELO}{DEFAULT}";

    [JsonPropertyName("all_elos_chat_format")]
    public string AllElosChatFormat { get; set; } = "- {PLAYER_COLOR}{PLAYER}{DEFAULT} {LABEL_COLOR}Elo{DEFAULT}: {ELO_COLOR}{ELO}{DEFAULT}";

    [JsonPropertyName("missing_player_name_message")]
    public string MissingPlayerNameMessage { get; set; } = "{PREFIX} You need player name {RED}< !elo {playername} >";

    [JsonPropertyName("no_player_found_message")]
    public string NoPlayerFoundMessage { get; set; } = "{PREFIX} No player found matching {RED}{SEARCH}";

    [JsonPropertyName("multiple_players_found_message")]
    public string MultiplePlayersFoundMessage { get; set; } = "{PREFIX} Multiple players found: {RED}{PLAYERS}";

    [JsonPropertyName("player_only_message")]
    public string PlayerOnlyMessage { get; set; } = "{PREFIX} This command is player-only.";
}
