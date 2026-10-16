# GameProfiles

Per-device game graphics settings for Windows. The same games run on a desktop (RTX GPU, <desktop resolution> @ 165 Hz)
and a ROG Xbox Ally X handheld (Ryzen AI Z2 Extreme, 1920x1080 @ 120 Hz). Each needs different settings, and Steam
Cloud keeps syncing one machine's config onto the other. GameProfiles detects which machine it is on and, every
time a game launches, writes that machine's chosen preset (a Claude-recommended one or your own manual save)
into the game's config files. It can also switch the display
mode and set NVIDIA driver profiles. Settings live as YAML in this git repo (`config/`).

It finds your games by itself: installed games from Steam, Battle.net, EA app, Xbox app, Epic and GOG show up in
the app's library, are managed automatically (Steam launch options included), and games it has no settings for
yet are queued for Claude to research.

It works like Apollo/Sunshine prep commands: apply before launch, undo the temporary parts (display mode) after exit.

## Install

Requires the .NET 8 SDK to build. Published exes are self-contained, so target machines need no runtime.

There are two exes, built on the same core:

| Exe | Needed on | Used for |
|---|---|---|
| `GameProfilesApp.exe` | **every machine** | The app (no arguments), and the Steam launch-option wrapper (`run …`, headless: no window, no console flash) |
| `gameprofiles.exe` | optional (desktop) | Command line for scripting and for Claude (`inspect`, `validate`, dry runs) |

```powershell
dotnet publish src/GameProfiles.App -c Release -o C:\Tools\GameProfiles
dotnet publish src/GameProfiles     -c Release -o C:\Tools\GameProfiles   # optional CLI
```

Use the same path on every machine (`C:\Tools\GameProfiles\`). Steam launch options then look the same everywhere.

## One-time setup per machine

There are two ways to get the config onto a machine (the **sync provider**):

| Provider | Use on | How it syncs |
|---|---|---|
| `git` (default) | the desktop | `ConfigRoot` is your own git checkout; the app runs the git CLI to commit, pull and push |
| `github` | the Ally X, or any machine without git | the app keeps its own copy under `%LOCALAPPDATA%\GameProfiles\repos\<owner>\<name>\` and syncs it through the GitHub API after you sign in. No git, no clone |

### Ally X (no git needed)

1. Copy the published `C:\Tools\GameProfiles\` folder to the Ally (same path as on the desktop).
2. Open `GameProfilesApp.exe` → **Settings** (Menu button) → **Sign in with GitHub** (see below), pick the repo
   (default `jrose462/GameProfiles`), and choose the `github` provider. The app downloads the config and from then
   on pulls and pushes it itself. CLI equivalent: `gameprofiles github login` (or `github token <PAT>`), then
   `gameprofiles github use jrose462/GameProfiles`; `gameprofiles github status` shows what it is using.
3. Check detection (`gameprofiles detect`, or the device shown in Settings).

### Desktop (git checkout)

1. Clone this repo (e.g. `<repo>`).
2. Point the tool at the config folder: open `GameProfilesApp.exe` and pick the repo folder on its first-run
   screen, or run `gameprofiles setup --config-root <repo>\config`. Either writes
   `%LOCALAPPDATA%\GameProfiles\settings.json`, which stays local and is not in git.
   If detection picks the wrong device, add `--force-profile ally-x` (or `desktop`); the app offers this too.
3. Check detection: `gameprofiles detect` shows the hardware strings and the matched device.
4. `gameprofiles list` and `gameprofiles validate`.

The config root is found in this order: `--config <dir>`, `GAMEPROFILES_CONFIG`, the `github` provider's managed
copy (when that provider is selected and the copy exists), `settings.json` `configRoot`, then a `config` folder
next to the exe or in a parent of the current directory. To go back from `github` to a checkout:
`gameprofiles sync-provider git`.

### GitHub sign-in

The `github` provider (and the repo picker) needs a GitHub token, stored in Windows Credential Manager
(`GameProfiles:github`). Two ways to get one:

- **Device-code sign-in** (Settings → Sign in with GitHub, or `gameprofiles github login`): the app shows a code;
  open github.com/login/device on any device, enter it, approve. This needs the **Client ID of a GitHub OAuth App
  with Device Flow enabled**, registered once:
  1. github.com/settings/developers → **OAuth Apps** → **New OAuth App**.
  2. Any name; any Homepage URL and Authorization callback URL (e.g. `https://github.com/jrose462/GameProfiles`;
     device flow never uses the callback).
  3. Register, then tick **Enable Device Flow** and save.
  4. Copy the **Client ID** (public, not a secret; no client secret needed) into Settings → GitHub client id
     (`gitHubClientId` in `settings.json`).

  The app asks for the `repo` scope, so private config repos work.
- **Personal access token**: create a fine-grained PAT (github.com/settings/personal-access-tokens) with access
  to just the config repo and **Contents: Read and write**, then paste it in Settings or run
  `gameprofiles github token <PAT>`.

Then pick the repo: Settings lists the repos you can push to (`gameprofiles github repos`); the default is
`jrose462/GameProfiles` (branch `main`). `gameprofiles github logout` removes the token.

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

`GameProfilesApp.exe` opens on your **Library**: every installed game it detected, plus configured games that are
not installed here. On the Ally (and small screens) it is a controller-first full-screen grid: **A** play,
**X** presets, **Y** sync, **Menu** settings (`libraryMode` in `settings.json`: `auto`, `grid` or `list`).
Playing from the app applies the game's active preset, then starts it through its launcher.

For each game, pick a preset in the **Recommended** tab or one of your **Manual saves**; the choice is remembered
for this device. From the Manual saves tab you can save the game's current settings, update, rename, or delete a
save. Saves are synced through the selected sync provider (git or GitHub).

**Settings** holds GitHub sign-in, the repo and sync provider, which launchers to scan (`scanStores`), whether
to write Steam launch options, and the device override.

## Library: auto-detect, opt-out, request queue

- **Detection**: the app scans Steam, Battle.net, EA app, Xbox app, Epic and GOG (`scanStores` in
  `settings.json`) and matches each install to `config/games/<id>.yaml` by `steamAppId`, then `storeIds`
  (battlenet / ea / epic / gog / xbox), then name.
- **Managed by default**: a detected game with presets for this device is *managed*: its preset is applied every
  time it launches through GameProfiles. Statuses: Managed, Needs presets, Opted out, Not installed.
- **Opt-out**: don't want GameProfiles touching a game? Opt it out in the app or with
  `gameprofiles opt-out steam:1285190` (`opt-in` reverses it). The key is `store:id`. Opt-outs are stored in
  `config/library.yaml` and synced, so they apply on every machine. Opted-out games launch plainly and their Steam
  launch option is removed. When a game's config says its settings do **not** cloud-sync
  (`cloudSyncsSettings: false`, e.g. Borderlands 4), the app suggests opting it out: each machine already keeps
  its own settings.
- **Request queue**: an installed game with no config is added to `config/requests.yaml` (store, id, name,
  install folder, exes, device) and synced. Ask Claude to "process GameProfiles requests"; it works through the
  queue with the `game-settings-recommender` skill, one commit per game, removing each entry as its config lands.
  `gameprofiles library` lists what was detected and its status; `gameprofiles library --queue` also queues
  missing games.

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
| `sync` | Commit manual saves, pull, and push the config repo (through the selected sync provider) |
| `library [--queue]` | List detected games with their `store:id` key and status; `--queue` adds games without a config to `config/requests.yaml` |
| `opt-out <key>` / `opt-in <key>` | Stop / resume managing a detected game (`key` = `store:id`, e.g. `steam:1285190`); stored in `config/library.yaml` |
| `steam-options …` | Show or rewrite the Steam launch options GameProfiles manages (see `gameprofiles steam-options --help`) |
| `github login \| token <PAT> \| repos \| use <owner/name> \| logout \| status` | GitHub sign-in (device code or PAT), list repos you can push to, select the config repo and the `github` provider, sign out, show the current setup |
| `sync-provider git` | Switch back to syncing a git checkout (the `configRoot` folder) |
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

Every managed Steam game needs this launch option (Properties → Launch Options):

```
"C:\Tools\GameProfiles\GameProfilesApp.exe" run cyberpunk2077 -- %command%
```

**The app writes it for you**: for every managed Steam game it sets the option (keeping anything else you had
there), and removes it again when you opt a game out. Steam overwrites its config while running, so the app only
writes while Steam is closed: it offers to close Steam, or does it the next time Steam is not running. To manage
launch options yourself, turn this off in Settings (or `"writeSteamLaunchOptions": false` in
`%LOCALAPPDATA%\GameProfiles\settings.json`); `gameprofiles steam-options` shows and applies the changes by hand.

The id after `run` is the game id (the YAML file name under `config/games/`). Steam starts the wrapper, and the
wrapper starts the game. With `run` the app opens no window and no console; output goes to the log (see below). `gameprofiles.exe run`
does the same from a terminal, but flashes a console window when Steam starts it.

## Non-Steam games (Battle.net, EA app, Xbox, Epic, GOG)

The simplest way is to start them from the GameProfiles app (library → A), which applies the preset and then
starts the game through its launcher. Outside the app they have no `%command%`. The game file's `launch:` block says how to start them, e.g. Battle.net with
`--exec="launch Pro"` (Overwatch) or `--exec="launch Fen"` (Diablo IV). The app's **Launch game** button runs them
through the wrapper. Or create a shortcut (it targets `GameProfilesApp.exe run <game>` when the app is next to the CLI):

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

Installed games are queued automatically (see "Library" above); ask Claude to "process GameProfiles requests"
to work through the queue. For a game that is not installed yet, or to redo one, ask Claude:
"add <game> to GameProfiles" or "refresh recommendations for <game>". It follows the
`game-settings-recommender` skill (`.claude/skills/game-settings-recommender/`), which researches the game's
settings, confirms key names with `gameprofiles inspect`, writes the four presets, validates and commits. See
[CLAUDE.md](CLAUDE.md) for the rules (written for Claude but readable by people) and
`config/games/_template.yaml` for every field. In VS Code with the YAML extension, game files are validated
against `config/schema/game.schema.json` as you type.
