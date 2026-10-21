# Awkward stores, composite values, research fallbacks, store ids, cloud sync

## Binary / opaque settings files

Some games keep graphics options in a binary file. Example: Star Wars Jedi: Survivor keeps the upscaler,
frame generation, ray tracing, motion blur, film grain and chromatic aberration in a binary UE save,
`{SavedGames}/Respawn/JediSurvivor/GameUserSettings.sav`. Its text ini only has some of the `sg.*` keys.

- **Recognise it**: `gp inspect <id>` prints the file as text, so you get mojibake with readable fragments. That
  is expected and not an error. To see option names, use a strings view instead. `strings` is not installed on
  this machine, so use the bundled script: `node <skill>/scripts/strings.js <file> [regex]`. It prints ASCII and
  UTF-16 strings of 6 or more characters.
  UE `.sav` (GVAS) files show property names followed by type names like `BoolProperty` / `IntProperty` / `EnumProperty`.
- **Map it as `handler: file`** with its own id (e.g. `sav`). Presets cannot set anything in it (the validator
  rejects keys), but manual saves snapshot it whole (`<fileId>.snap`), so once the user tunes in-game and saves,
  the setup is restored on every launch.
- **Never hand-edit binary values.** Wrong offsets or lengths corrupt the file.
- **In-game steps instead of keys**: put the settings the user must change in-game into each preset's
  `description`, e.g. "In-game: Upscaler FSR 3 Performance, Frame Gen off, RT off; then save a manual save." Also
  list them per device in `notes:`. Rule 6 (other vendor's upscaler off) then becomes one of these steps.
- Keep the rest of the presets on the text stores as normal. Keys that the binary store may override are a behaviour doubt: say so in a comment and settle it with `run --verify`.

## Composite / struct values in one key

Example: Borderlands 4 writes about 19 settings as one ini value:
`GraphicsSettings=(("ShadowQuality", "High"), ("FoliageDensity", "Medium"), ...)`. UE struct and array
properties in general look like `Key=(A=1,B=(C="x"))` or repeated `+Key=...` lines.

- The ini handler treats the whole value as one string, so a preset **writes the complete value**.
- Copy the game's own **order, spacing, quoting and casing** exactly from the file on disk. Change only the pair
  values you mean to change.
- Use only spellings you observed, either on disk (after toggling the option in-game and re-inspecting) or in
  the exe's string table. If you cannot confirm a value spelling for an entry, leave that entry at the value seen
  on disk and do not guess.
- Trade-off, to note in a comment: the whole value is rewritten for every preset. A new entry added by a patch,
  or a pair the user changed in-game, is overwritten until the preset is refreshed. Manual saves still capture
  the whole value, so in-game tuning survives as a save.
- Repeated `+Key=` array lines cannot be expressed as separate keys. Leave them out of presets and rely on manual
  saves (note it).

## Research fallbacks

- PCGamingWiki, TechSpot, Nexus Mods and some forums often return **403 to WebFetch**. Use WebSearch result
  snippets, other mirrors (Steam Community guides, Reddit, GitHub code search, Fandom wikis, archive.org copies),
  or the game's own files.
- **Exe string table** (key names only, never values): UE games embed config property names as UTF-16 strings.
  `node <skill>/scripts/strings.js "<Proj>/Binaries/Win64/<Game>.exe" "upscal|framegen|dlss|fsr|xess|raytrac|motionblur"`
  gives candidate names. A name found only here is not written to a preset until it has been seen in the config file.
- In `recommendation.sources`, record what you could not fetch and what you used instead, e.g.
  `"PCGamingWiki Jedi Survivor (403 to WebFetch; file paths taken from search snippet)"`. Repeat any material
  gap in `notes:`.

## Store ids

The library scan matches an installed game to a config by `steamAppId`, then `storeIds.<store>`, then a
normalised name. A wrong or missing id means the game shows as "needs presets" and gets queued again. The id a
queued request carries (`storeId`) is exactly what the scanner saw, so copy it verbatim; `gp library` shows the
`store:id` keys of everything installed. To look ids up by hand:

| Key | Id | Where to find it |
|---|---|---|
| (steam) | app id → `steamAppId` | `appmanifest_<id>.acf` in `steamapps\`, steamdb.info |
| `battlenet` | product code (`Pro` Overwatch, `Fen` Diablo IV, `WTCG` Hearthstone, `OSI` Diablo II: Resurrected) | the code used in `Battle.net.exe --exec="launch <code>"`; community lists |
| `ea` | offer id (`Origin.OFR.50.0005114`) | EA app logs `%LOCALAPPDATA%\Electronic Arts\EA Desktop\Logs\EADesktop.log` / `EASteamProxy.log` (`offerId=[...]`), `__Installer\installerdata.xml` in the install dir |
| `epic` | AppName (not the display name or catalog id) | `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` → `"AppName"` |
| `gog` | numeric game id, quoted as a string | `goggame-<id>.info` in the install dir, or `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id>` |
| `xbox` | package family name (`Publisher.Game_8wekyb3d8bbwe`) | `Get-AppxPackage *<name>* \| Select PackageFamilyName` |

Games sold through several launchers: add every id you can confirm. A Steam game that runs through another
launcher (EA titles via EASteamProxy, Ubisoft Connect) may also be detected by that launcher's scanner, so add
its id there too (e.g. jedi-survivor has `storeIds.ea`). `store:` must be a value `Validator.cs` accepts (today
steam, battlenet, ea, other); Epic, GOG and Xbox games use `store: other` with their `storeIds` until it accepts
those names.

## Cloud sync check (`cloudSyncsSettings`)

The question is whether any **graphics settings store** of the game travels between the desktop and the Ally X.
Saves syncing does not count; settings files (or a binary save that holds graphics options, like Jedi: Survivor's
`GameUserSettings.sav`) do.

- **Steam**: Steam lists the files it syncs in `<Steam>\userdata\<accountid>\<appid>\remotecache.vdf` (`<Steam>`
  usually `C:\Program Files (x86)\Steam`; there may be several account folders). `grep -i -E
  'ini|json|sav|cfg|settings' remotecache.vdf`. Listed → synced. No `<appid>` folder at all after the game has been
  played → Steam Cloud is not used for it (check the game's own launcher next). Auto-Cloud games also leave a
  `steam_autocloud.vdf` in the synced folder: a settings file outside that folder is not synced.
- **EA app**: `EADesktop.log` / `EADesktopVerbose.log` log `[CloudSync] New file added to the LocalState filesMap.
  File path [...]` for every synced file; `cloudsync\<contentId>_.*` exist for games with cloud saves.
- **Epic / GOG / Xbox / Battle.net**: no reliable local list. Use PCGamingWiki's "Cloud" row (Epic Games Store
  cloud, GOG Galaxy cloud, Xbox cloud) and whether the settings file sits in the synced save folder. Battle.net
  games often keep some settings server-side (account-bound): that counts as synced.
- Value: `true` if any graphics store travels, `false` if none does, `null` only when it could not be checked
  (game never run, sources disagree) with the reason in a comment. Put the evidence in a comment above the key
  and in `notes:`, e.g. "Steam Cloud syncs only SaveGames/*.sav, not Config (remotecache.vdf, 2026-10-16)".
- `false` makes the app suggest that the user opt the game out (each machine keeps its own settings anyway); the
  presets still work if they keep it managed. `true` keeps the rule that every per-device difference is set in
  every preset.
