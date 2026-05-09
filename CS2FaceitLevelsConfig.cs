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


    [JsonPropertyName("single_elo_chat_format")]
    public string SingleEloChatFormat { get; set; } = "- {PLAYER_COLOR}{PLAYER}{DEFAULT} {LABEL_COLOR}Elo{DEFAULT}: {ELO_COLOR}{ELO}{DEFAULT}";

    [JsonPropertyName("all_elos_chat_format")]
    public string AllElosChatFormat { get; set; } = "- {PLAYER_COLOR}{PLAYER}{DEFAULT} {LABEL_COLOR}Elo{DEFAULT}: {ELO_COLOR}{ELO}{DEFAULT}";

    [JsonPropertyName("missing_player_name_message")]
    public string MissingPlayerNameMessage { get; set; } = "{red}[CS2FaceitLevels] You need player name {red}< !elo {playername} >";

    [JsonPropertyName("no_player_found_message")]
    public string NoPlayerFoundMessage { get; set; } = "{red}[CS2FaceitLevels] No player found matching {default}{SEARCH}";

    [JsonPropertyName("multiple_players_found_message")]
    public string MultiplePlayersFoundMessage { get; set; } = "{red}[CS2FaceitLevels] Multiple players found: {default}{PLAYERS}";

    [JsonPropertyName("player_name_color")]
    public string PlayerNameColor { get; set; } = "{RED}";

    [JsonPropertyName("elo_label_color")]
    public string EloLabelColor { get; set; } = "{LIGHTPURPLE}";

    [JsonPropertyName("no_faceit_text")]
    public string NoFaceitText { get; set; } = "N/A";

    [JsonPropertyName("no_faceit_color")]
    public string NoFaceitColor { get; set; } = "{GREY}";

    [JsonPropertyName("level_1_elo_color")]
    public string Level1EloColor { get; set; } = "{GREY}";

    [JsonPropertyName("level_2_elo_color")]
    public string Level2EloColor { get; set; } = "{GREEN}";

    [JsonPropertyName("level_3_elo_color")]
    public string Level3EloColor { get; set; } = "{GREEN}";

    [JsonPropertyName("level_4_elo_color")]
    public string Level4EloColor { get; set; } = "{YELLOW}";

    [JsonPropertyName("level_5_elo_color")]
    public string Level5EloColor { get; set; } = "{YELLOW}";

    [JsonPropertyName("level_6_elo_color")]
    public string Level6EloColor { get; set; } = "{YELLOW}";

    [JsonPropertyName("level_7_elo_color")]
    public string Level7EloColor { get; set; } = "{YELLOW}";

    [JsonPropertyName("level_8_elo_color")]
    public string Level8EloColor { get; set; } = "{ORANGE}";

    [JsonPropertyName("level_9_elo_color")]
    public string Level9EloColor { get; set; } = "{ORANGE}";

    [JsonPropertyName("level_10_elo_color")]
    public string Level10EloColor { get; set; } = "{RED}";
}
