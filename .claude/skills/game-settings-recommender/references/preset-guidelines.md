# Preset guidelines

Four presets per game. Names are fixed (the app orders and remembers them by name).

| Device | Preset | Goal | targetFps | Typical cap |
|---|---|---|---|---|
| desktop | `quality` (default) | Best visuals at a smooth framerate | 100-120 (90 acceptable for path tracing showcases) | 162 |
| desktop | `performance` | High fps / competitive | 162 | 162 |
| ally-x | `performance` (default) | 60+ fps, sustained (honest lower target for very heavy games) | 60 (45 or 40 if 60 is unreachable) | 60, or per "Frame caps" below |
| ally-x | `battery` | Longest playable battery life | 40 (30 for very heavy games) | 40 / 30 |

Every preset has `label`, `description` (1-2 sentences: what it targets + what it gives up; include the TDP
assumption on ally-x), `targetFps`.

## desktop / quality

- Resolution <desktop resolution>, **borderless** (safest with G-Sync on modern Windows); exclusive fullscreen only if the
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
- `description` must say: pair with Armoury Crate **Silent (~13 W)**; GameProfiles cannot set TDP.

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

1. `gameprofiles inspect <id>` on the user's machine — final word on key names and value formats.
2. **PCGamingWiki** (`https://www.pcgamingwiki.com/wiki/<Game>`): config/save locations, engine, upscalers,
   frame-cap/VRR/ultrawide support, known issues, ini tweaks.
3. **Digital Foundry** optimized settings videos/articles, **BenchmarKing** / **Hardware Unboxed** /
   **TechPowerUp** / **ComputerBase** performance reviews: which settings cost what, RTX GPU numbers at 4K.
4. **Steam Deck / ROG Ally / Legion Go community guides**: ShareDeck (sharedeck.games), ProtonDB report notes,
   r/SteamDeck, r/ROGAlly, YouTube handheld settings guides (e.g. "Ally X <game> best settings"),
   Notebookcheck APU benchmarks. Steam Deck settings at 800p map roughly to Ally X at 1080p one upscaler step lower.
5. The engine's own scalability groups (`sg.*` in GameUserSettings.ini, `Scalability.ini` /
   `BaseScalability.ini` in the install for what each level sets) and engine docs.
6. Patch notes on the game's Steam news page for renamed/added options (refresh mode).
