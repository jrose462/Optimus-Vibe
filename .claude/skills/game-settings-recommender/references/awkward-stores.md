# Awkward stores, composite values, research fallbacks, Steam Cloud

## Binary / opaque settings files

Some games keep graphics options in a binary file. Example: Star Wars Jedi: Survivor keeps the upscaler,
frame generation, ray tracing, motion blur, film grain and chromatic aberration in a binary UE save,
`{SavedGames}/Respawn/JediSurvivor/GameUserSettings.sav`. Its text ini only has some of the `sg.*` keys.

- **Recognise it**: `gp inspect <id>` prints the file as text, so you get mojibake with readable fragments. That
  is expected and not an error. To see option names, use a strings view instead. `strings` is not installed on
  this machine, so use the bundled script: `node <skill>/scripts/strings.js <file> [regex]`. It prints ASCII and
  UTF-16 strings of 6 or more characters.
  UE `.sav` (GVAS) files show property names followed by type names like `BoolProperty` / `IntProperty` / `EnumProperty`.
- **Map it as `handler: file`** with its own id (e.g. `sav`). Presets cannot set anything in it (the validator
  rejects keys), but manual saves snapshot it whole (`<fileId>.snap`), so once the user tunes in-game and saves,
  the setup is restored on every launch.
- **Never hand-edit binary values.** Wrong offsets or lengths corrupt the file.
- **In-game steps instead of keys**: put the settings the user must change in-game into each preset's
  `description`, e.g. "In-game: Upscaler FSR 3 Performance, Frame Gen off, RT off; then save a manual save." Also
  list them per device in `notes:`. Rule 6 (other vendor's upscaler off) then becomes one of these steps.
- Keep the rest of the presets on the text stores as normal. Mark keys that the binary store may override with
  `# TODO verify (binary .sav may override)`.

## Composite / struct values in one key

Example: Borderlands 4 writes about 19 settings as one ini value:
`GraphicsSettings=(("ShadowQuality", "High"), ("FoliageDensity", "Medium"), ...)`. UE struct and array
properties in general look like `Key=(A=1,B=(C="x"))` or repeated `+Key=...` lines.

- The ini handler treats the whole value as one string, so a preset **writes the complete value**.
- Copy the game's own **order, spacing, quoting and casing** exactly from the file on disk. Change only the pair
  values you mean to change.
- Use only spellings you observed, either on disk (after toggling the option in-game and re-inspecting) or in
  the exe's string table. If you cannot confirm a value spelling for an entry, leave that entry at the value seen
  on disk and do not guess.
- Trade-off, to note in a comment: the whole value is rewritten for every preset. A new entry added by a patch,
  or a pair the user changed in-game, is overwritten until the preset is refreshed. Manual saves still capture
  the whole value, so in-game tuning survives as a save.
- Repeated `+Key=` array lines cannot be expressed as separate keys. Leave them out of presets and rely on manual
  saves (note it).

## Research fallbacks

- PCGamingWiki, TechSpot, Nexus Mods and some forums often return **403 to WebFetch**. Use WebSearch result
  snippets, other mirrors (Steam Community guides, Reddit, GitHub code search, Fandom wikis, archive.org copies),
  or the game's own files.
- **Exe string table** (key names only, never values): UE games embed config property names as UTF-16 strings.
  `node <skill>/scripts/strings.js "<Proj>/Binaries/Win64/<Game>.exe" "upscal|framegen|dlss|fsr|xess|raytrac|motionblur"`
  gives candidate names. A name found only here still gets `# TODO verify` until it has been seen in the config file.
- In `recommendation.sources`, record what you could not fetch and what you used instead, e.g.
  `"PCGamingWiki Jedi Survivor (403 to WebFetch; file paths taken from search snippet)"`. Repeat any material
  gap in `notes:`.

## Steam Cloud check

Steam lists the files it syncs for a game in `<Steam>\userdata\<accountid>\<appid>\remotecache.vdf`. `<Steam>`
is usually `C:\Program Files (x86)\Steam`, and `<accountid>` is the numeric folder; there may be several.

- `grep -i -E 'ini|json|sav|cfg|settings' remotecache.vdf`. If the settings file (or its folder) is listed, it
  is cloud-synced between the desktop and the Ally X.
- Record the result in `notes:`: "Steam Cloud syncs GameUserSettings.ini (remotecache.vdf, <date>)" or "not in
  remotecache.vdf, so not cloud-synced". If the file has not been checked (game never launched, no userdata),
  say so.
- If it is synced, every per-device difference must be set in every preset, which is already the rule.
