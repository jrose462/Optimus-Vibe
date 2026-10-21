# Settings-store archetypes

Classify the game in the first minute (checklist in SKILL.md step 0), write `archetype:` and `mappable:` into the
yaml header comment before any preset, and handle it as below. Real launches (Map this game, `probe`, Test preset /
`run --verify`) outrank wiki claims. Items marked *(when available)* depend on app features still being built; do not
invent syntax for them, describe the need in `notes:` instead.

Classification questions:
1. Is the readable file rewritten on exit (stale/other-device values after a launch)?
2. Is it inside the saves path or listed in `remotecache.vdf` (cloud-synced)?
3. Is there a port-specific second file (e.g. DmC `QlocConfig.ini`, not `DevilEngine.ini`)?
4. Are there composite values (one key holding a struct/list)?
5. Does the launcher gate the start (stub, prompt, wrong launch verb)?

| Archetype | Signs | Handling | Examples |
|---|---|---|---|
| A. Plain text/xml/json store, honoured at boot | Readable file in AppData/Documents/Saved Games changes on exit; dry-run before a real launch shows diffs, dry-run after shows `up to date`; `run --verify` says `held`. Not in the Steam Auto-Cloud root. | Map every option as a key, never prose. One verify run per device before the preset is trusted. `cloudSyncsSettings: false`. Keys the game recomputes (BL4 `FrameRateLimit` while `LimitFrameRate=False`, `sg.EffectsQuality`) are derived: mark them so drift checks ignore them *(when available)*. | armored-core-6, baldurs-gate-3, marvels-midnight-suns, overwatch-2, borderlands-4, diablo-iv, fortnite, hogwarts-legacy, tales-of-arise, cyberpunk2077 |
| B. Binary/opaque save is truth; text file derived and rewritten at boot | Ini holds stale or other-device values after launch; a binary `.sav` in Saved Games or a cloud folder changes every boot; wiki config path is inside/next to the saves path; launcher (EA app, Steam) syncs it around launch. | Pivot on the binary: a keyed handler (`gvas` for UE GVAS) *(when available)* or per-device x per-preset `snapshotsFrom` manual saves. Treat ini keys as advisory. Apply after the launcher sync phase (on game-exe detection), then re-apply once *(when available)*. Warn that a preset on one device is pushed to the other by cloud sync. Call it a snapshot preset. | jedi-survivor, forspoken |
| C. Text mirror enabled by a launch argument | Wiki "Command line arguments" lists a flag that writes a readable settings file (`-save_graphics_option_text`); without it only the opaque store exists; read-back untested. | 3-way experiment: edit mirror only; delete the opaque store; edit both. If the mirror is read, add the launch arg *(when available: extra game args on every route incl. Steam)* plus a `files:` entry; otherwise `mappable: false` and keep snapshots. | forspoken |
| D. Composite value inside an ini key | A key holds a packed struct/list (`GraphicsSettings=(("Name","Value"),...)`, `CameraSettings=(...)`, `FrameGeneration=(Mode=Off,...)`). | Until sub-field patching exists, write the whole string copied from a real `inspect` output, record the source device, note order sensitivity. Sub-field patching *(when available)* will let base and preset layers each set a few fields. | borderlands-4, hogwarts-legacy |
| E. Local file plus cloud-synced file split | Two files: one in `remotecache.vdf` (DOOMConfig.cfg), one local (DOOMConfig.local). Per-device values (resolution index, renderer, shadow atlas) live in the unsynced one. | Per-device keys in the local file. Keys that must go in the synced file will ping-pong between devices: re-apply every launch and warn. Prefer launch args (`+set cvar value`) over touching the synced file *(when available)*. | doom-2016 |
| F. File created on first run / seeded late | Map run lists the settings file as NEW; first launch skips with "launch the game once"; keys appear only after being changed in-game. | Ship a seed template (`createIfMissing` with a known skeleton) or write "launch once, then apply" guidance. Registry `createIfMissing` on absent values *(when available)*. Look for a port-specific second file before trusting the engine ini. | dmc-devil-may-cry, aniimo |
| G. Registry PlayerPrefs (Unity) | `HKCU\Software\<Studio>\<Game>`, hash-suffixed value names, strings stored as hex bytes; wiki config row is an hkcu path. | Registry handler with hex-string values; `probe` to learn enum ints (`dlssModeInt`) and AA strings; verify per device (the Ally registry is not visible from the desktop). | aniimo |
| H. Launcher-gated start | Managed run times out at ~3 min or the game never starts; launcher log shows the family selected but no LaunchBinary line (Battle.net); EA app/Epic/Larian prompts; a launcher exe in front of the game exe. | Confirm launch from launcher logs or the process tree, fail fast with a real reason, wait longer for prompt-prone launchers, list stub launchers in `processes:` *(launch watchdog when available)*. Per-game extra args (`--skip-launcher`, `--exec` variants). Largest cause of "preset did not apply". | diablo-iv, jedi-survivor, baldurs-gate-3, fortnite, marvels-midnight-suns |
| I. Server-held / versioned section | Wiki says settings/progress are server-side; the ini section name carries a version (`Render.13`); only graphics are local. | Map only the local graphics file; gameplay is out of scope. Match the section by pattern (`Render.*`) *(when available)* and warn when the named section is absent but a sibling version exists. | overwatch-2, diablo-iv |
| J. Not installed / unverified names | No AppData folder, no map run; keys are recollection or exe strings. | Do not ship unverified keys as presets; mark `needsInstallToValidate`, rebuild keys from `inspect` after install, then verify. | cyberpunk2077 |

A game can be several (BL4 is A + D; Jedi Survivor B + H; Diablo IV A + H + I).

## Trends across the mapped games

- The wiki's config path is not the file the game obeys in a sizeable share of cases (B, E, F). The dry-run/verify
  loop, not the wiki, decides.
- "Preset did not apply" is usually launcher/timing (H, B), not wrong keys.
- Cloud sync and Steam `remotecache.vdf` membership decide whether a preset leaks to the other device.
- Prose `In-game:` steps are only acceptable for HDR, TDP, or options absent from every store.

## Launch arguments by engine

Prefer args over editing a synced file once extra game args land *(when available)*. Confirm each flag on the wiki's
"Command line arguments" table first; unverified flags stay out.

| Engine | Typical args |
|---|---|
| id Tech (DOOM 2016) | `+set <cvar> <value>`, `+exec <cfg>` |
| Unreal | `-nostartupmovies`, `-nosplash`, `-windowed`/`-fullscreen`, `-ResX= -ResY=`, game-specific text-mirror flags |
| REDengine (Cyberpunk) | `-fpsClamp=<n>` |
| Unity | `-screen-width`, `-screen-height`, `-screen-fullscreen 0/1`, `-popupwindow` |
