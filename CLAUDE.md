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

## Store-specific launch notes

- **Steam**: no `launch:` needed. The user sets the game's launch option to
  `"C:\Tools\GameProfiles\GameProfilesApp.exe" run <id> -- %command%`.
- **Battle.net**: there is no %command%, so set `launch.exe` to `{ProgramFilesX86}/Battle.net/Battle.net.exe`
  with `args: ["--exec=launch <code>"]`. Product codes: `Pro` = Overwatch, `Fen` = Diablo IV (look up others).
  Battle.net.exe exits right away, so `processes:` must list the game exe (e.g. `Overwatch.exe`,
  `Diablo IV.exe`). Some Blizzard settings are synced server-side and can overwrite local files. Note this in
  `notes:`. The user runs `gameprofiles shortcut <id>` to get a .lnk, which can be added to Steam as a non-Steam game.
- **EA app**: launch through the EA app URI or the game exe (the EA app must be running/signed in). Find the exact
  form for the game and record it in `notes:`. Use `shortcut` as for Battle.net.

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
