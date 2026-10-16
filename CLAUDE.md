# GameProfiles: notes for Claude

GameProfiles is a C# .NET 8 Windows CLI (`src/GameProfiles`) that applies per-device game settings from YAML
in `config/`. The usual job in this repo is **adding or tuning a game**. Code changes follow normal practice:
run `dotnet test` and keep the handlers' round-trip tests green.

## Devices

| Device id | Hardware | Display | Driver |
|---|---|---|---|
| `desktop` | RTX GPU, desktop Ryzen CPU (it also exposes an "AMD Radeon(TM) Graphics" iGPU) | <desktop resolution> @ 165 Hz | nvidia |
| `ally-x` | ROG Xbox Ally X, Ryzen AI Z2 Extreme, Radeon iGPU | 1920x1080 @ 120 Hz VRR | amd |

`config/devices.yaml` matches the Ally X on the CPU (`*Z2 Extreme*`), never on an AMD GPU name, because the
desktop also reports a Radeon iGPU. Virtual adapters (Apollo/SudoMaker, Sunshine, GlideX, Parsec) are filtered
out by the detector. First match wins, so keep `ally-x` above `desktop`.

## Config layout (`config/`)

| Path | Owner | What |
|---|---|---|
| `devices.yaml` | Claude/user | Device match rules, default display, driver |
| `games/<id>.yaml` | **Claude** | One file per game. `_template.yaml` documents every field and handler syntax |
| `schema/game.schema.json` | Claude | JSON Schema for game YAML; keep it in sync with `Config/Models.cs` |
| `captures/<id>/<device>.yaml` | **User (via `capture`)** | Overlay merged on top of `profiles.<device>` |
| `snapshots/<id>/<device>/<fileId>.snap` | **User (via `capture --snapshot`)** | Whole-file copies, restored instead of keys |

**Never edit, regenerate, or delete anything under `captures/` or `snapshots/`.** They hold the user's in-game
tuning. If a capture conflicts with a change you are making to a game file, tell the user and let them decide.

## Adding a game

1. **Ask the user** for the game, the store (Steam, Battle.net, EA app...), and their intent for each device in
   their own words (e.g. desktop "max visuals, 100+ fps"; ally-x "steady 60, battery over visuals"). Do not
   invent intent. If they have none yet, write `EDIT ME` placeholders in `intent:`.
2. **Research the settings store**: file location, format (ini/json/xml/registry/line-based/binary), and the
   real key names and value spellings. Good sources are PCGamingWiki ("Configuration file(s) location"), the
   game's modding communities, and GitHub scripts that edit the file. Note which store keeps which setting:
   some games split graphics between a file and the registry, and some keep settings server-side.
3. **See the real keys on the user's machine.** Ask the user to run `gameprofiles inspect <id>`, or run it yourself
   if you have shell access to their machine. It prints resolved paths and current file contents. A minimal game
   file with `files:` and no `profiles:` is enough to inspect. Prefer what `inspect` shows over web sources. If
   you cannot verify a key, keep it but add `# TODO verify` to the line. Never guess silently.
4. **Write `config/games/<id>.yaml`** from `_template.yaml`:
   - `id` = file name; `processes` = the real game exe (launcher stubs exit early).
   - Put shared options in a `base` profile; `desktop` and `ally-x` extend it.
   - Map upscalers by vendor: DLSS (+ frame gen, Ray Reconstruction) on `desktop`; FSR/XeSS on `ally-x`.
     Explicitly turn the other device's upscaler **off** in each profile, because Steam Cloud may sync the other
     machine's value.
   - `display:`: `inherit` (device default), `none`, or a `{width,height,refresh}` map. `nvidia:` (e.g.
     `FrameRateLimiter`) is a persistent per-exe DRS profile. `amd:` is not implemented yet (see
     `docs/amd-adlx.md`). Leave it commented out unless the user asks.
   - Prefer full-path keys for JSON (the same form `capture` writes), e.g.
     `"data[group_name=/video/display].options[name=Resolution].value": "1920x1080"`.
   - Keep the `# yaml-language-server: $schema=../schema/game.schema.json` header.
5. **Validate**: `gameprofiles validate`, then `gameprofiles apply <id> --dry-run` (add `--profile ally-x` to
   preview the other device). The dry run prints a per-key diff; a key that resolves to nothing shows up here.
   Have the user apply for real, check in-game, and use `restore <id> --original` if something is wrong.

### Store-specific launch notes

- **Steam**: no `launch:` needed. The user sets the game's launch option to
  `"C:\Tools\GameProfiles\gameprofiles.exe" run <id> -- %command%`.
- **Battle.net**: there is no %command%, so set `launch.exe` to `{ProgramFilesX86}/Battle.net/Battle.net.exe`
  with `args: ["--exec=launch <code>"]`. Product codes: `Pro` = Overwatch, `Fen` = Diablo IV (look up others).
  Battle.net.exe exits right away, so `processes:` must list the game exe (e.g. `Overwatch.exe`,
  `Diablo IV.exe`). Some Blizzard settings are synced server-side and can overwrite local files. Note this in
  `notes:`. The user runs `gameprofiles shortcut <id>` to get a .lnk, which can be added to Steam as a non-Steam game.
- **EA app**: launch through the EA app URI or the game exe (the EA app must be running/signed in). Find the exact
  form for the game and record it in `notes:`. Use `shortcut` as for Battle.net.

### Caveats to record in `notes:`

- **Steam Cloud** can sync a config file between the desktop and the Ally X. That is the reason this tool exists.
  `run` re-applies the device's settings on every launch, but values the profile does not set still travel. Set
  every option that differs between devices in both profiles.
- Games that reset settings on boot: `readOnlyAfterApply: true` (in-game changes then do not save).
- Anything you could not verify, plus quirks (settings that apply only after a restart, launcher behaviour).

## Commits and diffs

- Keep diffs reviewable: one game per commit, no reformatting of unrelated files, comments next to non-obvious values.
- Conventional Commits: `feat(games): add cyberpunk2077 profiles`, `fix(games): correct overwatch render scale key`,
  `chore(devices): tighten ally-x match`, `docs: ...`, `feat(handlers): ...`, `test: ...`.
- Do not commit anything from `%LOCALAPPDATA%\GameProfiles` (backups, logs, state, settings.json).
