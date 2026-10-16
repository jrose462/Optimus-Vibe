# GameProfiles

Per-device game graphics settings for Windows. The same games run on a desktop (RTX GPU, <desktop resolution> @ 165 Hz)
and a ROG Xbox Ally X handheld (Ryzen AI Z2 Extreme, 1920x1080 @ 120 Hz). Each needs different settings, and Steam
Cloud keeps syncing one machine's config onto the other. GameProfiles detects which machine it is on and, every
time a game launches, writes that machine's settings into the game's config files. It can also switch the display
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

## Commands

Global options: `--config <dir>` (config root), `--profile <device>` (override detection).

| Command | What it does |
|---|---|
| `run <game> [-- %command%]` | Back up, apply, set display + driver profile, launch, wait for the game's processes, restore the display, then offer to capture changed settings |
| `apply <game> [--dry-run]` | Apply without launching; `--dry-run` prints a per-key diff |
| `capture <game> [--snapshot]` | Save the game's current values as this device's overrides (see below) |
| `restore <game> [--original \| --timestamp <stamp>]` | Roll the game's files back from a local backup |
| `detect` | Show detected hardware and the matched device |
| `inspect <game>` | Show resolved paths and current contents of the game's settings files |
| `list` | List configured games |
| `validate` | Check devices.yaml and every game file |
| `update` | `git pull` the config repo |
| `shortcut <game> [--out <dir>]` | Create a .lnk that runs the game through GameProfiles (for non-Steam games) |
| `setup --config-root <path> [--force-profile <device>]` | Per-machine setup |

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

## Capturing settings you tuned in-game

You don't need Claude (or YAML editing) to change a game's settings:

1. Launch through GameProfiles and change settings in the game's menu.
2. On exit, if the tracked settings changed, a prompt asks whether to save them as this device's settings for the
   game. Answer Yes, or run `gameprofiles capture <game>` later.
3. Captured values go to `config/captures/<game>/<device>.yaml` as an overlay on top of the game's profile. Delete
   that file to go back to the profile in `games/<game>.yaml`.
4. `capture <game> --snapshot` (used automatically for files with no mapped keys) copies whole files into
   `config/snapshots/<game>/<device>/`. Those files are restored as-is on launch.

Commit and push `captures/` and `snapshots/`, then `gameprofiles update` on the other machine. Each device only
reads its own captures.

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

- **NVIDIA (desktop)**: the `nvidia:` block in a game profile (e.g. `FrameRateLimiter: 162`) is written to the
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
  launches skip GameProfiles. Make sure options that differ per device are set in both profiles.
- **"Cannot write JSON path" / key not found**: the key name is wrong for this game version. Run
  `gameprofiles inspect <game>` and fix it (look for `# TODO verify` comments).
- **Display didn't switch back** (wrapper crashed or was killed): the next `gameprofiles run` detects the stale
  state and restores it first. Otherwise set it in Windows display settings.
- **No visible output from a Steam launch**: check `%LOCALAPPDATA%\GameProfiles\logs\`.
- **Undo everything for a game**: `gameprofiles restore <game> --original`.

## Adding games

See [CLAUDE.md](CLAUDE.md) for the workflow (it is written for Claude but works for people too) and
`config/games/_template.yaml` for every field. In VS Code with the YAML extension, game files are validated
against `config/schema/game.schema.json` as you type.
