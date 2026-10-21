# Settings-store archetypes

Classify the game in the first minute (checklist in SKILL.md step 0) and handle it as below. Real launches (Map this
game, `probe`, Test preset / `run --verify`) outrank wiki claims.

Where the classification goes: when the app supports it (the config repo's `_template.yaml` and `config/schema/game.schema.json`
list `archetype` and `mappable`), they are real top-level game-file fields, next to `applyTiming` / `settleSeconds`:
`archetype: <value>` and `mappable: true|false`. Use exactly these values, never letters:
`plain-honoured | binary-truth | text-mirror | composite | local-cloud-split | first-run | registry | launcher-gated | server-held`.
If the template and schema in the config repo do not have them yet, write the same two lines as a header comment instead.
Items marked *(when available)* depend on app features that do not exist (sub-field patching, a keyed handler for non-GVAS binaries,
section patterns, derived-key drift filtering); do not invent syntax for them, describe the need in `notes:`.
Fields that do exist: `launch.args`, `launch.waitSeconds`, `launch.launcherProcesses`, `applyTiming`
(`before | afterProcessStart | both`), `settleSeconds`, `display: keep`, preset-level `launch: { args }`, and `snapshotsFrom`.

Classification questions:
1. Is the readable file rewritten on exit (stale/other-device values after a launch)?
2. Is it inside the saves path or listed in `remotecache.vdf` (cloud-synced)?
3. Is there a port-specific second file (e.g. DmC `QlocConfig.ini`, not `DevilEngine.ini`)?
4. Are there composite values (one key holding a struct/list)?
5. Does the launcher gate the start (stub, prompt, wrong launch verb)?

| Archetype | Signs | Handling | Examples |
|---|---|---|---|
| `plain-honoured`: plain text/xml/json store, honoured at boot | Readable file in AppData/Documents/Saved Games changes on exit; dry-run before a real launch shows diffs, dry-run after shows `up to date`; `run --verify` says `held`. Not in the Steam Auto-Cloud root. | Map every option as a key, never prose. One verify run per device before the preset is trusted. `cloudSyncsSettings: false`. Keys the game recomputes (BL4 `FrameRateLimit` while `LimitFrameRate=False`, `sg.EffectsQuality`) cannot be marked as ignored (no such field): leave them out, or accept that `verify` reports them as overridden, and say so in `notes:`. | armored-core-6, baldurs-gate-3, marvels-midnight-suns, overwatch-2, borderlands-4, diablo-iv, fortnite, hogwarts-legacy, tales-of-arise, cyberpunk2077 |
| `binary-truth`: binary/opaque save is truth; text file derived and rewritten at boot | Ini holds stale or other-device values after launch; a binary `.sav` in Saved Games or a cloud folder changes every boot; wiki config path is inside/next to the saves path; launcher (EA app, Steam) syncs it around launch. | **When the binary is an Unreal GVAS save (starts with `GVAS`; Jedi Survivor GameUserSettings.sav): map its properties with `handler: gvas`** (`optimus-vibe inspect <game>` lists every property path with its type; key syntax in the box below) and keep `autoSnapshot: perPreset` on the same entry: the device copy is restored first, then the preset keys are written on top, so keybinds/accessibility/language come from the copy and the graphics keys from the preset. Other binary formats: pivot on the binary with per-device x per-preset `autoSnapshot: perPreset` on a `handler: file` entry (kept per device automatically, no manual saves), or `snapshotsFrom` when a specific named manual save should be restored. Treat ini keys as advisory. Set `applyTiming: both` (or `afterProcessStart`) and `settleSeconds` so the preset is re-applied after the launcher's sync phase. Warn that a preset on one device is pushed to the other by cloud sync. Call it an auto-snapshot preset. | jedi-survivor, forspoken |
| `text-mirror`: text mirror enabled by a launch argument | Wiki "Command line arguments" lists a flag that writes a readable settings file (`-save_graphics_option_text`); without it only the opaque store exists; read-back untested. | 3-way experiment: edit mirror only; delete the opaque store; edit both. If the mirror is read, add the flag to `launch.args` (one token per entry) plus a `files:` entry; otherwise `mappable: false` and keep snapshots. | forspoken |
| `composite`: composite value inside an ini key | A key holds a packed struct/list (`GraphicsSettings=(("Name","Value"),...)`, `CameraSettings=(...)`, `FrameGeneration=(Mode=Off,...)`). | There is no sub-field patching: write the whole string copied from a real `inspect` output, record the source device, note order sensitivity. Sub-field patching *(when available)* would let base and preset layers each set a few fields. | borderlands-4, hogwarts-legacy |
| `local-cloud-split`: local file plus cloud-synced file split | Two files: one in `remotecache.vdf` (DOOMConfig.cfg), one local (DOOMConfig.local). Per-device values (resolution index, renderer, shadow atlas) live in the unsynced one. | Per-device keys in the local file. Keys that must go in the synced file will ping-pong between devices: use `applyTiming: both` and warn. Prefer launch args (`+set cvar value`, one token per entry in `launch.args`) over touching the synced file. | doom-2016 |
| `first-run`: file created on first run / seeded late | Map run lists the settings file as NEW; first launch skips with "launch the game once"; keys appear only after being changed in-game. | Ship a seed template (`createIfMissing` with a known skeleton) or write "launch once, then apply" guidance. Files that do not exist before the first run are applied as soon as they appear (up to 120 s). Look for a port-specific second file before trusting the engine ini. | dmc-devil-may-cry, aniimo |
| `registry`: registry PlayerPrefs (Unity) | `HKCU\Software\<Studio>\<Game>`, hash-suffixed value names, strings stored as hex bytes; wiki config row is an hkcu path. | Registry handler with hex-string values; `probe` to learn enum ints (`dlssModeInt`) and AA strings; verify per device (the Ally registry is not visible from the desktop). | aniimo |
| `launcher-gated`: launcher-gated start | Managed run times out at ~3 min or the game never starts; launcher log shows the family selected but no LaunchBinary line (Battle.net); EA app/Epic/Larian prompts; a launcher exe in front of the game exe. | Confirm launch from launcher logs or the process tree. Raise `launch.waitSeconds` (default 180; 600 for EA/Battle.net/Epic) and list the launcher in `launch.launcherProcesses` so a prompt reads "waiting on the launcher" instead of a failure; list the real game exe(s) in `processes:`. Per-game extra args (`--skip-launcher`, `--exec` variants) go in `launch.args`. Largest cause of "preset did not apply". | diablo-iv, jedi-survivor, baldurs-gate-3, fortnite, marvels-midnight-suns |
| `server-held`: server-held / versioned section | Wiki says settings/progress are server-side; the ini section name carries a version (`Render.13`); only graphics are local. | Map only the local graphics file; gameplay is out of scope. There is no section-pattern matching: name the current section and warn in `notes:` that a version bump (`Render.14`) needs a refresh. | overwatch-2, diablo-iv |

### `handler: gvas` key syntax (Unreal SaveGame, `.sav`)

- Keys are property paths: `ResolutionSizeX` (top level), `Struct.Member` (e.g. `Window.X` for an IntPoint struct), `Array[2]` (scalar element), `Array[2].Member`.
  Nested YAML maps join with `.` as for ini/json. Writable: Int/UInt/Int64/Int16/Int8, Float, Double, Bool, Byte, Enum (give the full `EnumType::Value`), Str, Name.
  Maps, sets, text and unknown structs are listed read-only by `inspect` and refused on write.
- **Unreal omits properties that equal their defaults**, so a quality level at its default value is simply absent from the file (Jedi Survivor: the seven
  `*Quality` ints are missing on the desktop copy, present on the Ally one). A key that may be absent needs a type hint after a colon so the handler can insert it:
  `ShadowQuality:int: 0` (hints: int uint int64 uint64 int16 uint16 int8 float double bool byte str name enum). Always hint properties you set; the hint is ignored on read
  and an error if it contradicts the type already in the file. The file must exist (the handler never creates a save).
- **Enums need their type in the hint**: `WindowMode:enum(EWindowMode::Type): EWindowMode::Fullscreen` (a namespaced enum is typed `EWindowMode::Type` but its values read `EWindowMode::Fullscreen`; an enum class `EFoo` has values `EFoo::Bar`), and enum-as-byte `Quality:byte(EFoo): EFoo::Bar`. A bare `:enum` insert is refused because the type cannot be told from the value. Copy the type from `inspect` (the bracketed type is only the property kind; the enum type is the one shown next to an existing value, or read the property's tag). An existing enum byte takes only a value name (`EFoo::Bar`), never a number.
- A fixed-size C array member shows in `inspect` as `Name`, `Name#1`, `Name#2`: address them with that key.
- One key the save cannot take (wrong type, container, unknown path without a hint) does not block the rest: the other keys are written and the plan/apply output names each refused key. Keys without a hint on a gvas entry draw a validator warning because the property may be absent from a save.
- Drift caveat: when a preset sets a property to its default value, the game may drop it again on exit. For gvas files that is logged as a note ("no longer in the save") and is not counted as an override, because the defaults are unknown; it is the game normalising the file, not a rejected value.
- Values the .sav does not hold (frame limit, window mode, HDR on Jedi Survivor) stay in their other file or in-game; do not invent properties.

Not an archetype: **a game that is not installed** (no AppData folder, no map run; keys are recollection or exe strings).
Do not guess an archetype. Write the comment `not installed: keys unverified`, put no keys in the presets (describe the plan
in `notes:` or in a `# TODO` comment), then rebuild from `inspect` after install and verify (cyberpunk2077 was like this).

A game can be several (BL4 is `plain-honoured` + `composite`; Jedi Survivor `binary-truth` + `launcher-gated`; Diablo IV
`plain-honoured` + `launcher-gated` + `server-held`). The `archetype` field takes the one that decides how the preset
reaches the game; the rest go in `notes:`.

## When nothing works: `flagged:` (set in-game)

Some games ignore every file and registry key Optimus-Vibe can write: an opaque store rewritten each boot, a binary save that
is the only truth, a server-held setting. Classify them `mappable: false` and, once real launches prove it, add a top-level
`flagged:` block to the game file:

```yaml
mappable: false
flagged:
  reason: the game rewrites its binary settings store on every boot   # required, one line, shown in the app
  since: "2026-10-20"
  evidence: Test preset overridden twice; mapping run changed only system.save   # no personal data
  issue: 12                  # the research-later issue in the config repo, when there is one
```

What "flagged" does ("settings off, rest on"): no preset file/registry writes, no manual-save restore, no `snapshotsFrom`, no
autoSnapshot restore or capture, no after-start re-apply, no post-session drift/capture prompt, no sync-check prompt, Test preset
off. Still applied: the display resolution switch, the NVIDIA driver profile, `launch.args`, the launch watchdog, Map this
game (it is how research gathers evidence) and the preset's in-game steps and notes. So keep writing a useful preset
`description` / `notes:` with the in-game steps, and keep `display:`, `nvidia:` and `launch:` in the presets.

Evidence that justifies it (all from real launches): (1) `run --verify` overridden in two Test preset results in a row (the app
counts the streak), (2) a mapping run whose pass B changed only binary files (no text key, no registry value), (3) no text mirror
or launch argument that makes the game read a file back. The app only *suggests* the flag (game page: "Flag as set in-game") and
files a `research-later` issue; the user decides. A user-made flag lives in `config/library.yaml` `setInGame:` (by game id) and
counts as much as this block; a lab-written `flagged:` can only be lifted by changing the game file.

`research-later` issues (no `game-lab` label, so the workflow does not start by itself) ask for deeper research later: see
`seed/config-repo/.github/game-lab/research-prompt.md`. Outcome (a): a writable store is found, so fix the mappings, remove
`flagged:` (set `mappable: true`) and say in the PR that the user should press **Manage settings again** if they flagged it
themselves. Outcome (b): still impossible, so keep `mappable: false` + `flagged:` with the reason, evidence and issue number and
close the issue with the findings.

## Trends across the mapped games

- The wiki's config path is not the file the game obeys in a sizeable share of cases (`binary-truth`,
  `local-cloud-split`, `first-run`). The dry-run/verify loop, not the wiki, decides.
- "Preset did not apply" is usually launcher/timing (`launcher-gated`, `binary-truth`), not wrong keys.
- Cloud sync and Steam `remotecache.vdf` membership decide whether a preset leaks to the other device.
- Prose `In-game:` steps are only acceptable for HDR, TDP, or options absent from every store.

## Launch arguments by engine

Prefer `launch.args` (one token per entry: `["+set", "r_x", "1"]`, not `["+set r_x 1"]`; a preset can add
`launch: { args }`, the same flag appears once and the preset wins) over editing a synced file. Confirm each flag on the
wiki's "Command line arguments" table first; unverified flags stay out.

How the args reach the game: Optimus-Vibe starts Steam games with `steam.exe -applaunch <id> <args>`. Do not rely on the
`steam://rungameid/<id>//args` URI form: it drops the args (tested). For a wrapped launch
(`OptimusVibe.exe run <id> -- %command%`) the app appends `launch.args` after `%command%`.

| Engine | Typical args |
|---|---|
| id Tech (DOOM 2016) | `+set <cvar> <value>`, `+exec <cfg>` |
| Unreal | `-nostartupmovies`, `-nosplash`, `-windowed`/`-fullscreen`, `-ResX= -ResY=`, game-specific text-mirror flags |
| REDengine (Cyberpunk) | `-fpsClamp=<n>` |
| Unity | `-screen-width`, `-screen-height`, `-screen-fullscreen 0/1`, `-popupwindow` |
