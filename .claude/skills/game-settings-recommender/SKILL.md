---
name: game-settings-recommender
description: >-
  Adds a game to GameProfiles or refreshes its recommended graphics presets — researches the game's settings
  file and real key names, confirms them on the machine with `gameprofiles inspect`, then writes
  config/games/<id>.yaml with per-device presets (desktop quality/performance, ally-x performance/battery),
  validates, dry-runs and commits. Use whenever the user says "add <game> to GameProfiles", "generate/recommend
  graphics settings for <game>", "make presets for <game>", "set up <game> for the Ally/desktop",
  "refresh recommendations" / "the game patched, update its presets", or runs /game-settings-recommender <game>.
  Also works through the app's queue of detected games without a config (config/requests.yaml) when the user says
  "process GameProfiles requests", "work through the game queue", "add the queued games", or runs
  /game-settings-recommender queue.
---

# Game settings recommender

GameProfiles (repo `<repo>`, C# .NET 8) writes per-device settings into a game's config files
on every launch. This skill produces the **Recommended** tab content in the app: `presets:` in
`config/games/<id>.yaml`. The user's own tweaks live in the **Manual saves** tab (`config/saves/`), which you
never touch. If invoked outside the repo, work in `<repo>` anyway. The app scans Steam,
Battle.net, EA, Xbox, Epic and GOG for installed games, matches them to configs by `steamAppId`/`storeIds`, and
queues games without a config in `config/requests.yaml` for you. `config/library.yaml` `optOut:` (user-owned)
lists games not to manage; `cloudSyncsSettings: false` makes the app suggest opting a game out.
Source of truth: this repo's `.claude\skills\game-settings-recommender\`; `~/.claude/skills/` holds a mirror
(edit the repo copy, then re-copy it).

Read before writing presets: `references/hardware.md` (what the two devices can do),
`references/preset-guidelines.md` (rules per preset, cost of settings, research sources),
`references/engine-notes.md` (where UE/Unity/Frostbite/etc. keep settings and the real key names),
`references/awkward-stores.md` (binary/opaque files, composite values, research fallbacks, store ids, cloud sync).
`scripts/strings.js` (strings view of binary files and exes). `<skill>` below = this skill's folder.
Repo docs: `CLAUDE.md`, `config/games/_template.yaml` (every field and handler syntax),
`config/games/cyberpunk2077.yaml` (worked example).

## Hard rules

1. **Never invent keys.** Every key in a preset must come from a cited source or from `inspect` output on the
   user's machine. A key you could not confirm on the machine gets `# TODO verify (<what is unsure>)` on its line;
   if a wrong value could break the game (resolution, window mode, a binary/enum value you are guessing the
   spelling of), leave it out of the presets entirely and mention it in `notes:`. `# TODO verify` also covers
   **behaviour**: a confirmed key the game may ignore or override (a duplicate in a second store, a key with no
   menu option such as UE `FrameRateLimit`, a quick-preset key that re-applies its own values on boot).
2. **Never touch `config/saves/`** (user-owned manual saves, git-synced by the app) — not even to "fix" them. If
   a save would conflict with your change (e.g. you renamed a file id), tell the user. Likewise never edit
   `config/library.yaml` unless the user asks; in `config/requests.yaml` only remove entries you have handled.
3. **Never change** FOV, gameplay/difficulty, controls/keybinds, HUD/subtitles/accessibility, audio, language,
   brightness/HDR calibration, or account/online settings. Graphics, display mode and framerate only.
4. `intent:` is the user's words only. If they gave none, write `EDIT ME (placeholder): ...` lines.
5. Within one device, **every preset sets the same keys** (directly or via `profiles.base`). Switching presets
   must never leave a value from the previous preset behind, and manual saves record every key presets mention.
6. Set the *other* vendor's upscaler/frame-gen explicitly **off** in every preset (Steam Cloud may sync the
   other machine's value). If the game has one vendor-neutral frame-gen key, setting it off on the Ally covers
   this. If those options live in a binary store GameProfiles cannot edit, rule 6 becomes an in-game
   instruction in the preset `description` and `notes:` (see `references/awkward-stores.md`).

## The data model (what you write)

```yaml
profiles:            # shared bases only, never applied directly, NOT named after devices
  base: { <fileId>: { ... } }          # keys identical on every device/preset + safe defaults
presets:
  desktop:
    quality:     { label, description, targetFps, extends: base, display: inherit, nvidia: {...}, <fileId>: {...} }
    performance: { ... }
  ally-x:
    performance: { ..., display: { width: 1920, height: 1080, refresh: 120 } }
    battery:     { ... }
recommendation: { generated: "YYYY-MM-DD", gameVersion: "...", sources: [ "...", ... ] }
```

Reserved preset keys: `extends` (must name a `profiles:` entry), `display`, `nvidia`, `amd` (not implemented —
leave commented), `label`, `description`, `targetFps`. Every other key is a file id from `files:`.
Device defaults (`devices.yaml` `defaultPreset`): desktop → `quality`, ally-x → `performance`.

## Workflow: add a game

Run commands from the repo root. `gp` below means the CLI: use `gameprofiles` if it is on PATH, else
`src\GameProfiles\bin\Debug\net8.0-windows\win-x64\gameprofiles.exe` (build it with
`dotnet build src/GameProfiles` if missing), or `dotnet run --project src/GameProfiles --`. Always pass
`--config <repo>\config` unless `gp setup` already points there.

### (a) Identify the game

- Exact name, store, and for Steam the app id:
  look for `appmanifest_<id>.acf` in each library's `steamapps\` (libraries are listed in
  `C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf`; `grep -l '"name".*<Game>' .../appmanifest_*.acf`),
  or search steamdb.info / the store URL. `store:` takes what `Validator.cs` `Stores` accepts (today `steam`,
  `battlenet`, `ea`, `other`; Epic/GOG/Xbox games use `other` until it accepts their names).
- **`storeIds:`** (required for every non-Steam game; also add ids for other launchers a Steam game shows up in,
  e.g. a Steam EA game's EA offer id): keys `battlenet` (product code), `ea` (offer id), `epic` (AppName), `gog`
  (game id, quoted), `xbox` (package family name). Without the right id the library scan cannot match the install
  to your config and keeps queueing it. Where to find each: `references/awkward-stores.md` ("Store ids");
  `gp library` lists what the scanner detected, with each `store:id` key and exes.
- The engine (PCGamingWiki infobox, or the install folder: `Engine\` + `<Proj>\Binaries\Win64\` = Unreal;
  `*_Data\` + `UnityPlayer.dll` = Unity).
- Process names for `processes:` — the real game exe, not only the launcher stub. Unreal games are often NOT
  `<Proj>-Win64-Shipping.exe` (e.g. `JediSurvivor.exe`, `Borderlands4.exe`): list the exe(s) in
  `<Proj>\Binaries\Win64\` **and** the stub in the install root if it differs, or check Task Manager in-game.
- Game id: **kebab-case** — lowercase words joined by `-`, numbers as their own word (`jedi-survivor`,
  `borderlands-4`, `cyberpunk2077` predates this and stays); the file name must match.

### (b) Research the settings store

- Find the config file location and format, and every graphics key with its allowed values. Start with
  PCGamingWiki ("Configuration file(s) location", "Video" section), then engine notes in
  `references/engine-notes.md`, then optimized-settings guides (Digital Foundry, BenchmarKing, Hardware Unboxed),
  Steam Deck / ROG Ally community guides, and GitHub/Nexus mods that edit the file. Note each URL — they go into
  `recommendation.sources`.
- Watch for split stores (file + registry, ini + binary `.sav`), per-account folders (use a `*` path segment),
  settings kept server-side, and settings living in a game-specific ini section rather than the engine's.
- Unreal: check **both** `{LocalAppData}/<Project>/Saved/Config/<Windows|WindowsNoEditor>/` and
  `{Documents}/My Games/<Game>/Saved/Config/Windows/` (and `{SavedGames}`); see `references/engine-notes.md`.
- Sites often 403 to WebFetch (PCGamingWiki, TechSpot, Nexus): use WebSearch snippets, other mirrors, or the
  game exe's string table (names only) — and record what could not be fetched (`references/awkward-stores.md`).
- **`cloudSyncsSettings:`** (required, every game): is any graphics settings store cloud-synced? Steam: is it
  listed in `<Steam>\userdata\<id>\<appid>\remotecache.vdf`; other stores: `references/awkward-stores.md`
  ("Cloud sync check"). `true`/`false`; `null` only if truly uncheckable (say why). Evidence (file + date) goes in
  a comment above the key and in `notes:`. `false` makes the app suggest opting the game out (each machine
  already keeps its own settings), so get it right.

### (c) Confirm the real keys on the machine

1. Write a minimal `config/games/<id>.yaml`: header comment, identity (incl. `storeIds`, `cloudSyncsSettings`),
   `processes`, `files:` (correct handler and path tokens), no presets yet.
2. Run `gp inspect <id>` — it prints the resolved path and the file's contents (`--full` for long files). If the
   file is MISSING, the game has not been launched on this machine yet: ask the user to start it once (reach the
   main menu, change any graphics option, quit) and re-run. You may also read the file directly.
3. Use **exactly** the section names, key names, value types and spellings the file shows (e.g. `sg.ShadowQuality=3`
   is an int, `bUseVSync=False` a capitalized bool, UE floats like `FrameRateLimit=60.000000`). Keys the game
   writes only after an option is changed in-game may be absent: confirm them by toggling the option in-game and
   re-inspecting, or mark them `# TODO verify`.
4. If you are on the desktop, the Ally X file is not visible; same game version = same keys, but values like
   resolution may differ — note that in comments.
5. Binary/opaque stores (e.g. a UE `GameUserSettings.sav`): `inspect` dumps them as garbled text — expected.
   Map them as `handler: file` (manual saves snapshot them whole) and handle their options with in-game steps.
   Composite values (one key holding a struct/list, e.g. `GraphicsSettings=(("Name", "Value"),...)`): write the
   whole value exactly as on disk. Both are detailed in `references/awkward-stores.md`.

### (d) Write the presets

Read `references/hardware.md` and `references/preset-guidelines.md` first, then:

- `profiles.base` holds two kinds of keys: (1) keys with the same value on all four presets (motion blur/film
  grain/CA off, vsync off, window mode — **borderless** unless the game needs exclusive fullscreen) and (2) a
  **safe default for every toggle that some preset turns on** (RT off, frame gen off, Ray Reconstruction off), so
  presets only override. It is not a place for device-specific values.
- `presets.desktop.quality` and `.performance`; `presets.ally-x.performance` and `.battery`. Each gets
  `label` (Title case, the preset name unless a better one fits), `description` (1-2 sentences: what it targets
  and what it trades away; ally-x battery must say to pair it with Armoury Crate Silent ~13 W, since
  GameProfiles cannot set TDP; plus any in-game steps for settings in a binary store), `targetFps` (integer),
  `extends: base`, `display`, and the file blocks.
- **Honest targets**: if the game cannot hold 60 on the Ally X, set ally-x `performance` `targetFps` lower
  (e.g. 45) and say so in its description. Caps and the VRR-floor policy are in `preset-guidelines.md`
  ("Frame caps"); the Ally X always keeps `refresh: 120`.
- Desktop: `display: inherit`; `nvidia: { FrameRateLimiter: 162 }`. With frame generation on, rely on the driver
  cap/Reflex and leave the in-game limiter off (it can cap the *base* framerate).
  Ally X: `display: { width: 1920, height: 1080, refresh: 120 }`; leave `amd:` commented out.
- Put a short comment next to any non-obvious value, and `# TODO verify` where (c) could not confirm.
- Keep the `# yaml-language-server: $schema=../schema/game.schema.json` header and a header comment naming the
  file location, format, and what was verified vs not.

### (e) intent, recommendation, notes

- `intent:` per device — the user's words, or `EDIT ME (placeholder): ...`.
- `recommendation:` — `generated:` today's date (`YYYY-MM-DD`, quoted), `gameVersion:` the installed build if
  known (Steam: `buildid` in the appmanifest, or the version on the main menu; else `"unknown"`), `sources:` one
  string per source (title + URL, plus "`gameprofiles inspect <id>` on <device>, <date>").
  Name sources you could not fetch (e.g. "PCGamingWiki: 403 to WebFetch, used search snippet").
- `notes:` cloud-sync result (matches `cloudSyncsSettings`), settings that need a restart, files the game resets on
  boot (`readOnlyAfterApply`), per-device in-game steps for binary-store settings, anything left unverified.

### (f) Validate and dry-run

```powershell
gp validate --config <repo>\config
gp apply <id> --preset quality --dry-run                       # on the desktop
gp apply <id> --preset performance --dry-run
gp apply <id> --profile ally-x --preset performance --dry-run  # Ally presets, previewed from the desktop
gp apply <id> --profile ally-x --preset battery --dry-run
gp presets <id>; gp presets <id> --profile ally-x               # labels, fps targets, [device default]
```

- `validate` must report 0 errors; fix warnings unless they are expected (explain any you keep).
- The dry run prints **only keys that would change** (`current -> target`); a file with nothing to change says
  `up to date`. Success = no `(missing)` as a current value (and, for json, no error). `(missing)` means the key
  or path is not in the file: re-check spelling. Consequences differ by handler — ini/keyvalue **append** the
  key (a typo silently adds a dead key), json fails `apply` for that file when an array filter matches nothing,
  xml never creates nodes. If a key is genuinely absent until changed in-game, keep it only with
  `# TODO verify` and say so.
- `user: skipped — not found` means the settings file does not exist on this machine (game never launched here,
  or you are previewing a file that only exists on the other device); the key diff cannot be checked then.
- Optionally schema-check with node + ajv against `config/schema/game.schema.json` (no python on this machine).
- Do **not** run a real `apply` unless the user asks; they test in-game.

### (g) Saves

Do not create, edit, or delete anything under `config/saves/`. Do not run `gp save`, `gp delete-save`, or
`gp use` on the user's behalf unless asked.

### (h) Commit and push

Stage only the game file (plus `config/requests.yaml` when the game came from the queue, and any doc you were
asked to change): `git add config/games/<id>.yaml`, commit `feat(games): add <id> recommendations` (refresh:
`feat(games): refresh <id> recommendations`, fix: `fix(games): ...`). End the message with whatever attribution
trailers the session requires (e.g. `Co-Authored-By:` / `Claude-Session:` lines from the system reminder). Then
`git pull --rebase` and `git push`. If the push fails, report it; never force-push. If you were told someone
else commits (e.g. you are a subagent), skip this step and say so.

Finish with a short report: presets and their targets, keys still `# TODO verify`, what the user should test
in-game, and the sources.

## Workflow: refresh (after a patch, or "refresh recommendations")

1. Read the existing `config/games/<id>.yaml`, its `recommendation:` block and `notes:`.
2. Re-run (b) for patch notes that add/rename/remove graphics options (new upscaler versions, new presets,
   moved files) and (c) `gp inspect <id>` to diff the live file against the mapped keys.
3. Update keys and values; resolve `# TODO verify` items that inspect now confirms; keep preset names stable
   (the app remembers selections by name — renaming one silently falls back to the device default).
4. Do not delete a file id or key that manual saves use without telling the user: list
   `config/saves/<id>/*/*/save.yaml` (read-only) to see which keys saves rely on.
5. Bump `recommendation.generated`/`gameVersion`, update `sources`, then (f) and (h) with
   `feat(games): refresh <id> recommendations`.

If the user asks to refresh all games, do one game per commit. Existing configs missing `cloudSyncsSettings` or
(non-Steam) `storeIds` get them filled in on refresh.

## Workflow: process the request queue

`config/requests.yaml` is written by the app's library scan: one entry per installed game with no config
(`store`, `storeId`, `name`, `installDir`, `exes`, `requested` date, `device` it was seen on).

1. `git pull --rebase` first (other machines queue games too), then read `config/requests.yaml`. Empty → say
   so and stop. Otherwise list the queue (name, store, device) before starting.
2. For each entry, in file order, run **Workflow: add a game** with the entry as step (a):
   - Identity: steam → `store: steam` + `steamAppId: <storeId>`; else the validator-accepted `store:` (see (a))
     + `storeIds.<store>: "<storeId>"` copied **exactly** (the scanner matches on it). Add other launchers' ids too.
   - `installDir`: where to find the engine (`Engine\`, `*_Data\`) and the exe string table; `exes` seeds
     `processes:` (still check launcher stub vs real game exe).
   - `device` other than the one you run on: `gp inspect` cannot see its file, so confirm keys from sources and
     the exe, mark the rest `# TODO verify`, and tell the user to run `gp inspect <id>` on that device.
   - A config for the game already exists but lacks this id: add the `steamAppId`/`storeIds` to it instead of
     creating a duplicate.
3. In the **same commit** as the game file, delete exactly that entry from `config/requests.yaml` (keep the
   header and other entries; `requests: []` when empty). One game per commit
   (`feat(games): add <id> recommendations`), then pull --rebase and push.
4. **Cannot be mapped** (no identifiable settings store, settings only server-side, no confirmable keys, not a
   game — e.g. a tool or launcher): write no config and leave the entry untouched (`GameRequest` has no note
   field). Stop and report to the user: which entry, why, and options (skip it and continue; for non-games
   `gp opt-out <store>:<id>`, which you run only if they say so). Also stop on an unfixable validation error or a
   failed push.

Final report: games added (id, presets/targets, `cloudSyncsSettings` + evidence, TODO-verify items, which device
must run `inspect`), entries still queued with the reason, and suggested opt-outs.
