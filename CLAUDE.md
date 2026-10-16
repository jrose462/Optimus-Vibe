# GameProfiles: notes for Claude

GameProfiles is a C# .NET 8 Windows tool that applies per-device game settings from YAML in `config/`:
a CLI (`src/GameProfiles`, `gameprofiles.exe`, for scripting and for Claude), a shared core
(`src/GameProfiles.Core`), and a WPF app (`src/GameProfiles.App`, `GameProfilesApp.exe`) for picking presets and
managing manual saves. The app is also the Steam launch-option wrapper: `GameProfilesApp.exe run …` runs headless
(no window/console) with the same behaviour as `gameprofiles run`. The usual job in this repo is **adding a
game or refreshing its recommended presets**. Code changes follow normal practice: run `dotnet test` and keep
the handlers' round-trip tests green.

## Adding or refreshing a game: use the skill

**The way to add a game or refresh its recommendations is the `game-settings-recommender` skill**
(`.claude/skills/game-settings-recommender/SKILL.md`, mirrored to `~/.claude/skills/`; the repo copy is the
source of truth). It covers identification, research, confirming keys with `gameprofiles inspect`, writing
the four presets, validation, dry runs and the commit. Its `references/` hold the device specs, preset rules,
and engine notes. Do not hand-roll a game file without following it.

Every game file must carry `cloudSyncsSettings` (true/false from the Steam `remotecache.vdf` / store cloud check;
null only if truly unknown — `false` makes the app suggest opting the game out) and, for non-Steam games,
`storeIds` (battlenet / ea / epic / gog / xbox) so the library scan can match the install.

## The library, the request queue and opt-outs

The app auto-detects installed games (Steam, Battle.net, EA app, Xbox app, Epic, GOG; `Library/` in Core) and
manages every detected game that has presets, unless the user opted it out. For managed Steam games it writes the
launch option `"<path>\GameProfilesApp.exe" run <id> -- %command%` itself while Steam is closed
(`writeSteamLaunchOptions` in settings.json turns that off).

- **`config/requests.yaml` is your work queue.** The app appends every installed game that has no config
  (`store`, `storeId`, `name`, `installDir`, `exes`, `requested`, `device`). When the user says "process
  GameProfiles requests" / "work through the game queue" (or at the start of a session where they ask what to do
  next and the queue is non-empty), process it with the skill's "process the request queue" workflow: one
  commit per game that adds `config/games/<id>.yaml` and removes that entry. Leave entries you could not map and
  tell the user why. Only ever remove handled entries; never reorder or rewrite the rest.
- **`config/library.yaml` is user-owned.** Its `optOut:` list (`store:id` keys) records games the user does not
  want managed. Never add or remove entries unless the user explicitly asks; suggest `gameprofiles opt-out <key>`
  instead.
- Sync: `git` provider = a checkout synced with the git CLI (desktop); `github` provider = a managed copy under
  `%LOCALAPPDATA%\GameProfiles\repos\` synced through the GitHub API after device-code/PAT sign-in (the Ally; no
  git). Both push to `jrose462/GameProfiles` by default, so pull before editing the queue.

## Devices

| Device id | Hardware | Display | Driver | defaultPreset |
|---|---|---|---|---|
| `desktop` | RTX GPU 16 GB, desktop Ryzen CPU (it also exposes an "AMD Radeon(TM) Graphics" iGPU) | <desktop resolution> @ 165 Hz, G-Sync | nvidia | `quality` |
| `ally-x` | ROG Xbox Ally X, Ryzen AI Z2 Extreme (RDNA 3.5, 16 CUs), 24 GB LPDDR5X shared, 80 Wh | 1920x1080 @ 120 Hz VRR | amd | `performance` |

`config/devices.yaml` matches the Ally X on the CPU (`*Z2 Extreme*`), never on an AMD GPU name, because the
desktop also reports a Radeon iGPU. Virtual adapters (Apollo/SudoMaker, Sunshine, GlideX, Parsec) are filtered
out by the detector. First match wins, so keep `ally-x` above `desktop`.

## Presets and manual saves

- **Recommended presets** (Claude-written, app tab "Recommended"): `presets.<device>.<name>` in
  `config/games/<id>.yaml`. Conventional names: desktop `quality` / `performance`; ally-x `performance` /
  `battery`. Each has `label`, `description`, `targetFps`, usually `extends: base`, plus `display`, `nvidia`,
  (`amd`, not implemented yet — `docs/amd-adlx.md`) and file blocks. `profiles:` holds shared bases only (usually
  `base`); `extends` may only name a `profiles:` entry. `recommendation:` records date, game version and sources.
- **Manual saves** (user-made, app tab "Manual saves"): `config/saves/<game>/<device>/<slug>/save.yaml`
  (+ `<fileId>.snap` whole-file copies). Made with the app, the post-game prompt, or `gameprofiles save`. They
  record every key any preset or profile mentions. The app/CLI commits and pushes them (`gameprofiles sync`).
- **Which one applies**: explicit `--preset` / app choice → the remembered selection (per game per device, in
  `%LOCALAPPDATA%\GameProfiles\selections.json`) → the device's `defaultPreset` → the device's first preset.

**Never edit, regenerate, or delete anything under `config/saves/`.** It holds the user's tuning. If a change to
a game file would break a save (renamed file id, removed key), tell the user and let them decide.

## Config layout (`config/`)

| Path | Owner | What |
|---|---|---|
| `devices.yaml` | Claude/user | Device match rules, default display, driver, defaultPreset |
| `games/<id>.yaml` | **Claude** (via the skill) | One file per game. `_template.yaml` documents every field and handler syntax |
| `schema/game.schema.json` | Claude | JSON Schema for game YAML; keep it in sync with `Config/Models.cs` |
| `saves/<game>/<device>/<slug>/` | **User** (app / `save`) | Manual saves, git-synced |
| `library.yaml` | **User** (app / `opt-out` / `opt-in`) | `optOut:` list of `store:id` keys not to manage. Never edit unless asked |
| `requests.yaml` | App appends, **Claude** removes handled entries | Queue of installed games without a config (work it with the skill) |

## Store-specific launch notes

- **Steam**: no `launch:` needed. The app writes the launch option
  `"C:\Tools\GameProfiles\GameProfilesApp.exe" run <id> -- %command%` for managed games (or the user sets it).
- **Battle.net**: there is no %command%, so set `launch.exe` to `{ProgramFilesX86}/Battle.net/Battle.net.exe`
  with `args: ["--exec=launch <code>"]` and `storeIds.battlenet: <code>`. Product codes: `Pro` = Overwatch,
  `Fen` = Diablo IV (look up others).
  Battle.net.exe exits right away, so `processes:` must list the game exe (e.g. `Overwatch.exe`,
  `Diablo IV.exe`). Some Blizzard settings are synced server-side and can overwrite local files. Note this in
  `notes:`. The user runs `gameprofiles shortcut <id>` to get a .lnk, which can be added to Steam as a non-Steam game.
- **EA app**: launch through the EA app URI or the game exe (the EA app must be running/signed in). Find the exact
  form for the game and record it in `notes:`. Use `shortcut` as for Battle.net. Set `storeIds.ea` (offer id).
- **Epic / GOG / Xbox**: `store: other` (until `Validator.cs` accepts these names) with `storeIds.epic` (AppName),
  `storeIds.gog` (game id) or `storeIds.xbox` (package family name); the app launches them through their launcher.

## Caveats to record in `notes:`

- **Steam Cloud** can sync a config file between the desktop and the Ally X. That is the reason this tool exists.
  `run` re-applies the active preset on every launch, but values the preset does not set still travel. Set every
  option that differs between devices in every preset, and turn the other vendor's upscaler off explicitly.
- Games that reset settings on boot: `readOnlyAfterApply: true` (in-game changes then do not save).
- Anything you could not verify (`# TODO verify`), plus quirks (settings that apply only after a restart,
  launcher behaviour).

## Commits and diffs

- Keep diffs reviewable: one game per commit, no reformatting of unrelated files, comments next to non-obvious values.
- Conventional Commits: `feat(games): add <id> recommendations`, `feat(games): refresh <id> recommendations`,
  `fix(games): correct overwatch render scale key`, `chore(devices): tighten ally-x match`, `docs: ...`,
  `feat(handlers): ...`, `test: ...`. Manual-save commits made by the app use `chore(saves): ...`.
- Do not commit anything from `%LOCALAPPDATA%\GameProfiles` (backups, logs, state, selections, settings.json).
