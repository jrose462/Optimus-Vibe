# GameProfiles

Per-device game graphics settings for Windows. The same games run on a desktop (RTX GPU, <desktop resolution> @ 165 Hz)
and a ROG Xbox Ally X handheld (Ryzen AI Z2 Extreme, 1920x1080 @ 120 Hz). Each needs different settings, and Steam
Cloud keeps syncing one machine's config onto the other. GameProfiles detects which machine it is on and, every
time a game launches, writes that machine's chosen preset (a Claude-recommended one or your own manual save)
into the game's config files. It can also switch the display
mode and set NVIDIA driver profiles. Settings live as YAML in this git repo (`config/`).

It works like Apollo/Sunshine prep commands: apply before launch, undo the temporary parts (display mode) after exit.

## Install

Requires the .NET 8 SDK to build. The published exe is self-contained, so target machines need no runtime.

```powershell
dotnet publish src/GameProfiles -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# copy the exe from src\GameProfiles\bin\Release\net8.0-windows\win-x64\publish\ to C:\Tools\GameProfiles\
```

Use the same exe path on every machine (`C:\Tools\GameProfiles\gameprofiles.exe`). Steam launch options then
look the same everywhere.

## One-time setup per machine

1. Clone this repo (e.g. `<repo>`).
2. Point the tool at the config folder. This writes `%LOCALAPPDATA%\GameProfiles\settings.json`, which stays
   local and is not in git:
   ```powershell
   gameprofiles setup --config-root <repo>\config
   ```
   If detection picks the wrong device, add `--force-profile ally-x` (or `desktop`).
3. Check detection: `gameprofiles detect` shows the hardware strings and the matched device.
4. `gameprofiles list` and `gameprofiles validate`.

The config root is found in this order: `--config <dir>`, `GAMEPROFILES_CONFIG`, `settings.json`, then a `config`
folder next to the exe or in a parent of the current directory.

## Presets and manual saves

Each game has two kinds of settings per device, shown as two tabs in the app:

- **Recommended** presets, written by Claude (`presets:` in `config/games/<game>.yaml`). The desktop gets
  `quality` (best visuals at a smooth framerate: DLSS Quality/Balanced, RT where affordable) and `performance`
  (toward the 165 Hz cap: DLSS Performance, RT off). The Ally X gets `performance` (60+ fps sustained, FSR/XeSS
  from 1080p, 120 Hz VRR) and `battery` (30/40 fps cap and the cheapest playable settings; pair it with Armoury
  Crate Silent, since GameProfiles cannot set TDP). Each preset has a label, a short description, and a target fps.
- **Manual saves**, made by you: tune the game in its own menu, then save the result under a name. A save records
  every setting the game's presets know about (files with no mapped keys are copied whole). Saves live in
  `config/saves/<game>/<device>/<slug>/` and are committed and pushed automatically, so they follow you to the
  other machine's checkout.

Which settings a launch uses: the preset you picked (in the app or with `use`) is remembered per game per
device; until you pick one, the device's `defaultPreset` from `devices.yaml` applies (desktop `quality`, Ally X
`performance`).

## The app

`GameProfilesApp.exe` lists your games with the active preset for this device. For each game, pick a preset in the
**Recommended** tab or one of your **Manual saves**; the choice is remembered for this device. From the Manual saves
tab you can save the game's current settings, update, rename, or delete a save. Saves are synced through git.

## Commands

Global options: `--config <dir>` (config root), `--profile <device>` (override detection),
`--preset <name | manual:<slug>>` (use this preset for one command instead of the remembered one).

| Command | What it does |
|---|---|
| `run <game> [--no-prompt] [-- %command%]` | Back up, apply the active preset, set display + driver profile, launch, wait for the game's processes, restore the display, then offer to keep settings you changed in-game as a manual save |
| `apply <game> [--dry-run]` | Apply without launching; `--dry-run` prints a per-key diff |
| `presets <game>` | List recommended presets and manual saves for this device (`*` = active) |
| `use <game> <preset \| manual:<slug> \| default>` | Remember which preset the game uses on this device (`default` = back to the device default) |
| `save <game> <name> [--update <slug>] [--notes <text>] [--no-activate] [--no-sync]` | Save the game's current settings as a manual save (and make it active, and commit/push, unless told not to) |
| `delete-save <game> <slug>` | Delete one of this device's manual saves |
| `sync` | Commit manual saves, pull, and push the config repo |
| `restore <game> [--original \| --timestamp <stamp>]` | Roll the game's files back from a local backup |
| `detect` | Show detected hardware and the matched device |
| `inspect <game> [--full]` | Show resolved paths, current values vs the active preset, and file contents |
| `list` | List configured games with the active preset, available presets, and number of manual saves |
| `validate` | Check devices.yaml, every game file, and every manual save |
| `update` | `git pull` the config repo |
| `shortcut <game> [--out <dir>]` | Create a .lnk that runs the game through GameProfiles (for non-Steam games) |
| `setup --config-root <path> [--force-profile <device>]` | Per-machine setup |

Examples:

```powershell
gameprofiles presets cyberpunk2077
gameprofiles use cyberpunk2077 performance
gameprofiles save cyberpunk2077 "Steady 40" --notes "Medium crowds, 40 cap"
gameprofiles apply cyberpunk2077 --profile ally-x --preset battery --dry-run
```

## Steam games

In Steam, open the game's Properties, then Launch Options:

```
"C:\Tools\GameProfiles\gameprofiles.exe" run cyberpunk2077 -- %command%
```

Use the game id (the YAML file name under `config/games/`). Steam starts the wrapper, and the wrapper starts the
game. When launched by Steam there is no console window; output goes to the log (see below).

## Non-Steam games (Battle.net, EA app)

These have no `%command%`. The game file's `launch:` block says how to start them, e.g. Battle.net with
`--exec="launch Pro"` (Overwatch) or `--exec="launch Fen"` (Diablo IV). Create a shortcut:

```powershell
gameprofiles shortcut overwatch --out "$env:USERPROFILE\Desktop"
```

Launch the game from that .lnk, or add it to Steam as a non-Steam game so it also works from the Ally's
full-screen experience.

## Tuning settings in-game

You don't need Claude (or YAML editing) to change a game's settings:

1. Launch through GameProfiles and change settings in the game's menu.
2. On exit, if any tracked setting changed, a prompt asks whether to keep the changes as a new manual save (or
   to update the manual save you are using). Answer Yes and it becomes the game's active preset on this device.
   Answer No and the active preset is re-applied next launch.
3. Or save any time with `gameprofiles save <game> "<name>"` or from the app's Manual saves tab.
4. To go back to a recommendation: pick it in the app or `gameprofiles use <game> default`.

Manual saves are committed and pushed automatically (turn this off with `"autoSync": false` in
`%LOCALAPPDATA%\GameProfiles\settings.json`; then run `gameprofiles sync` yourself). Run `gameprofiles update`
(or `sync`) on the other machine to pull them. Each device only uses its own saves.

## Backups, restore, logs

- Before writing, the tool backs up every file it touches to `%LOCALAPPDATA%\GameProfiles\backups\<game>\`:
  `original\` (the first state ever seen, kept forever) plus timestamped folders (`yyyyMMdd-HHmmss`, last 10).
- `gameprofiles restore <game> --original` returns to the very first state; `--timestamp <stamp>` restores a specific
  backup (the folder name). Without a flag it restores the most recent backup.
- Logs: `%LOCALAPPDATA%\GameProfiles\logs\`. Run state used for crash recovery: `%LOCALAPPDATA%\GameProfiles\state.json`.

## Device detection

`config/devices.yaml` lists devices with `match` rules on `model`, `manufacturer`, `cpu`, `gpu`, or `machine`
(computer name), using `*`/`?` wildcards. The first device whose rules all match wins. Virtual display adapters
from Apollo/Sunshine/GlideX/Parsec are ignored. The Ally X is matched on its CPU (`*Z2 Extreme*`) because the
desktop's desktop Ryzen CPU also reports an "AMD Radeon(TM) Graphics" iGPU. Override with `--profile <device>` or
`setup --force-profile <device>`.

## Driver settings

- **NVIDIA (desktop)**: the `nvidia:` block in a game preset (e.g. `FrameRateLimiter: 162`) is written to the
  driver's per-application profile for the game's exe via NVAPI DRS. It persists like a setting made in NVIDIA
  Control Panel and is not undone on exit.
- **AMD (Ally X)**: AMD has no supported API for Adrenalin per-game profiles. The plan is to apply ADLX *global* 3D
  settings (Chill, RSR, AFMF, Anti-Lag...) on launch and restore them on exit. **Not implemented yet**: `amd:`
  blocks are ignored. See [docs/amd-adlx.md](docs/amd-adlx.md).

A vendor block on the wrong hardware is skipped.

## Safety

- Only game config files, registry values listed in a game's YAML, the display mode, and driver profiles are
  changed. The tool never touches game memory or game binaries and does not inject anything, so it is safe with
  anti-cheat.
- Every write is preceded by a backup; the display mode is restored on exit, and after a crash on the next start.

## Troubleshooting

- **"This machine matches no device"**: run `gameprofiles detect` and adjust `match` in `devices.yaml`, or
  `setup --force-profile <device>`.
- **Settings don't stick**: the game may rewrite its config on boot. Try `readOnlyAfterApply: true` for that file,
  and check `notes:` in the game's YAML. Some games store settings server-side (some Blizzard keys).
- **Wrong machine's settings after a sync**: launch through the wrapper (Steam launch option / shortcut). Plain
  launches skip GameProfiles. Make sure options that differ per device are set in every preset.
- **"Cannot write JSON path" / key not found**: the key name is wrong for this game version. Run
  `gameprofiles inspect <game>` and fix it (look for `# TODO verify` comments).
- **Display didn't switch back** (wrapper crashed or was killed): the next `gameprofiles run` detects the stale
  state and restores it first. Otherwise set it in Windows display settings.
- **No visible output from a Steam launch**: check `%LOCALAPPDATA%\GameProfiles\logs\`.
- **Undo everything for a game**: `gameprofiles restore <game> --original`.

## Adding games

Ask Claude: "add <game> to GameProfiles" or "refresh recommendations for <game>". It follows the
`game-settings-recommender` skill (`.claude/skills/game-settings-recommender/`), which researches the game's
settings, confirms key names with `gameprofiles inspect`, writes the four presets, validates and commits. See
[CLAUDE.md](CLAUDE.md) for the rules (written for Claude but readable by people) and
`config/games/_template.yaml` for every field. In VS Code with the YAML extension, game files are validated
against `config/schema/game.schema.json` as you type.
