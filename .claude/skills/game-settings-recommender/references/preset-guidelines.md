# Preset guidelines

Six presets per game: two per device for its own screen, plus `big-picture` on each device for a big TV. Names are
fixed (the app orders and remembers them by name). `desktop` and `ally-x` are the conventional device ids from the
starter `devices.yaml`; use the ids and hardware in the user's own `config/devices.yaml` (a desktop-class device gets
the desktop rows, a handheld the ally-x rows). Fps caps quoted below as 162 assume a 165 Hz display: use the device
refresh rate minus 3. The TV is described by the device's `bigPicture` block in `devices.yaml` when present;
otherwise assume a 4K (3840x2160) 120 Hz TV with VRR and HDR viewed from a couch.

| Device | Preset | Goal | targetFps | Typical cap |
|---|---|---|---|---|
| desktop | `quality` (default) | Best visuals at a smooth framerate | 100-120 (90 acceptable for path tracing showcases) | 162 |
| desktop | `performance` | High fps / competitive | 162 | 162 |
| ally-x | `performance` (default) | 60+ fps, sustained (honest lower target for very heavy games) | 60 (45 or 40 if 60 is unreachable) | 60, or per "Frame caps" below |
| ally-x | `battery` | Longest playable battery life | 40 (30 for very heavy games) | 40 / 30 |
| desktop | `big-picture` | 4K TV, streamed (Apollo/Sunshine → Moonlight); couch distance | 120 (60 for very heavy games) | 120 / 60 |
| ally-x | `big-picture` | 4K TV, docked and plugged in; couch distance | 60 (40 for heavy games) | 60 / 40 |

`big-picture` is chosen automatically when the TV is in use (the Ally docked to a 4K TV, or a stream client asking
for 4K), so it must stand alone: never assume the user also picked another preset.

Every preset has `label`, `description` (1-2 sentences: what it targets + what it gives up; include the TDP
assumption on ally-x), `targetFps`.

## desktop / quality

- Display mode from the device in `devices.yaml`, **borderless** (safest with VRR on modern Windows); exclusive fullscreen only if the
  game needs it (e.g. HDR or frame gen broken in borderless).
- Upscaler: DLSS **Quality** (or Balanced in very heavy games / with RT). DLAA only for light games that still
  hit ~120 fps. Turn on Ray Reconstruction when RT is on and the game offers it.
- Ray tracing: on where affordable (reflections/GI first; RT shadows last — least visible per cost). Path
  tracing off unless the game is built around it and DLSS Performance + FG still gives ~80+ fps output.
- Frame generation: on when base fps is ≥60 and output stays under the 162 cap; off in latency-sensitive games.
- Settings: High/Ultra/Epic, but drop the usual "ultra tax" items one notch (volumetric fog/clouds, shadow
  resolution, screen-space reflections when RT reflections are on, hair strands). Optimized-settings guides
  (Digital Foundry, BenchmarKing) list these per game — follow them.
- Cap 162 with `nvidia.FrameRateLimiter: 162`, vsync off in-game (G-Sync + cap). In-game limiter at 162 too when
  frame gen is **off**; with frame gen **on**, leave the in-game limiter off (see "Frame caps").
- Reflex On (On + Boost is fine on this CPU-strong system).

## desktop / performance

- Same resolution and display; DLSS **Performance** (Balanced if the game is light).
- RT off, path tracing off. Frame generation on only in single-player games where it gets you to the cap;
  off in competitive shooters (latency). Reflex On + Boost.
- Settings: High base with expensive items at Medium/Low (shadows, volumetrics, GI/Lumen, reflections, post).
  Keep textures and anisotropic filtering high (cheap on 16 GB).
- Competitive games: also motion blur/DOF/film grain/CA off (usually already in base), lowest-latency options.
- Target 162 fps.

## ally-x / performance

- Output 1920x1080 @ 120 Hz (`display: { width: 1920, height: 1080, refresh: 120 }`), borderless unless the game
  needs exclusive fullscreen.
- Upscaler: FSR or XeSS **Performance** from 1080p (960x540 internal) for heavy UE5/modern games; **Balanced**
  or **Quality** for older/lighter games that still hold 60. TSR is fine in UE5 if FSR/XeSS look worse. Prefer
  the game's built-in "Steam Deck"/"handheld" preset as a starting point when it exists.
- RT off, hardware Lumen off (software Lumen Low/Medium or GI Low), frame gen off.
- Settings: Low/Medium mix — see "cost" below; textures Medium-High (cheap if VRAM allocation is 6+ GB).
- Cap 60 (in-game limiter). If the game easily holds 70+ consider uncapped with VRR, but default to 60:
  sustained beats peak on a handheld. Vsync off with VRR (or on if the game tears and has no VRR support in
  borderless).
- **Honest target**: if research says the game cannot hold 60 at 25-30 W even at Low + FSR Performance, set
  `targetFps` to what it can sustain (e.g. 45, or 40) and say so in `description` ("heavy game: ~45 fps at
  25-30 W"). Cap per "Frame caps" below.
- Assumes 25-30 W (Armoury Crate Performance/Turbo); say so in `description`.

## ally-x / battery

- Same output; upscaler **Performance** (never Ultra Performance at 1080p). Lower internal resolution or the
  game's lowest preset before dropping below 30.
- Cap **40** (divides 120 Hz). Use 30 only for games that cannot hold 40 at ~13 W. Refresh stays **120** (see
  "Frame caps").
- Disable everything that is expensive per pixel and invisible on a 7" screen: volumetrics, SSR, SSAO
  (Low/off), shadows Low, post-process Low, foliage density Low, motion blur/DOF off, effects Low.
- No frame generation.
- `description` must say: pair with Armoury Crate **Silent (~13 W)**; Optimus-Vibe cannot set TDP.

## desktop / big-picture (streamed to the TV)

- Output **3840x2160 @ 120** (`display: { width: 3840, height: 2160, refresh: 120 }`): the stream host (Apollo)
  creates a virtual display at the client's resolution, so the game must render for it, not for the 5120x2160
  monitor. Borderless.
- Upscaler: DLSS **Balanced** at 4K (Quality in light games that still hold 120; Performance in very heavy ones).
  4K DLSS Performance looks better on a TV at couch distance than 1440p native.
- Frame generation: on in single-player games when base fps is ≥ 60 (latency is hidden by the stream's own
  latency less than you'd think: keep it off in competitive shooters). Reflex On.
- RT: reflections/GI where the game still reaches the target; skip RT shadows. Path tracing off.
- Settings: the `quality` look, one notch down where the 4K cost bites (volumetrics, shadows, post).
- **TV readability**: raise HUD/UI scale and subtitle size by one or two steps where the game has them (only in
  `big-picture`; leave the other presets' HUD alone), and turn on large/background subtitles if offered.
- **HDR on** when the game supports it natively (Moonlight/Apollo stream HDR to the TV). Do not set peak/paper
  white brightness: those depend on the TV; tell the user in `notes:` to run the game's HDR calibration once on the
  TV. Set the switch in the other presets too (see Shared rules: on for the desktop's own presets, off on the handheld).
- Cap **120** with `nvidia.FrameRateLimiter: 120` (the stream runs at 120 fps; VRR doesn't cross the stream, so an
  even 120 or 60 matters). Heavy games that can't hold ~110: cap **60** and say so in `description`. With frame gen
  on, leave the in-game limiter off (see "Frame caps").

## ally-x / big-picture (docked to the TV)

- Output **3840x2160 @ 120** when the TV accepts it from the dock (`display: { width: 3840, height: 2160, refresh: 120 }`),
  VRR on (the TV's range is typically 40-120 Hz, so caps of 40 and 60 both work). If research shows the Z2 Extreme's
  output path can't do 4K120 over the dock in this game (or UI text becomes unreadably small at 4K), use 2560x1440
  @ 120 and say why in `notes:`.
- Render low and upscale: FSR/XeSS **Performance** or **Ultra Performance** at 4K output (internal ~1080p/720p),
  Balanced at 1440p output. TSR in UE5 if it looks better. Never drop below ~720p internal.
- Plugged in, so power isn't the limit: assume the 30-35 W docked profile (Armoury Crate Turbo) and say so in
  `description`. The GPU is still the limit: Low/Medium base, textures Medium-High, RT off, hardware Lumen off.
- Same **TV readability** and **HDR** rules as desktop / big-picture (HDR only when the game supports it natively).
- Cap **60** (in-game limiter) when sustainable, else **40** (even pacing at 120 Hz, inside the TV's VRR range).
  No frame generation unless the game's own FSR 3 FG is proven stable on the Z2 Extreme from ≥ 40 fps base.

## Shared rules

- `profiles.base` holds (1) keys with the same value on all four presets (motion blur/film grain/CA off, vsync
  off, window mode: borderless unless the game needs exclusive) and (2) a safe default (off) for every toggle
  some preset turns on (RT, frame gen, Ray Reconstruction). Presets only override. No device-specific values.
- Every preset of a device sets the same keys (directly or via base).
- Turn off the other vendor's upscaler and frame gen explicitly (Steam Cloud carries values between machines).
  A single vendor-neutral frame-gen key set off on the Ally satisfies this. If these options live in a binary
  store (`handler: file`), write the step into the preset `description` and `notes:` instead.
- Never change: FOV, gameplay/difficulty, controls/keybinds/sensitivity, HUD/subtitles/accessibility,
  colour-blind modes, audio, language, brightness/gamma/HDR calibration, account/online/crossplay settings.
  Exception, `big-picture` only: HUD/UI scale, subtitle size/background and the HDR on/off switch (never the HDR
  calibration values). Every other preset must then set HUD scale and subtitle size back to the game's default (put
  those defaults in `profiles.base`) and set the HDR switch explicitly so switching back from the TV restores it:
  `profiles.base` HDR **off** (the handheld panel is SDR), and the desktop's own presets HDR **on** (the owner plays
  in HDR on the desk monitor; check `devices.yaml`'s description, and ask if another user's desktop is SDR).
- Frame caps: see the section below.
- If the game has a quick-preset key that overrides individual keys (e.g. writes "Custom"), set the preset
  value only if the game honours individual keys afterwards; otherwise set the individual keys and the preset
  to "Custom". Confirm with an in-game check note in `notes:`.

## Frame caps

- **Ally X refresh is always 120** in presets (`refresh: 120`). Do not drop to 60 Hz, even for a 30 cap: 30 and
  40 both divide 120, so pacing is even, and 120 Hz keeps input/menus responsive. The panel's VRR floor is about
  48 Hz. Below it, low-framerate compensation doubles frames, which is unreliable on some games. So:
  - target ≥ 55: cap 60 (in-game limiter).
  - target 45-55 (heavy games): cap **40** for even pacing, or leave a 60 cap with VRR if the game hovers
    50-60. Say which in `description`. A 45 fps cap sits under the VRR floor without dividing 120, so avoid it.
  - target < 45: cap 40 (or 30 if 40 is not sustainable). Never an uneven cap below 48.
- **Ally X limiter**: the game's own limiter. There is no driver cap yet (`amd:` is not implemented).
- **Desktop with frame generation on**: cap with `nvidia.FrameRateLimiter: 162` (and Reflex, which DLSS FG
  enables), and leave the in-game limiter **off/uncapped**. Many in-game limiters cap the *base* framerate, so
  an in-game 162 with FG would double past the refresh rate or fight the driver cap.
- **Desktop with frame generation off**: driver cap 162, plus the in-game limiter at 162 if the game has one
  (lower latency than the driver cap alone).
- UE `FrameRateLimit` in GameUserSettings.ini is often honoured even when no menu option exists. Mark it
  `# TODO verify (no menu option; may be ignored)` unless confirmed.

## What costs the most (UE4/UE5 and most modern engines)

Roughly in order of GPU cost, highest first:

1. **Resolution / upscaler mode** — scales everything. First lever on the Ally.
2. **Ray tracing / path tracing** — off on the Ally always; on the desktop pick per effect.
3. **Global illumination** (UE5 Lumen: `sg.GlobalIlluminationQuality`; Epic→High saves a lot, Medium disables
   Lumen GI in many games — check what the game does) and **reflections** (`sg.ReflectionQuality`, Lumen
   reflections/SSR).
4. **Shadows** (`sg.ShadowQuality`; virtual shadow maps in UE5 are expensive at Epic/High).
5. **Volumetric fog/clouds/lighting** (often in `sg.EffectsQuality` or game-specific keys).
6. **Foliage / view distance** (`sg.FoliageQuality`, `sg.ViewDistanceQuality`) — also CPU-heavy (draw calls).
7. **Post-process** (`sg.PostProcessQuality`: DOF, bloom, motion blur, lens effects) — moderate; Low is fine on
   the Ally.
8. **Shading** (`sg.ShadingQuality`, UE5) — moderate.
9. **Anti-aliasing** (`sg.AntiAliasingQuality`; with an upscaler this mostly controls TSR/TAA cost).
10. **Textures** (`sg.TextureQuality`) — almost no GPU time, only VRAM; keep High on desktop, Medium-High on Ally.
11. Hair strands / cloth / physics, SSAO, screen-space GI, contact shadows: game-specific; Low/off on the Ally.

Scalability value scale in UE: 0 = Low, 1 = Medium, 2 = High, 3 = Epic, 4 = Cinematic.

## Sources of truth (research in this order, cite in `recommendation.sources`)

1. `optimus-vibe inspect <id>` on the user's machine — final word on key names and value formats.
2. **PCGamingWiki** (`https://www.pcgamingwiki.com/wiki/<Game>`): config/save locations, engine, upscalers,
   frame-cap/VRR/ultrawide support, known issues, ini tweaks.
3. **Digital Foundry** optimized settings videos/articles, **BenchmarKing** / **Hardware Unboxed** /
   **TechPowerUp** / **ComputerBase** performance reviews: which settings cost what, numbers for the user's GPU class at 4K.
4. **Steam Deck / ROG Ally / Legion Go community guides**: ShareDeck (sharedeck.games), ProtonDB report notes,
   r/SteamDeck, r/ROGAlly, YouTube handheld settings guides (e.g. "Ally X <game> best settings"),
   Notebookcheck APU benchmarks. Steam Deck settings at 800p map roughly to Ally X at 1080p one upscaler step lower.
5. The engine's own scalability groups (`sg.*` in GameUserSettings.ini, `Scalability.ini` /
   `BaseScalability.ini` in the install for what each level sets) and engine docs.
6. Patch notes on the game's Steam news page for renamed/added options (refresh mode).
