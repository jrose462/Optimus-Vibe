# Engine notes: where settings live and how to map them

Always confirm the real file, section names, key names and value formats with `gameprofiles inspect <id>`
(or by reading the file) before writing presets. Everything below is a starting point, not ground truth.

## Unreal Engine (UE4 / UE5)

### Files

| Engine | Location (packaged game) |
|---|---|
| UE4 | `{LocalAppData}/<Project>/Saved/Config/WindowsNoEditor/GameUserSettings.ini` (+ `Engine.ini`) |
| UE5 | `{LocalAppData}/<Project>/Saved/Config/Windows/GameUserSettings.ini` (+ `Engine.ini`) |
| Either | `{Documents}/My Games/<Game or Project>/Saved/Config/Windows/` (common in newer games, e.g. Borderlands 4) |

Check **both** `{LocalAppData}/<Project>/...` and `{Documents}/My Games/...` (and `{SavedGames}/<Studio>/<Game>/`
for `.sav` files) before picking one; some games write to both and only read one.

- `<Project>` is the internal project name, not the store name (e.g. the Star Wars Jedi games are reported to
  use `SwGame`; Borderlands 3 and 4 use `OakGame` — always check PCGamingWiki and the actual folder
  under `%LOCALAPPDATA%`). Some games use `{Documents}/My Games/<Project>/...`
  or `{SavedGames}`. Some add a per-user/per-platform subfolder (Steam id, `Steam/`, `EGS/`) — use a `*` segment.
- `GameUserSettings.ini` = user settings (what menus write). `Engine.ini` = cvar overrides (`[SystemSettings]`
  section, e.g. `r.Lumen.Reflections.Allow=0`); the game does not write it, so only add keys there for real
  tweaks you can cite. The ini handler inserts missing keys/sections (into an existing file); add `createIfMissing:
  true` to a separate `engine` file entry only if Engine.ini may not exist yet.
- Map as `handler: ini`. Blocks are nested by section; the section name is the bracket content verbatim:
  ```yaml
  gus:
    /Script/Engine.GameUserSettings:
      sg.ShadowQuality: 1
      FrameRateLimit: 60.000000
    /Script/MyProject.MyGameUserSettings:  # made-up example of a game-specific section — use what inspect shows
      UpscalerMode: 2
  ```

### `[/Script/Engine.GameUserSettings]` keys

| Key | Values | Notes |
|---|---|---|
| `sg.ResolutionQuality` | 0-100 (float) | Screen percentage when the game uses engine resolution scaling; with vendor upscalers it is often ignored or overridden |
| `sg.ViewDistanceQuality` | 0-4 | 0 Low … 3 Epic, 4 Cinematic |
| `sg.AntiAliasingQuality` | 0-4 | Also TSR quality in UE5 |
| `sg.ShadowQuality` | 0-4 | Big cost (VSM in UE5) |
| `sg.GlobalIlluminationQuality` | 0-4 | UE5: Lumen GI on at High/Epic; UE4: mostly ignored |
| `sg.ReflectionQuality` | 0-4 | UE5: Lumen reflections at High/Epic |
| `sg.PostProcessQuality` | 0-4 | DOF, bloom, motion blur quality |
| `sg.TextureQuality` | 0-4 | VRAM, little GPU time |
| `sg.EffectsQuality` | 0-4 | Particles, often volumetric fog/translucency |
| `sg.FoliageQuality` | 0-4 | Density/distance; CPU and GPU |
| `sg.ShadingQuality` | 0-4 | UE5 (also UE4.26+) |
| `sg.LandscapeQuality` | 0-4 | Some UE5 games |
| `FrameRateLimit` | float, `0.000000` = uncapped | Written as `60.000000` |
| `bUseVSync` | `True` / `False` | Capitalized |
| `bUseDynamicResolution` | `True` / `False` | Turn off when a fixed upscaler mode is set |
| `ResolutionSizeX` / `ResolutionSizeY` | ints | Fullscreen resolution |
| `LastUserConfirmedResolutionSizeX/Y` | ints | Set together with ResolutionSizeX/Y, or the game may revert |
| `DesiredScreenWidth` / `DesiredScreenHeight` | ints | Some games read these instead |
| `FullscreenMode` / `LastConfirmedFullscreenMode` / `PreferredFullscreenMode` | 0 Fullscreen, 1 WindowedFullscreen (borderless), 2 Windowed | Set the trio consistently |
| `bUseHDRDisplayOutput`, `HDRDisplayOutputNits` | | Leave alone (HDR calibration is the user's) |
| `AudioQualityLevel` | | Never touch |
| `Version`, `bUseDesiredScreenHeight` | | Leave alone |

`[ScalabilityGroups]` in the same file repeats the `sg.*` values in some games; if inspect shows it, set the same
values there too.

**Game-specific sections often hold everything.** Many games put all their menu settings, including
resolution, quality levels and window mode, in their own section and leave `[/Script/Engine.GameUserSettings]`
nearly empty or stale. Examples: `[/Script/SwGame.SWGameUserSettings]` (Jedi: Survivor) and
`[/Script/OakGame.OakUIGameUserSettings]` (Borderlands 4). Map whichever section the game actually updates when
you change a menu option (change it, quit, inspect, diff). Values there may be composite structs; see
`awkward-stores.md`.

**Exe names**: the process is often not `<Project>-Win64-Shipping.exe`. Jedi: Survivor runs `JediSurvivor.exe`
and Borderlands 4 runs `Borderlands4.exe`. List what is in `<Project>/Binaries/Win64/`, plus the root stub
when it differs.

Vendor upscalers (DLSS/FSR/XeSS mode, frame gen, Reflex), RT toggles, motion blur, film grain,
chromatic aberration and frame-limiter UI usually live in a **game-specific section** (e.g.
`[/Script/<Project>.<Project>GameUserSettings]`) with game-specific names and enum ints. Never guess those:
inspect, toggle the option in-game, inspect again, and diff.

Engine `.pak` files ship `DefaultScalability.ini`/`BaseScalability.ini` describing what each `sg.*` level sets —
useful for cost judgements, not for writing.

UE quirks: games often rewrite GameUserSettings.ini on exit (fine — GameProfiles reapplies on launch); a few
validate the file on boot and reset unknown values (`readOnlyAfterApply: true` fixes that but blocks in-game
changes). Shader-compile stutter is not a setting.

## Unity

- **PlayerPrefs in the registry**: `HKCU\Software\<CompanyName>\<ProductName>` (`handler: registry`). Value names
  carry a hash suffix: `Screenmanager Resolution Width_h182942802`, `Screenmanager Fullscreen mode_h3630240806`,
  `UnityGraphicsQuality_h1669003810`. Use the exact names `inspect` shows (hash included). Ints are DWORDs;
  floats are stored as 8-byte REG_BINARY (doubles) and strings as REG_BINARY null-terminated UTF-8 — leave those
  out unless you can write them correctly (the registry handler keeps existing kinds; binary = hex string).
- Many Unity games instead write their own JSON/XML/ini under `{LocalLow}/<Company>/<Product>/`.

## Frostbite (EA)

- `{Documents}/<Game>/settings/PROFSAVE_profile` (binary-ish, mixed) and `PROFOPTS_profile` or
  `{Documents}/<Game>/settings/<Platform>/PROFSAVE_profile`. Plain `GstRender.Key value` lines in many titles —
  `handler: keyvalue` with `format: ...` or a `template: '{key} {value}'` after checking the separator with
  inspect. Some titles store it as binary: then use `handler: file` (manual saves only, no presets) and say so.
- EA app launch: no %command%; see CLAUDE.md store notes.

## Others (brief)

- **REDengine 4** (Cyberpunk 2077): `{LocalAppData}/CD Projekt Red/Cyberpunk 2077/UserSettings.json`, JSON array
  filters (`data[group_name=...].options[name=...].value`). See `config/games/cyberpunk2077.yaml`.
- **id Tech** (Doom, Indiana Jones): `{SavedGames}/id Software/<Game>/base/*Config.local` with `seta key "value"`
  → keyvalue + `template: 'seta {key} "{value}"'`.
- **Source / Source 2**: `video.txt` / `cs2_video.txt` (`"key" "value"` lines) → keyvalue `format: quoted` with
  care for indentation; Steam Cloud carries these.
- **Decima, Snowdrop, Anvil, RE Engine, Northlight**: usually ini/xml/json under `{Documents}` or `{LocalAppData}`;
  RE Engine uses `config.ini` in the install dir (`{SteamLibrary:<appid>}`). Look up PCGamingWiki per game.
- **Blizzard** (Battle.net): `{Documents}/Overwatch/Settings/Settings_v0.ini`, Diablo IV `LocalPrefs.txt`
  (keyvalue quoted); some settings sync server-side.

## Path tokens recap

`{Documents} {LocalAppData} {AppData} {LocalLow} {SavedGames} {UserProfile} {ProgramData} {ProgramFiles}
{ProgramFilesX86} {SteamRoot} {SteamLibrary:<appid>} {SteamUserData:<appid>}`, `%ENV%`, and `*`/`?` in a
segment (newest match wins). Registry paths: `HKCU\...` / `HKLM\...` (YAML-escape backslashes in double quotes).
