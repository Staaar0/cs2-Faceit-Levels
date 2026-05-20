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
    public override string ModuleVersion => "1.0.4";
    public override string ModuleDescription => "Shows real FACEIT levels in the CS2 scoreboard.";

    private const int ChallengerBadgeLevel = 11;
    private const int ChallengerRankLimit = 1000;
    private const int ChallengerPinId = 1010;

    private const string PlayerNameColor = "{RED}";
    private const string EloLabelColor = "{LIGHTPURPLE}";
    private const string NoFaceitText = "N/A";
    private const string NoFaceitColor = "{GREY}";

    private static readonly HttpClient Http = new();
    private static readonly Regex ColorTokenRegex = new("\\{([A-Za-z_]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Lazy<Dictionary<string, string>> ChatColorTokens = new(BuildChatColorTokenMap);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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

    private readonly ConcurrentDictionary<ulong, CachedFaceitData> _cache = new();
    private readonly ConcurrentDictionary<ulong, byte> _fetching = new();

    private CounterStrikeSharp.API.Modules.Timers.Timer? _reapplyTimer;
    private CS2FaceitLevelsLang Lang { get; set; } = new();

    public CS2FaceitLevelsConfig Config { get; set; } = new();

    public void OnConfigParsed(CS2FaceitLevelsConfig config)
    {
        Config = NormalizeConfig(config);
        AddLanguageNoteToGeneratedConfig();
        Lang = LoadLanguage(Config.Language);
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventPlayerConnectFull>((@event, info) => QueueRefresh(@event.Userid, 2.0f));
        RegisterEventHandler<EventPlayerSpawn>((@event, info) => QueueRefresh(@event.Userid, 0.2f));
        RegisterEventHandler<EventPlayerTeam>((@event, info) => QueueRefresh(@event.Userid, 0.5f));
        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            AddTimer(1.0f, () => RefreshAllPlayers(force: false), TimerFlags.STOP_ON_MAPCHANGE);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            var player = @event.Userid;
            if (player != null && player.SteamID != 0)
                _fetching.TryRemove(player.SteamID, out _);

            return HookResult.Continue;
        });

        AddCommand("css_cs2faceitlevels_refresh", "Refresh and reapply FACEIT level pins for all connected players.", OnRefreshCommand);

        if (Config.EnableEloCommands)
        {
            AddCommand("css_elo", "Privately show another player's FACEIT ELO by partial name.", OnEloCommand);
            AddCommand("css_elos", "Privately list all connected players' FACEIT ELO.", OnElosCommand);
        }

        _reapplyTimer = AddTimer(Config.ReapplyCachedPinSeconds, ReapplyCachedPins, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        if (hotReload)
            AddTimer(2.0f, () => RefreshAllPlayers(force: true), TimerFlags.STOP_ON_MAPCHANGE);
    }

    public override void Unload(bool hotReload)
    {
        _reapplyTimer?.Kill();
        _reapplyTimer = null;
        Logger.LogInformation("[CS2FaceitLevels] Unloaded.");
    }

    private static CS2FaceitLevelsConfig NormalizeConfig(CS2FaceitLevelsConfig config)
    {
        config.CacheMinutes = Math.Max(config.CacheMinutes, 1);
        config.RequestTimeoutSeconds = Math.Max(config.RequestTimeoutSeconds, 2);
        config.ReapplyCachedPinSeconds = Math.Max(config.ReapplyCachedPinSeconds, 5.0f);

        if (string.IsNullOrWhiteSpace(config.Language))
            config.Language = "en";

        return config;
    }

    private HookResult QueueRefresh(CCSPlayerController? player, float delay)
    {
        if (IsUsablePlayer(player))
        {
            var slot = player.Slot;
            AddTimer(delay, () => RefreshPlayerBySlot(slot, force: false), TimerFlags.STOP_ON_MAPCHANGE);
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

    private void OnEloCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!IsUsablePlayer(caller))
        {
            command.ReplyToCommand(RenderMessage(Lang.PlayerOnlyMessage));
            return;
        }

        var search = GetJoinedArguments(command, 1);
        if (string.IsNullOrWhiteSpace(search))
        {
            caller.PrintToChat(RenderMessage(Lang.MissingPlayerNameMessage));
            return;
        }

        var matches = OnlinePlayers()
            .Where(player => player.PlayerName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(player => player.PlayerName)
            .ToList();

        switch (matches.Count)
        {
            case 0:
                caller.PrintToChat(RenderMessage(Lang.NoPlayerFoundMessage, new() { ["SEARCH"] = search }));
                return;

            case > 1:
                caller.PrintToChat(RenderMessage(Lang.MultiplePlayersFoundMessage, new()
                {
                    ["PLAYERS"] = string.Join(", ", matches.Take(5).Select(player => player.PlayerName))
                }));
                return;
        }

        var callerSlot = caller.Slot;
        var target = PlayerSnapshot.From(matches[0]);

        _ = Task.Run(async () =>
        {
            var data = await GetOrFetchFaceitDataAsync(target.SteamId, force: false);
            Server.NextFrame(() =>
            {
                var currentCaller = Utilities.GetPlayerFromSlot(callerSlot);
                if (IsUsablePlayer(currentCaller))
                    currentCaller.PrintToChat(RenderEloLine(Lang.SingleEloChatFormat, target, data));
            });
        });
    }

    private void OnElosCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (!IsUsablePlayer(caller))
        {
            command.ReplyToCommand(RenderMessage(Lang.PlayerOnlyMessage));
            return;
        }

        var callerSlot = caller.Slot;
        var players = OnlinePlayers()
            .OrderBy(player => player.TeamNum)
            .ThenBy(player => player.PlayerName)
            .Select(PlayerSnapshot.From)
            .ToList();

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
                    currentCaller.PrintToChat(RenderEloLine(Lang.AllElosChatFormat, result.Player, result.Data));
            });
        });
    }

    private void RefreshAllPlayers(bool force)
    {
        foreach (var player in OnlinePlayers())
            RefreshPlayerBySlot(player.Slot, force);
    }

    private void ReapplyCachedPins()
    {
        foreach (var player in OnlinePlayers())
        {
            if (_cache.TryGetValue(player.SteamID, out var cached) && cached.Level is >= 1 and <= ChallengerBadgeLevel)
                ApplyFaceitLevel(player, cached.Level.Value);
        }
    }

    private void RefreshPlayerBySlot(int slot, bool force)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (!IsUsablePlayer(player))
            return;

        var steamId = player.SteamID;
        var cached = GetValidCache(steamId, force);
        if (cached != null)
        {
            ApplyCachedResult(player, cached);
            return;
        }

        if (!_fetching.TryAdd(steamId, 1))
            return;

        var playerName = player.PlayerName;
        _ = Task.Run(async () => await RefreshPlayerAsync(slot, steamId, playerName));
    }

    private async Task RefreshPlayerAsync(int slot, ulong steamId, string playerName)
    {
        CachedFaceitData? cached = null;

        try
        {
            var data = await FetchFaceitDataAsync(steamId);
            cached = CacheFaceitData(steamId, data);
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
            var player = Utilities.GetPlayerFromSlot(slot);
            if (IsUsablePlayer(player) && player.SteamID == steamId)
                ApplyCachedResult(player, cached);
        });
    }

    private CachedFaceitData? GetValidCache(ulong steamId, bool force)
    {
        return !force && _cache.TryGetValue(steamId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow
            ? cached
            : null;
    }

    private CachedFaceitData CacheFaceitData(ulong steamId, FaceitPlayerData? data)
    {
        var cached = new CachedFaceitData(data?.Level, data?.SkillLevel, data?.Elo, DateTimeOffset.UtcNow.AddMinutes(Config.CacheMinutes));
        _cache[steamId] = cached;
        return cached;
    }

    private void ApplyCachedResult(CCSPlayerController player, CachedFaceitData? cached)
    {
        if (cached?.Level is >= 1 and <= ChallengerBadgeLevel)
            ApplyFaceitLevel(player, cached.Level.Value);
        else if (Config.ClearPinWhenNoFaceit)
            ClearPin(player);
    }

    private async Task<CachedFaceitData?> GetOrFetchFaceitDataAsync(ulong steamId, bool force)
    {
        var cached = GetValidCache(steamId, force);
        if (cached != null)
            return cached;

        try
        {
            return CacheFaceitData(steamId, await FetchFaceitDataAsync(steamId));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[CS2FaceitLevels] FACEIT lookup failed for SteamID {SteamId}.", steamId);
            return CacheFaceitData(steamId, null);
        }
    }

    private async Task<FaceitPlayerData?> FetchFaceitDataAsync(ulong steamId)
    {
        if (string.IsNullOrWhiteSpace(Config.FaceitApiKey) || Config.FaceitApiKey == "PUT_YOUR_FACEIT_API_KEY_HERE")
        {
            if (Config.Debug)
                Logger.LogWarning("[CS2FaceitLevels] FACEIT API key is empty.");

            return null;
        }

        using var request = CreateFaceitRequest($"https://open.faceit.com/data/v4/players?game=cs2&game_player_id={steamId}");
        using var cts = CreateRequestTimeout();
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

        return await ParseFaceitPlayerDataAsync(json.RootElement, steamId);
    }

    private async Task<FaceitPlayerData?> ParseFaceitPlayerDataAsync(JsonElement root, ulong steamId)
    {
        if (!TryGetCs2Stats(root, out var cs2) || !TryReadSkillLevel(cs2, out var skillLevel))
            return null;

        var elo = TryReadInt(cs2, "faceit_elo", out var eloValue) ? eloValue : (int?)null;
        var level = skillLevel;

        if (skillLevel != 10)
            return new FaceitPlayerData(level, skillLevel, elo);

        if (!TryReadString(root, "player_id", out var playerId))
            return new FaceitPlayerData(level, skillLevel, elo);

        if (!TryReadString(cs2, "region", out var region))
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
            var url = $"https://open.faceit.com/data/v4/rankings/games/cs2/regions/{Uri.EscapeDataString(region)}/players/{Uri.EscapeDataString(playerId)}";
            using var request = CreateFaceitRequest(url);
            using var cts = CreateRequestTimeout();
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
                Logger.LogInformation("[CS2FaceitLevels] FACEIT ranking for player {PlayerId} in region {Region}: {Position}. Challenger={IsChallenger}.", playerId, region, position, isChallenger);

            return isChallenger;
        }
        catch (Exception ex)
        {
            if (Config.Debug)
                Logger.LogWarning(ex, "[CS2FaceitLevels] FACEIT challenger lookup failed for player {PlayerId} in region {Region}.", playerId, region);

            return false;
        }
    }

    private HttpRequestMessage CreateFaceitRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.FaceitApiKey);
        request.Headers.UserAgent.ParseAdd("CS2FaceitLevels-CSSharp/1.0");
        return request;
    }

    private CancellationTokenSource CreateRequestTimeout()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(Config.RequestTimeoutSeconds));
    }

    private static bool TryGetCs2Stats(JsonElement root, out JsonElement cs2)
    {
        cs2 = default;
        return root.TryGetProperty("games", out var games) && games.TryGetProperty("cs2", out cs2);
    }

    private static bool TryReadSkillLevel(JsonElement cs2, out int skillLevel)
    {
        skillLevel = 0;
        return TryReadInt(cs2, "skill_level", out skillLevel) && skillLevel is >= 1 and <= 10;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static bool TryReadRankingPosition(JsonElement element, out int position)
    {
        position = 0;

        if (TryReadPositiveRank(element, out position))
            return true;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            return false;

        return TryReadPositiveRank(items[0], out position);
    }

    private static bool TryReadPositiveRank(JsonElement element, out int position)
    {
        return TryReadPositiveInt(element, "position", out position)
               || TryReadPositiveInt(element, "rank", out position)
               || TryReadPositiveInt(element, "ranking", out position);
    }

    private static bool TryReadPositiveInt(JsonElement element, string propertyName, out int value)
    {
        return TryReadInt(element, propertyName, out value) && value > 0;
    }

    private void ApplyFaceitLevel(CCSPlayerController player, int level)
    {
        if (!IsUsablePlayer(player) || !FaceitLevelToPin.TryGetValue(level, out var pinId) || player.InventoryServices == null)
            return;

        player.InventoryServices.Rank[5] = (MedalRank_t)pinId;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");

        if (Config.Debug)
            Logger.LogInformation("[CS2FaceitLevels] Applied FACEIT badge level {Level} pin {PinId} to {PlayerName}.", level, pinId, player.PlayerName);
    }

    private void ClearPin(CCSPlayerController player)
    {
        if (!IsUsablePlayer(player) || player.InventoryServices == null)
            return;

        player.InventoryServices.Rank[5] = MedalRank_t.MEDAL_RANK_NONE;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
    }

    private void AddLanguageNoteToGeneratedConfig()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ModuleDirectory))
                return;

            var pluginDirectory = new DirectoryInfo(ModuleDirectory);
            var counterStrikeSharpDirectory = pluginDirectory.Parent?.Parent;
            if (counterStrikeSharpDirectory == null)
                return;

            var configPath = Path.Combine(counterStrikeSharpDirectory.FullName, "configs", "plugins", pluginDirectory.Name, $"{pluginDirectory.Name}.json");
            if (!File.Exists(configPath))
                return;

            var configText = File.ReadAllText(configPath);
            var updated = Regex.Replace(
                configText,
                "^(\\s*\\\"language\\\"\\s*:\\s*\\\"[^\\\"]*\\\"\\s*,)(?:\\s*//.*)?\\s*$",
                "$1 //ar, en, lv, pl, pt-BR, pt-PT, ru, tr, ua, zh-ch",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);

            if (!string.Equals(configText, updated, StringComparison.Ordinal))
                File.WriteAllText(configPath, updated);
        }
        catch
        {
            // Never block plugin startup because of a cosmetic config comment.
        }
    }

    private CS2FaceitLevelsLang LoadLanguage(string language)
    {
        var wanted = NormalizeLanguageName(language);
        var langDirectories = GetLanguageDirectories().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var langPath = FindLanguageFile(langDirectories, wanted) ?? FindLanguageFile(langDirectories, "en");

        if (langPath == null)
        {
            Logger.LogWarning("[CS2FaceitLevels] No language file found for '{Language}'. Searched: {Directories}. Using built-in English messages.", wanted, string.Join(" | ", langDirectories));
            return new CS2FaceitLevelsLang();
        }

        try
        {
            var lang = JsonSerializer.Deserialize<CS2FaceitLevelsLang>(File.ReadAllText(langPath), JsonOptions);
            if (lang == null)
            {
                Logger.LogWarning("[CS2FaceitLevels] Language file '{LanguageFile}' parsed as null. Using built-in English messages.", langPath);
                return new CS2FaceitLevelsLang();
            }

            Logger.LogInformation("[CS2FaceitLevels] Loaded language '{Language}' from: {LanguageFile}", wanted, langPath);
            return lang;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[CS2FaceitLevels] Failed to load language file '{LanguageFile}'. Check JSON syntax. Using built-in English messages.", langPath);
            return new CS2FaceitLevelsLang();
        }
    }

    private IEnumerable<string> GetLanguageDirectories()
    {
        foreach (var directory in GetPluginBaseDirectories())
        {
            if (!string.IsNullOrWhiteSpace(directory))
                yield return Path.Combine(directory, "lang");
        }
    }

    private IEnumerable<string> GetPluginBaseDirectories()
    {
        var moduleDirectory = TryGetBasePluginStringProperty("ModuleDirectory");
        if (!string.IsNullOrWhiteSpace(moduleDirectory))
            yield return moduleDirectory;

        var modulePath = TryGetBasePluginStringProperty("ModulePath");
        var modulePathDirectory = string.IsNullOrWhiteSpace(modulePath) ? null : Path.GetDirectoryName(modulePath);
        if (!string.IsNullOrWhiteSpace(modulePathDirectory))
            yield return modulePathDirectory;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            yield return assemblyDirectory;

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            yield return AppContext.BaseDirectory;

        var currentDirectory = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            yield return Path.Combine(currentDirectory, "addons", "counterstrikesharp", "plugins", ModuleName);
            yield return currentDirectory;
        }
    }

    private string? TryGetBasePluginStringProperty(string propertyName)
    {
        try
        {
            var property = GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                           ?? typeof(BasePlugin).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return property?.GetValue(this) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLanguageFile(IEnumerable<string> langDirectories, string language)
    {
        var wanted = NormalizeLanguageName(language);

        foreach (var directory in langDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var fileLanguage = NormalizeLanguageName(Path.GetFileNameWithoutExtension(file));
                if (string.Equals(fileLanguage, wanted, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }

        return null;
    }

    private static string NormalizeLanguageName(string? language)
    {
        var value = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        return (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(value) : value).Replace('_', '-');
    }

    private string RenderMessage(string template, Dictionary<string, string>? replacements = null)
    {
        var message = template.Replace("{PREFIX}", Lang.ChatPrefix, StringComparison.OrdinalIgnoreCase);

        if (replacements != null)
        {
            foreach (var (key, value) in replacements)
                message = message.Replace("{" + key + "}", value, StringComparison.OrdinalIgnoreCase);
        }

        return ReplaceChatColorTags(message);
    }

    private string RenderEloLine(string template, PlayerSnapshot player, CachedFaceitData? data)
    {
        var message = template
            .Replace("{PREFIX}", Lang.ChatPrefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{PLAYER_COLOR}", PlayerNameColor, StringComparison.OrdinalIgnoreCase)
            .Replace("{LABEL_COLOR}", EloLabelColor, StringComparison.OrdinalIgnoreCase)
            .Replace("{ELO_COLOR}", GetEloColor(data?.SkillLevel), StringComparison.OrdinalIgnoreCase)
            .Replace("{PLAYER}", player.PlayerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{STEAMID64}", player.SteamId.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{ELO}", data?.Elo?.ToString() ?? NoFaceitText, StringComparison.OrdinalIgnoreCase)
            .Replace("{LEVEL}", data?.SkillLevel?.ToString() ?? NoFaceitText, StringComparison.OrdinalIgnoreCase);

        return ReplaceChatColorTags(message);
    }

    private static string GetEloColor(int? skillLevel)
    {
        return skillLevel switch
        {
            1 => "{GREY}",
            2 or 3 => "{LIME}",
            >= 4 and <= 7 => "{YELLOW}",
            8 or 9 => "{ORANGE}",
            10 => "{RED}",
            _ => NoFaceitColor
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
            if (field.FieldType == typeof(char))
                map[NormalizeColorKey(field.Name)] = ((char)(field.GetValue(null) ?? '\x01')).ToString();
        }

        if (map.TryGetValue("grey", out var grey))
            map.TryAdd("gray", grey);

        return map;
    }

    private static string NormalizeColorKey(string key)
    {
        return key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
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

    private static IEnumerable<CCSPlayerController> OnlinePlayers()
    {
        return Utilities.GetPlayers().Where(IsUsablePlayer);
    }

    private static bool IsUsablePlayer([NotNullWhen(true)] CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot && player.Connected == PlayerConnectedState.Connected && player.SteamID != 0;
    }

    private sealed record CachedFaceitData(int? Level, int? SkillLevel, int? Elo, DateTimeOffset ExpiresAt);
    private sealed record FaceitPlayerData(int? Level, int? SkillLevel, int? Elo);
    private sealed record PlayerFaceitResult(PlayerSnapshot Player, CachedFaceitData? Data);
    private sealed record PlayerSnapshot(int Slot, ulong SteamId, string PlayerName)
    {
        public static PlayerSnapshot From(CCSPlayerController player) => new(player.Slot, player.SteamID, player.PlayerName);
    }
}
