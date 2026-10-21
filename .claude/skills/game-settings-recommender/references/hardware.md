# Hardware: reading the user's devices

**Do not assume any hardware.** The user's devices are defined in their CONFIG repo, in `config/devices.yaml`
(next to `config/games/`). Read it first (`<config repo>\config\devices.yaml`), and for every device take:

- the **device id** (the key under `devices:`): presets are written per device id;
- `description`: the user's own free text (GPU, CPU, RAM, display, TDP, anything that matters). Treat it as the
  source of truth for the hardware; if it is thin, ask the user to fill it in (or to run `optimus-vibe detect`)
  before sizing presets;
- `display` (width x height @ refresh), `driver` (`nvidia` | `amd` | `none`) and `defaultPreset`.

If a spec you need (VRAM, shared memory, TDP, panel type) is missing from `description`, say so and make the
assumption explicit in the preset `description` and `notes:` instead of guessing silently.
Conventional preset names: a desktop-class device gets `quality` / `performance`; a handheld gets
`performance` / `battery`. Use whatever names the user's devices and existing games already use.

The rest of this file is hardware-agnostic reasoning to apply to those specs.

## Desktop-class devices (discrete GPU, `driver: nvidia`)

- **Pixel count drives everything.** Compute megapixels of the device's `display` and compare with 4K (8.29 MP) and
  1440p (3.69 MP). Wide or very high resolutions make native rendering unaffordable in modern AAA games; upscaling
  is the default.
- **DLSS internal resolution** = output x scale: Quality 66.7%, Balanced 58%, Performance 50%, Ultra Performance 33%.
  At high output resolutions Quality/Balanced look near-native and Performance is still good (the DLSS 4 transformer
  model even more so).
- **Frame generation**: DLSS 3 Frame Generation needs an RTX 40 series or newer; DLSS 4 Multi Frame Generation needs RTX 50.
  FG needs ~60+ fps base to feel good and adds latency (enable Reflex with it). Check the GPU generation in
  `description` before enabling any of it.
- **VRAM**: compare against the GPU's VRAM in `description`. Ultra/Epic textures are usually fine on 12-16 GB; path
  tracing + FG + max textures at very high resolutions can approach the limit (watch for stutter / texture pop).
- **Ray tracing**: affordable at the quality preset with DLSS Balanced/Performance (+ FG where supported) in most
  titles; full path tracing usually is not at very high resolutions.
- **Cap**: a few fps under the display's refresh rate keeps VRR engaged (e.g. refresh 165 -> cap 162). Use
  `nvidia: { FrameRateLimiter: <cap> }` and/or the game's own limiter. Supported `nvidia:` names: `FrameRateLimiter`
  (fps or off), `VSync` (app|off|on), `PowerMode` (adaptive|max|optimal), plus raw DRS setting names/ids (see
  `NvidiaDrsManager.cs`).
- Also available: NVIDIA Reflex (turn on where offered), DLAA (native-res DLSS; only for light games).
- A CPU with an integrated GPU also exposes an iGPU; irrelevant for games when a discrete GPU drives the monitor
  (this is why `devices.yaml` should set `driver:` explicitly).

## Handheld / APU devices (shared memory, `driver: amd`)

- **Memory bandwidth** is the main GPU bottleneck on shared LPDDR memory. Resolution, heavy post-processing, high-res
  shadow maps, volumetrics and screen-space effects scale with it. Lowering internal resolution helps more than
  almost anything else.
- **iGPU compute**: current handheld APUs are roughly entry-level discrete-GPU class at 25-30 W, less at 15 W. No
  dedicated RT hardware worth using: **ray tracing always off**; avoid hardware Lumen.
- **TDP**: performance scales strongly with the power limit (15 W -> 25 W -> 35 W). Presets cannot change TDP
  (Optimus-Vibe does not set it), so preset descriptions must state the TDP assumption (performance presets assume
  ~25-30 W; battery presets should be paired with the silent mode, ~13 W). Read the device's TDP range from `description`.
- **CPU**: handheld clocks are low; CPU-heavy games (crowds, simulation, UE5 traversal stutter) can limit fps
  regardless of graphics settings; shader-compilation stutter is worse here.
- **Battery**: frame caps (30/40) save the most power because the APU idles between frames. Estimate runtime from the
  battery size in `description`.
- **Display**: use the native mode from `display`. With VRR at 120 Hz, a 60 fps cap is smooth; 40 fps divides 120 Hz
  evenly (each frame shown 3 refreshes) and is the battery sweet spot; 30 fps divides too but feels sluggish. Below
  ~48 fps VRR relies on low-framerate compensation, so an exact divisor cap (40/30) gives even pacing. For a 60 Hz or
  other refresh rate, pick caps that divide it evenly.
- **Upscalers**: no DLSS. Use FSR (FSR 3.1; FSR 4 ML upscaling officially needs RDNA 4, so on older RDNA expect FSR 3.1
  or the game's fallback), XeSS (the DP4a path runs on AMD; often better image quality than FSR 2/3 at low res but
  costs a bit more), or TSR in UE5 games. At 1080p output: Quality 1280x720, Balanced ~1114x626, Performance 960x540,
  Ultra Performance 640x360 (too blurry; avoid).
- **Frame generation** (FSR 3 FG / AFMF): only above ~55-60 fps base; latency and artifacts are noticeable on a small
  screen at low base rates. Generally off; never in a battery preset.
- **VRAM**: with shared memory the game usually sees a few GB as VRAM (set in the vendor's utility); textures
  Medium-High are usually fine, Ultra can stutter. Texture quality costs little GPU time, mostly memory.

## Other devices

Laptops, mini PCs, Steam Deck-class handhelds: classify by `description` (discrete GPU -> desktop reasoning with a
power/thermal haircut; APU -> handheld reasoning) and name the presets after what the user's `defaultPreset` expects.

## Driver keys (`nvidia:` / `amd:`) instead of file edits

Use a driver key when the game has no reliable in-game option, rewrites its config on exit, or the lever only exists in the
driver. Use the game's own settings file for everything the game exposes (resolution, quality, upscaler mode): the driver
cannot see inside the game, and double-capping (game limiter plus driver cap) just adds latency.

**NVIDIA (`driver: nvidia`)** is per game exe (DRS profile, persistent, no restore). Aliases: `FrameRateLimiter` (fps | off),
`VSync` (app | off | on), `PowerMode` (adaptive | max | optimal). Any `NvidiaSettingTable` name works too, notably
`PreRenderLimit` (max pre-rendered frames), `UltraLowLatency` (On/Off), `ShaderCacheSize`, `PerformanceStateShaderDiskCache`,
and (Profile Inspector ids, verify with `inspect` first) `DlssPreset` (K/L/M transformer presets, needs
`DlssModelPresetProfile: Custom`), `DlssOverrideDll`, `DlssFgOverrideDll`, `SmoothMotion`. Prefer the driver cap
(`FrameRateLimiter`) over the in-game limiter only when frame generation is on or the game limiter is broken.

**AMD (`driver: amd`)** is GLOBAL (ADLX has no per-app scope): applied at launch, restored at exit. Keys: `FrtcFps`
(fps | off), `ChillEnabled` / `ChillMinFps` / `ChillMaxFps`, `RsrEnabled` / `RsrSharpness`, `AfmfEnabled`, `AntiLag`
(off | on | next), `RisEnabled` / `RisSharpness`, `WaitForVSync` (off | app-off | app-on | on), `EnhancedSync`,
`BoostEnabled` / `BoostMinResolution`. Ally recommendations:
- Battery presets: `FrtcFps: 40` (or 30) is the single best driver lever; pair with `AfmfEnabled: false`. Chill
  (`ChillMinFps`/`ChillMaxFps`) saves more on slow scenes but makes pacing variable; prefer a flat FRTC cap for 40 fps.
- Performance presets: `AfmfEnabled: true` in single-player games whose capped base holds ~50-60 fps (60 rendered -> ~120 on
  the 120 Hz panel; say so in `description`); `false` in competitive games, below a ~50 base, and when the game's own frame
  generation is on. `AntiLag: on`. AFMF is a global switch: always set it explicitly in every Ally preset, big-picture included.
- `RsrEnabled: true` only for games with no in-game FSR/XeSS (RSR works on the whole frame, including the HUD, and conflicts
  with in-game upscalers); `RsrSharpness` 50-70.
- Use `WaitForVSync: app-off` (the Adrenalin default) unless a game tears on the 120 Hz VRR panel.
- Anything the driver reports as unsupported is skipped with a warning, so these blocks are safe on older drivers.
