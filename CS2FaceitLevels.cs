using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2FaceitLevels;

public sealed class CS2FaceitLevels : BasePlugin, IPluginConfig<CS2FaceitLevelsConfig>
{
    public override string ModuleName => "CS2FaceitLevels";
    public override string ModuleAuthor => "✪ Stαr";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleDescription => "Shows real FACEIT levels in the CS2 scoreboard.";

    private static readonly HttpClient Http = new();
    private static readonly Regex ColorTokenRegex = new("\\{([A-Za-z_]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Lazy<Dictionary<string, string>> ChatColorTokens = new(BuildChatColorTokenMap);

    private const int ChallengerBadgeLevel = 11;
    private const int ChallengerRankLimit = 1000;
    private const int ChallengerPinId = 1010;

    private readonly ConcurrentDictionary<ulong, CachedFaceitData> _cache = new();
    private readonly ConcurrentDictionary<ulong, byte> _fetching = new();

    private CounterStrikeSharp.API.Modules.Timers.Timer? _reapplyTimer;

    public CS2FaceitLevelsConfig Config { get; set; } = new();

    private static readonly Dictionary<int, int> FaceitLevelToPin = new()
    {
        [1] = 1017,
        [2] = 1032,
        [3] = 1019,
        [4] = 1005,
        [5] = 1051,
        [6] = 1007,
        [7] = 1020,
        [8] = 1082,
        [9] = 1035,
        [10] = 1060,
        [ChallengerBadgeLevel] = ChallengerPinId,
    };

    public void OnConfigParsed(CS2FaceitLevelsConfig config)
    {
        Config = config;

        if (Config.CacheMinutes < 1)
            Config.CacheMinutes = 1;

        if (Config.RequestTimeoutSeconds < 2)
            Config.RequestTimeoutSeconds = 2;

        if (Config.ReapplyCachedPinSeconds < 5.0f)
            Config.ReapplyCachedPinSeconds = 5.0f;
    }

    public override void Load(bool hotReload)
    {

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        AddCommand("css_cs2faceitlevels_refresh", "Refresh and reapply FACEIT level pins for all connected players.", OnRefreshCommand);
        AddCommand("css_elo", "Privately show another player\'s FACEIT ELO by partial name.", OnEloCommand);
        AddCommand("css_elos", "Privately list all connected players\' FACEIT ELO.", OnElosCommand);

        _reapplyTimer = AddTimer(Config.ReapplyCachedPinSeconds, ReapplyCachedPins, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        if (hotReload)
        {
            AddTimer(2.0f, () => RefreshAllPlayers(force: true), TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    public override void Unload(bool hotReload)
    {
        _reapplyTimer?.Kill();
        _reapplyTimer = null;
        Logger.LogInformation("[CS2FaceitLevels] Unloaded.");
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsUsablePlayer(player))
            return HookResult.Continue;

        var slot = player.Slot;
        AddTimer(2.0f, () => RefreshPlayerBySlot(slot, force: false), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsUsablePlayer(player))
            return HookResult.Continue;

        var slot = player.Slot;
        AddTimer(0.2f, () => RefreshPlayerBySlot(slot, force: false), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !IsUsablePlayer(player))
            return HookResult.Continue;

        var slot = player.Slot;
        AddTimer(0.5f, () => RefreshPlayerBySlot(slot, force: false), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        AddTimer(1.0f, () => RefreshAllPlayers(force: false), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.SteamID != 0)
        {
            _fetching.TryRemove(player.SteamID, out _);
        }

        return HookResult.Continue;
    }

    private void OnEloCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!IsUsablePlayer(caller))
        {
            command.ReplyToCommand("[CS2FaceitLevels] This command is player-only.");
            return;
        }

        if (command.ArgCount < 2)
        {
            caller.PrintToChat(ReplaceChatColorTags(Config.MissingPlayerNameMessage));
            return;
        }

        var search = GetJoinedArguments(command, 1);
        if (string.IsNullOrWhiteSpace(search))
        {
            caller.PrintToChat(ReplaceChatColorTags(Config.MissingPlayerNameMessage));
            return;
        }

        var matches = Utilities.GetPlayers()
            .Where(IsUsablePlayer)
            .Where(p => p.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PlayerName)
            .ToList();

        if (matches.Count == 0)
        {
            caller.PrintToChat(ReplaceChatColorTags(Config.NoPlayerFoundMessage.Replace("{SEARCH}", search, StringComparison.OrdinalIgnoreCase)));
            return;
        }

        if (matches.Count > 1)
        {
            var names = string.Join(", ", matches.Take(5).Select(p => p.PlayerName));
            caller.PrintToChat(ReplaceChatColorTags(Config.MultiplePlayersFoundMessage.Replace("{PLAYERS}", names, StringComparison.OrdinalIgnoreCase)));
            return;
        }

        var target = PlayerSnapshot.From(matches[0]);
        var callerSlot = caller.Slot;

        _ = Task.Run(async () =>
        {
            var data = await GetOrFetchFaceitDataAsync(target.SteamId, force: false);
            Server.NextFrame(() =>
            {
                var currentCaller = Utilities.GetPlayerFromSlot(callerSlot);
                if (!IsUsablePlayer(currentCaller))
                    return;

                currentCaller.PrintToChat(RenderEloChatLine(Config.SingleEloChatFormat, target, data));
            });
        });
    }

    private void OnElosCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!IsUsablePlayer(caller))
        {
            command.ReplyToCommand("[CS2FaceitLevels] This command is player-only.");
            return;
        }

        var players = Utilities.GetPlayers()
            .Where(IsUsablePlayer)
            .OrderBy(p => p.TeamNum)
            .ThenBy(p => p.PlayerName)
            .Select(PlayerSnapshot.From)
            .ToList();

        var callerSlot = caller.Slot;

        _ = Task.Run(async () =>
        {
            var results = await Task.WhenAll(players.Select(async player =>
                new PlayerFaceitResult(player, await GetOrFetchFaceitDataAsync(player.SteamId, force: false))));

            Server.NextFrame(() =>
            {
                var currentCaller = Utilities.GetPlayerFromSlot(callerSlot);
                if (!IsUsablePlayer(currentCaller))
                    return;

                foreach (var result in results)
                {
                    currentCaller.PrintToChat(RenderEloChatLine(Config.AllElosChatFormat, result.Player, result.Data));
                }
            });
        });
    }

    private void OnRefreshCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player != null)
        {
            command.ReplyToCommand("[CS2FaceitLevels] This command can only be used from server console.");
            return;
        }

        RefreshAllPlayers(force: true);
        command.ReplyToCommand("[CS2FaceitLevels] Refresh started.");
    }

    private void RefreshAllPlayers(bool force)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsUsablePlayer(player))
                continue;

            RefreshPlayerBySlot(player.Slot, force);
        }
    }

    private void ReapplyCachedPins()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsUsablePlayer(player))
                continue;

            if (_cache.TryGetValue(player.SteamID, out var cached) && cached.Level is >= 1 and <= ChallengerBadgeLevel)
            {
                ApplyFaceitLevel(player, cached.Level.Value);
            }
        }
    }

    private void RefreshPlayerBySlot(int slot, bool force)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (player == null || !IsUsablePlayer(player))
            return;

        var steamId = player.SteamID;
        if (steamId == 0)
            return;

        if (!force && _cache.TryGetValue(steamId, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                if (cached.Level is >= 1 and <= ChallengerBadgeLevel)
                    ApplyFaceitLevel(player, cached.Level.Value);
                else if (Config.ClearPinWhenNoFaceit)
                    ClearPin(player);

                return;
            }
        }

        if (!_fetching.TryAdd(steamId, 1))
            return;

        var playerName = player.PlayerName;
        var slotAtStart = player.Slot;

        _ = Task.Run(async () =>
        {
            int? level = null;
            try
            {
                var data = await FetchFaceitDataAsync(steamId);
                level = data?.Level;
                var expires = DateTimeOffset.UtcNow.AddMinutes(Config.CacheMinutes);
                _cache[steamId] = new CachedFaceitData(level, data?.SkillLevel, data?.Elo, expires);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[CS2FaceitLevels] FACEIT lookup failed for {PlayerName} ({SteamId}).", playerName, steamId);
            }
            finally
            {
                _fetching.TryRemove(steamId, out _);
            }

            Server.NextFrame(() =>
            {
                var currentPlayer = Utilities.GetPlayerFromSlot(slotAtStart);
                if (currentPlayer == null || !IsUsablePlayer(currentPlayer) || currentPlayer.SteamID != steamId)
                    return;

                if (level is >= 1 and <= ChallengerBadgeLevel)
                {
                    ApplyFaceitLevel(currentPlayer, level.Value);
                }
                else if (Config.ClearPinWhenNoFaceit)
                {
                    ClearPin(currentPlayer);
                }
            });
        });
    }

    private async Task<FaceitPlayerData?> FetchFaceitDataAsync(ulong steamId)
    {
        if (string.IsNullOrWhiteSpace(Config.FaceitApiKey) || Config.FaceitApiKey == "PUT_YOUR_FACEIT_API_KEY_HERE")
        {
            if (Config.Debug)
                Logger.LogWarning("[CS2FaceitLevels] FACEIT API key is empty.");
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://open.faceit.com/data/v4/players?game=cs2&game_player_id={steamId}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.FaceitApiKey);
        request.Headers.UserAgent.ParseAdd("CS2FaceitLevels-CSSharp/1.0");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (Config.Debug)
                Logger.LogInformation("[CS2FaceitLevels] No FACEIT profile for SteamID {SteamId}.", steamId);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("[CS2FaceitLevels] FACEIT API returned {StatusCode} for SteamID {SteamId}.", (int)response.StatusCode, steamId);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

        if (!json.RootElement.TryGetProperty("games", out var games))
            return null;

        if (!games.TryGetProperty("cs2", out var cs2))
            return null;

        if (!cs2.TryGetProperty("skill_level", out var skillLevelElement))
            return null;

        if (!skillLevelElement.TryGetInt32(out var skillLevel))
            return null;

        if (skillLevel is < 1 or > 10)
            return null;

        int? elo = null;
        if (cs2.TryGetProperty("faceit_elo", out var eloElement) && eloElement.TryGetInt32(out var eloValue))
            elo = eloValue;

        var level = skillLevel;

        if (level != 10)
            return new FaceitPlayerData(level, skillLevel, elo);

        if (!json.RootElement.TryGetProperty("player_id", out var playerIdElement))
            return new FaceitPlayerData(level, skillLevel, elo);

        var playerId = playerIdElement.GetString();
        if (string.IsNullOrWhiteSpace(playerId))
            return new FaceitPlayerData(level, skillLevel, elo);

        string? region = null;
        if (cs2.TryGetProperty("region", out var regionElement))
            region = regionElement.GetString();

        if (string.IsNullOrWhiteSpace(region))
        {
            if (Config.Debug)
                Logger.LogInformation("[CS2FaceitLevels] FACEIT level 10 player {SteamId} has no CS2 region in API response.", steamId);
            return new FaceitPlayerData(level, skillLevel, elo);
        }

        var isChallenger = await IsFaceitChallengerAsync(playerId, region);
        return new FaceitPlayerData(isChallenger ? ChallengerBadgeLevel : level, skillLevel, elo);
    }

    private async Task<bool> IsFaceitChallengerAsync(string playerId, string region)
    {
        try
        {
            var safeRegion = Uri.EscapeDataString(region);
            var safePlayerId = Uri.EscapeDataString(playerId);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://open.faceit.com/data/v4/rankings/games/cs2/regions/{safeRegion}/players/{safePlayerId}");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.FaceitApiKey);
            request.Headers.UserAgent.ParseAdd("CS2FaceitLevels-CSSharp/1.0");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                if (Config.Debug)
                    Logger.LogInformation("[CS2FaceitLevels] FACEIT ranking API returned {StatusCode} for player {PlayerId} in region {Region}.", (int)response.StatusCode, playerId, region);
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

            if (!TryReadRankingPosition(json.RootElement, out var position))
                return false;

            var isChallenger = position is > 0 and <= ChallengerRankLimit;

            if (Config.Debug)
            {
                Logger.LogInformation("[CS2FaceitLevels] FACEIT ranking for player {PlayerId} in region {Region}: {Position}. Challenger={IsChallenger}.", playerId, region, position, isChallenger);
            }

            return isChallenger;
        }
        catch (Exception ex)
        {
            if (Config.Debug)
                Logger.LogWarning(ex, "[CS2FaceitLevels] FACEIT challenger lookup failed for player {PlayerId} in region {Region}.", playerId, region);
            return false;
        }
    }

    private static bool TryReadRankingPosition(JsonElement element, out int position)
    {
        position = 0;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPositiveIntProperty(element, "position", out position))
            return true;

        if (TryGetPositiveIntProperty(element, "rank", out position))
            return true;

        if (TryGetPositiveIntProperty(element, "ranking", out position))
            return true;

        if (element.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var first = items[0];

            if (TryGetPositiveIntProperty(first, "position", out position))
                return true;

            if (TryGetPositiveIntProperty(first, "rank", out position))
                return true;

            if (TryGetPositiveIntProperty(first, "ranking", out position))
                return true;
        }

        return false;
    }

    private static bool TryGetPositiveIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = 0;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return value > 0;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return value > 0;

        return false;
    }

    private async Task<CachedFaceitData?> GetOrFetchFaceitDataAsync(ulong steamId, bool force)
    {
        if (!force && _cache.TryGetValue(steamId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;

        FaceitPlayerData? data = null;
        try
        {
            data = await FetchFaceitDataAsync(steamId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[CS2FaceitLevels] FACEIT lookup failed for SteamID {SteamId}.", steamId);
        }

        var cachedData = new CachedFaceitData(data?.Level, data?.SkillLevel, data?.Elo, DateTimeOffset.UtcNow.AddMinutes(Config.CacheMinutes));
        _cache[steamId] = cachedData;
        return cachedData;
    }

    private string RenderEloChatLine(string template, PlayerSnapshot player, CachedFaceitData? data)
    {
        var elo = data?.Elo?.ToString() ?? Config.NoFaceitText;
        var level = data?.SkillLevel?.ToString() ?? Config.NoFaceitText;
        var eloColor = GetEloColor(data?.SkillLevel);

        var line = template
            .Replace("{PLAYER_COLOR}", Config.PlayerNameColor, StringComparison.OrdinalIgnoreCase)
            .Replace("{LABEL_COLOR}", Config.EloLabelColor, StringComparison.OrdinalIgnoreCase)
            .Replace("{ELO_COLOR}", eloColor, StringComparison.OrdinalIgnoreCase)
            .Replace("{PLAYER}", player.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{STEAMID64}", player.SteamId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{ELO}", elo, StringComparison.OrdinalIgnoreCase)
            .Replace("{LEVEL}", level, StringComparison.OrdinalIgnoreCase);

        return ReplaceChatColorTags(line);
    }

    private string GetEloColor(int? skillLevel)
    {
        return skillLevel switch
        {
            1 => Config.Level1EloColor,
            2 => Config.Level2EloColor,
            3 => Config.Level3EloColor,
            4 => Config.Level4EloColor,
            5 => Config.Level5EloColor,
            6 => Config.Level6EloColor,
            7 => Config.Level7EloColor,
            8 => Config.Level8EloColor,
            9 => Config.Level9EloColor,
            10 => Config.Level10EloColor,
            _ => Config.NoFaceitColor
        };
    }

    private static string ReplaceChatColorTags(string message)
    {
        return ColorTokenRegex.Replace(message, match =>
        {
            var key = NormalizeColorKey(match.Groups[1].Value);
            return ChatColorTokens.Value.TryGetValue(key, out var color) ? color : match.Value;
        });
    }

    private static Dictionary<string, string> BuildChatColorTokenMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(ChatColors).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(char))
                continue;

            var value = ((char)(field.GetValue(null) ?? '\x01')).ToString();
            map[NormalizeColorKey(field.Name)] = value;
        }

        if (map.TryGetValue("grey", out var grey))
            map.TryAdd("gray", grey);

        return map;
    }

    private static string NormalizeColorKey(string key)
    {
        return key.Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string GetJoinedArguments(CommandInfo command, int startIndex)
    {
        var parts = new List<string>();
        for (var i = startIndex; i < command.ArgCount; i++)
        {
            var part = command.ArgByIndex(i);
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part);
        }

        return string.Join(" ", parts).Trim();
    }

    private void ApplyFaceitLevel(CCSPlayerController player, int level)
    {
        if (!IsUsablePlayer(player))
            return;

        if (!FaceitLevelToPin.TryGetValue(level, out var pinId))
            return;

        var inventoryServices = player.InventoryServices;
        if (inventoryServices == null)
            return;

        inventoryServices.Rank[5] = (MedalRank_t)pinId;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");

        if (Config.Debug)
        {
            Logger.LogInformation("[CS2FaceitLevels] Applied FACEIT badge level {Level} pin {PinId} to {PlayerName}.", level, pinId, player.PlayerName);
        }
    }

    private void ClearPin(CCSPlayerController player)
    {
        if (!IsUsablePlayer(player) || player.InventoryServices == null)
            return;

        player.InventoryServices.Rank[5] = MedalRank_t.MEDAL_RANK_NONE;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
    }

    private static bool IsUsablePlayer([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot && player.Connected == PlayerConnectedState.Connected && player.SteamID != 0;
    }

    private sealed record CachedFaceitData(int? Level, int? SkillLevel, int? Elo, DateTimeOffset ExpiresAt);
    private sealed record FaceitPlayerData(int? Level, int? SkillLevel, int? Elo);
    private sealed record PlayerSnapshot(int Slot, ulong SteamId, string PlayerName)
    {
        public static PlayerSnapshot From(CCSPlayerController player) => new(player.Slot, player.SteamID, player.PlayerName);
    }
    private sealed record PlayerFaceitResult(PlayerSnapshot Player, CachedFaceitData? Data);
}
