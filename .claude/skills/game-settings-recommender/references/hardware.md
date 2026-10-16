# Hardware: the two GameProfiles devices

Device ids come from `config/devices.yaml`. Presets are written per device id.

## `desktop`

| | |
|---|---|
| GPU | NVIDIA GeForce RTX GPU, 16 GB GDDR6X (Ada Lovelace) |
| CPU | AMD desktop Ryzen CPU (8C/16T, 96 MB L3 V-Cache) — rarely the bottleneck |
| Display | <desktop resolution> ultrawide (21:9) @ 165 Hz, G-Sync-compatible VRR |
| Driver backend | `nvidia:` block → per-exe NVIDIA DRS profile (persistent) |
| Default preset | `quality` |

What limits it:

- **Pixel count.** <desktop resolution> is 11.06 MP, about 33% more than 4K (8.29 MP) and 2.7x 1440p. A 4080 that does
  ~60 fps native 4K in a heavy UE5 game does ~45 here. Treat native rendering as unaffordable in modern AAA
  games; DLSS is the default.
- **DLSS internal resolutions at <desktop resolution>**: Quality (66.7%) 3413x1440; Balanced (58%) 2970x1253;
  Performance (50%) 2560x1080; Ultra Performance (33%) 1707x720. Quality/Balanced look near-native at this
  output size; Performance is still good thanks to the high output resolution (DLSS 4 transformer model even more so).
- **Frame generation**: DLSS 3 Frame Generation is supported on the 4080 (Ada). DLSS 4 Multi Frame Generation is
  **not** (RTX 50 only). FG needs ~60+ fps base to feel good and adds latency (Reflex is enabled with it).
- **VRAM**: 16 GB is enough for Ultra/Epic textures in nearly everything; path tracing + FG + max textures at
  this resolution can approach the limit in a few titles (watch for stutter/texture pop).
- **Ray tracing**: affordable at Quality preset with DLSS Balanced/Performance + FG in most titles; full path
  tracing generally is not at <desktop resolution> unless DLSS Performance + FG and ~60-80 fps output is acceptable.
- **Cap**: 162 fps (a few under 165 Hz keeps G-Sync engaged). Use `nvidia: { FrameRateLimiter: 162 }` and/or
  the game's own limiter. Supported `nvidia:` names: `FrameRateLimiter` (fps or off), `VSync` (app|off|on),
  `PowerMode` (adaptive|max|optimal), plus raw DRS setting names/ids (see `NvidiaDrsManager.cs`).
- Also available: NVIDIA Reflex (turn on where offered), DLAA (native-res DLSS; only for light games).
- The desktop Ryzen CPU also exposes an AMD iGPU; irrelevant for games (the 4080 drives the monitor).

## `ally-x`

| | |
|---|---|
| Device | ASUS ROG Xbox Ally X (2025) |
| APU | AMD Ryzen AI Z2 Extreme: 8 Zen 5 cores (3 Zen 5 + 5 Zen 5c), RDNA 3.5 iGPU with **16 CUs** (Radeon 890M class), NPU |
| Memory | 24 GB LPDDR5X-8000, **shared** between CPU and GPU (games usually see 4-8 GB as VRAM, set in Armoury Crate) |
| Display | 7" 1920x1080 IPS, 120 Hz, VRR (FreeSync Premium, roughly 48-120 Hz) |
| Battery | 80 Wh |
| Power | TDP ~13-17 W (Silent), ~15-25 W (Performance), 35 W (Turbo, plugged in) — set by the user in Armoury Crate / Command Center, **not** by GameProfiles |
| OS shell | Xbox full-screen experience on Windows 11 |
| Driver backend | `amd:` block — **not implemented** (docs/amd-adlx.md); leave commented |
| Default preset | `performance` |

What limits it:

- **Memory bandwidth** (~128 GB/s shared, vs 717 GB/s on the 4080) is the main GPU bottleneck. Resolution,
  heavy post-processing, high-res shadow maps, volumetrics, and screen-space effects scale with it. Lowering
  internal resolution helps more than almost anything else.
- **iGPU compute**: 16 RDNA 3.5 CUs ≈ a GTX 1650 / RX 6400-6500 XT class GPU at 25-30 W, less at 15 W. No
  dedicated RT hardware worth using: **ray tracing always off**; avoid hardware Lumen.
- **TDP**: performance scales strongly from 15 W → 25 W → 35 W. Presets cannot change TDP, so descriptions must
  state the TDP assumption (Performance assumes 25-30 W; Battery should be paired with Silent ~13 W).
- **CPU**: Zen 5 at handheld clocks; CPU-heavy games (crowds, simulation, UE5 traversal stutter) can limit fps
  regardless of graphics settings; shader-compilation stutter is worse here.
- **Battery**: 80 Wh. Rough draw: ~13-15 W TDP → ~20-22 W total → 3.5-4 h; 25 W → ~35 W total → ~2-2.5 h;
  35 W → ~1.5 h. Frame caps (30/40) save the most power because the APU idles between frames.
- **Display**: native 1920x1080 16:9 (no 1280x800 mode). 120 Hz with VRR: 60 fps cap is smooth; 40 fps divides
  120 Hz evenly (each frame shown 3 refreshes) and is the battery sweet spot; 30 fps divides too but feels sluggish.
  Below ~48 fps VRR relies on low-framerate compensation, so an exact divisor cap (40/30) gives even pacing.
- **Upscalers**: no DLSS. Use FSR (FSR 3.1 / FSR 4 if the game ships it — FSR 4 ML upscaling officially needs
  RDNA 4, so on RDNA 3.5 expect FSR 3.1 or the game's fallback), XeSS (DP4a path runs on AMD; often better image
  quality than FSR 2/3 at low res but costs a bit more), or TSR in UE5 games. At 1080p output:
  Quality 1280x720, Balanced ~1114x626, Performance 960x540, Ultra Performance 640x360 (too blurry; avoid).
  Alternative: output 1280x720 (non-integer 1.5x scale on the panel, soft) with Quality upscaling.
- **Frame generation** (FSR 3 FG / AFMF): only above ~55-60 fps base; latency and artifacts are noticeable on a
  small screen at low base rates. Generally off; never in Battery.
- **VRAM**: textures Medium-High usually fine with 6-8 GB allocated; Ultra textures can stutter. Texture
  quality costs little GPU time, mostly memory.
