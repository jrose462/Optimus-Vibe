# Optimus-Vibe: notes for Claude

Optimus-Vibe is a C# .NET 8 Windows tool that applies per-device game settings from YAML in the user's own **config repo** (`config/` folder):
a CLI (`src/OptimusVibe.Cli`, `optimus-vibe.exe`, for scripting and for Claude), a shared core
(`src/OptimusVibe.Core`), and a WPF app (`src/OptimusVibe.App`, `OptimusVibe.exe`) for picking presets and
managing manual saves. The app is also the Steam launch-option wrapper: `OptimusVibe.exe run …` runs headless
(no window/console) with the same behaviour as `optimus-vibe run`. 
## Two repos (read this first)

| Repo | Visibility | Holds |
|---|---|---|
| **App repo** `jrose462/Optimus-Vibe` (this one) | public | code, docs, the `game-settings-recommender` skill, the generic starter config in `seed/config/` (embedded into Core and used to seed new config repos / empty folders), GitHub Releases, bug-report issues |
| **Config repo** (one per user, e.g. `optimus-vibe-config`) | private | `config/devices.yaml`, `config/games/*.yaml`, `config/schema/`, `config/saves/`, `config/library.yaml`, `config/requests.yaml` |

This repo must never contain user-specific data: no personal paths, hardware model names, account/Steam ids, emails,
game configs or saves. Tests build their own fixtures. On first run the app asks "Where should your game settings
live?": **GitHub** (sign in with a device code, then "Create my config repo" (default name `optimus-vibe-config`,
private, seeded from `seed/config/`) or pick an existing repo) or a **local folder** (an existing git checkout, or
any folder; an empty one is offered the starter set). GitHub sign-in = a private config repo to store settings in, plus being
the author of your bug reports. No sign-in = a local folder, and reports filed via the built-in token. Updates need no sign-in
(the app repo is public). There is no default config repo:
`AppSettings.GitHubRepo` is null until the user chooses.

The usual job when a *user* opens Claude in their **config repo** is **adding a game or refreshing its recommended
presets** (the skill below). In *this* repo the job is code: run `dotnet test` and keep the handlers' round-trip tests
green.

**Bug reports.** "Report a problem" / `optimus-vibe report-bug` file the issue as the signed-in GitHub user when there is a sign-in token
(`GitHubAuth.LoadToken()`; they become the author), else with the built-in fine-grained, issues-only token (the owner's PAT, injected at build time
from the `BUG_REPORT_TOKEN` secret, XOR-obfuscated, extractable: see `docs/ci.md`), else by copying the report and opening a prefilled
`issues/new` page. The UI/CLI always say which ("Filed as @login" / "Filed anonymously via Optimus-Vibe"). Labels are tried and dropped when refused;
every report body starts with the hidden marker `<!-- optimus-vibe:report v1 -->`, which is what the triage workflow keys on (it adds the
`bug-report` label itself). At most 5 reports a day per machine. Never log or
display the token.

## Adding or refreshing a game: use the skill

**The way to add a game or refresh its recommendations is the `game-settings-recommender` skill**
(`.claude/skills/game-settings-recommender/SKILL.md` in this app repo, mirrored to `~/.claude/skills/`; the app repo copy is the
source of truth). The skill works **in the user's config repo** (`--config <config repo>\config`, commits go there) and reads
their hardware from that repo's `config/devices.yaml`. It covers identification, research, confirming keys with `optimus-vibe inspect`, writing
the four presets, validation, dry runs and the commit. Its `references/` hold the device specs, preset rules,
and engine notes. Do not hand-roll a game file without following it.

Every game file must carry `cloudSyncsSettings` (true/false from the Steam `remotecache.vdf` / store cloud check;
null only if truly unknown — `false` makes the app suggest opting the game out; confirm it with
`optimus-vibe cloud-check <id>`, which also reports observed cross-device changes) and, for non-Steam games,
`storeIds` (battlenet / ea / epic / gog / xbox) so the library scan can match the install.

## The library, the request queue and opt-outs

The app auto-detects installed games (Steam, Battle.net, EA app, Xbox app, Epic, GOG; `Library/` in Core) and
manages every detected game that has presets, unless the user opted it out. For managed Steam games it writes the
launch option `"<path>\OptimusVibe.exe" run <id> -- %command%` itself while Steam is closed
(`writeSteamLaunchOptions` in settings.json turns that off).

- **`config/requests.yaml` (config repo) is your work queue.** The app appends every installed game that has no config
  (`store`, `storeId`, `name`, `installDir`, `exes`, `requested`, `device`). When the user says "process
  Optimus-Vibe requests" / "work through the game queue" (or at the start of a session where they ask what to do
  next and the queue is non-empty), process it with the skill's "process the request queue" workflow: one
  commit per game that adds `config/games/<id>.yaml` and removes that entry. Leave entries you could not map and
  tell the user why. Only ever remove handled entries; never reorder or rewrite the rest.
- **`config/library.yaml` is user-owned.** Its `optOut:` list (`store:id` keys) records games the user does not
  want managed. Never add or remove entries unless the user explicitly asks; suggest `optimus-vibe opt-out <key>`
  instead.
- Sync: `git` provider = a checkout synced with the git CLI (desktop); `github` provider = a managed copy under
  `%LOCALAPPDATA%\OptimusVibe\repos\` synced through the GitHub API after device-code/PAT sign-in (the Ally; no
  git). Both push to the user's config repo, so pull before editing the queue.
- Config work and `library.yaml`/`requests.yaml` edits always go to the config repo, never the app repo.

## Devices

Devices are defined by the user in their config repo's `config/devices.yaml` (each with a free-text `description` of the
hardware, a `match` rule, `display`, `driver` and `defaultPreset`). The starter file (`seed/config/devices.yaml`) ships two
neutral examples: `desktop` (matched by an NVIDIA GPU wildcard, driver nvidia, defaultPreset `quality`) and `ally-x`
(matched by CPU `*Z2 Extreme*`, driver amd, defaultPreset `performance`), with comments telling the user to run
`optimus-vibe detect` and edit. Match a handheld on the CPU rather than an AMD GPU name, because a desktop with an AMD CPU
also reports a Radeon iGPU; virtual adapters (Apollo/SudoMaker, Sunshine, GlideX, Parsec) are filtered out by the detector.
First match wins, so keep the more specific device first. Never assume hardware: read `devices.yaml`.

## Presets and manual saves

- **Recommended presets** (Claude-written, app tab "Recommended"): `presets.<device>.<name>` in
  `config/games/<id>.yaml`. Conventional names: desktop `quality` / `performance`; ally-x `performance` /
  `battery`. Each has `label`, `description`, `targetFps`, usually `extends: base`, plus `display`, `nvidia`,
  (`amd`, not implemented yet — `docs/amd-adlx.md`) and file blocks. `profiles:` holds shared bases only (usually
  `base`); `extends` may only name a `profiles:` entry. `recommendation:` records date, game version and sources.
- **Manual saves** (user-made, app tab "Manual saves"): `config/saves/<game>/<device>/<slug>/save.yaml`
  (+ `<fileId>.snap` whole-file copies). Made with the app, the post-game prompt, or `optimus-vibe save`. They
  record every key any preset or profile mentions. The app/CLI commits and pushes them (`optimus-vibe sync`).
- **Which one applies**: explicit `--preset` / app choice → the remembered selection (per game per device, in
  `%LOCALAPPDATA%\OptimusVibe\selections.json`) → the device's `defaultPreset` → the device's first preset.

**Never edit, regenerate, or delete anything under `config/saves/`.** It holds the user's tuning. If a change to
a game file would break a save (renamed file id, removed key), tell the user and let them decide.

## Config layout (`config/` in the config repo; the starter copy is `seed/config/` here)

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
  `"C:\Tools\Optimus-Vibe\OptimusVibe.exe" run <id> -- %command%` for managed games (or the user sets it).
- **Battle.net**: there is no %command%, so set `launch.exe` to `{ProgramFilesX86}/Battle.net/Battle.net.exe`
  with `args: ["--exec=launch <code>"]` and `storeIds.battlenet: <code>`. Product codes: `Pro` = Overwatch,
  `Fen` = Diablo IV (look up others).
  Battle.net.exe exits right away, so `processes:` must list the game exe (e.g. `Overwatch.exe`,
  `Diablo IV.exe`). Some Blizzard settings are synced server-side and can overwrite local files. Note this in
  `notes:`. The user runs `optimus-vibe shortcut <id>` to get a .lnk, which can be added to Steam as a non-Steam game.
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
- Do not commit anything from `%LOCALAPPDATA%\OptimusVibe` (backups, logs, state, selections, settings.json).

## Public repo rules

This repo is PUBLIC (MIT). Never commit personal data (emails, user paths, Steam ids, machine names), tokens, game art/logos,
screenshots showing game art, or binaries; never copy code without a compatible license. Bug reports are public issues and
are sanitized in `BugReport.Sanitize` (see `PRIVACY.md`); extend it and its tests when a new kind of personal data can reach a
report. The bug-to-release loop is automated by Claude and CI: see `docs/pipeline.md`. Redistributed dependencies must be
listed in `THIRD_PARTY_NOTICES.md` when `PackageReference`s change.
