# CS2FaceitLevels 

CS2 plugin that shows a player's real FACEIT level in the CS2 scoreboard.

From 1 to challanger badge

The plugin does this:

1. Reads the player's SteamID64 after authorization.
2. Requests the player's FACEIT CS2 skill level from the FACEIT Data API.
3. Writes the mapped pin ID to the scoreboard pin slot.
4. workshop addon replaces those pin images with FACEIT level images.

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager)
- [Faceit API Key](https://developers.faceit.com/apps)
- [this workshop addon](https://steamcommunity.com/sharedfiles/filedetails/?id=3724637448) [addon ID: 3724637448]

## ELO Chat Commands

Players can privately check FACEIT ELO using chat commands.

### Commands

- elo/elos commands can be disabled via config

| Command | Description |
|---|---|
| `!elo <playername>` | Shows the selected player’s FACEIT ELO |
| `!elos` | Shows all connected players’ FACEIT ELO |


## Install on server

Copy the `addons` folder into your CS2 server `game/csgo/` folder:

Edit this file and add your FACEIT API key:

```text
counterstrikesharp/configs/plugins/CS2FaceitLevels/CS2FaceitLevels.json.
```

Supported languages:

```text
ar, en, lv, pl, pt-BR, pt-PT, ru, tr, ua, zh-ch
```

Default language:

```json
"language": "en"
```

## MultiAddonManager Setup

- you will need this addon ID [3724637448] if you want the plugin to work
- MultiAddonManager config path: game\csgo\cfg\multiaddonmanager

```
mm_extra_addons 				"3724637448"
mm_client_extra_addons			"3724637448"
```

- if you want to add multiple addons in MultiAddonManager
```
mm_extra_addons 				"3724637448,3732307395"
mm_client_extra_addons			"3724637448,3732307395"
```
## notice
- about the workshop map you do not need to Subscribe manually MultiAddonManager will do everything for you
- for better experience i highly recommended Disabling any plugin that change coins/pins. 
- CS2FaceitLevels plugin will override them but it will take `15sec(can be changed in config)` to reapply FaceitLevles :)


## Search keywords:

cs2 faceit, faceit, faceit plugin, cs2 faceit plugin, cs2-faceit-levels, cs2faceitlevels, faceit levels, faceit-levels, faceit level, faceit level plugin, faceit rank, faceit ranks, faceit rank plugin, faceit elo, faceit elo plugin, faceit scoreboard, faceit scoreboard plugin, cs2 faceit levels, cs2 faceit level, cs2 faceit ranks, cs2 faceit rank, cs2 faceit elo, cs2 faceit scoreboard, cs2 faceit level plugin, cs2 faceit rank plugin, cs2 faceit elo plugin, cs2 faceit scoreboard plugin, real faceit level plugin, real faceit ranks, show faceit level, show faceit levels, show faceit rank, show faceit ranks, show faceit elo, show faceit on scoreboard, show faceit level on scoreboard, show faceit rank on scoreboard, counterstrikesharp faceit, counter strike sharp faceit, counterstrikesharp faceit levels, counterstrikesharp faceit plugin, cssharp faceit, css faceit levels, cs sharp faceit levels, cs2 cssharp faceit, cs2 counterstrikesharp faceit,
