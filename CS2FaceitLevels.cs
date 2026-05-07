using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    public override string ModuleVersion => "1.0.2";
    public override string ModuleDescription => "Shows real FACEIT levels in the CS2 scoreboard.";

    private static readonly HttpClient Http = new();

    private const int ChallengerBadgeLevel = 11;
    private const int ChallengerRankLimit = 1000;
    private const int ChallengerPinId = 1010;

    private readonly ConcurrentDictionary<ulong, CachedFaceitLevel> _cache = new();
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
                level = await FetchFaceitLevelAsync(steamId);
                var expires = DateTimeOffset.UtcNow.AddMinutes(Config.CacheMinutes);
                _cache[steamId] = new CachedFaceitLevel(level, expires);
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

    private async Task<int?> FetchFaceitLevelAsync(ulong steamId)
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

        if (!skillLevelElement.TryGetInt32(out var level))
            return null;

        if (level is < 1 or > 10)
            return null;

        if (level != 10)
            return level;

        if (!json.RootElement.TryGetProperty("player_id", out var playerIdElement))
            return level;

        var playerId = playerIdElement.GetString();
        if (string.IsNullOrWhiteSpace(playerId))
            return level;

        string? region = null;
        if (cs2.TryGetProperty("region", out var regionElement))
            region = regionElement.GetString();

        if (string.IsNullOrWhiteSpace(region))
        {
            if (Config.Debug)
                Logger.LogInformation("[CS2FaceitLevels] FACEIT level 10 player {SteamId} has no CS2 region in API response.", steamId);
            return level;
        }

        var isChallenger = await IsFaceitChallengerAsync(playerId, region);
        return isChallenger ? ChallengerBadgeLevel : level;
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

    private void ApplyFaceitLevel    private void ApplyFaceitLevel(CCSPlayerController player, int level)
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

    private static bool IsUsablePlayer(CCSPlayerController player)
    {
        return player.IsValid && !player.IsBot && player.Connected == PlayerConnectedState.Connected && player.SteamID != 0;
    }

    private sealed record CachedFaceitLevel(int? Level, DateTimeOffset ExpiresAt);
}
