# AMD driver settings on the ROG Xbox Ally X (ADLX)

Status: **implemented, unverified for writes on the Ally**. `AmdAdlxManager` reads and writes the GLOBAL 3D graphics settings
of the Radeon GPU through ADLX (`amdadlx64.dll`, installed with Adrenalin). The read path was exercised on a Ryzen 9800X3D
desktop iGPU (ADLX 1.4.0.121): availability, GPU enumeration and 11 of 14 settings read correctly. Writes were not run
there (that iGPU is secondary and its settings are not touched). Run the checks under "Verify on the Ally" on the handheld.

## Per-game or global?

**Global only.** There is no supported way to set Adrenalin's per-game ("Gaming > Games > <game>") settings:

- **ADLX** (current SDK 2.0.0.125): none of the 3D-settings interfaces take an application. Every `Get*` in
  `IADLX3DSettingsServices`, `...Services1`, `...Services2` and `...Services3` takes only an `IADLXGPU*` (or nothing:
  RSR, AFMF), and the interface headers say the values apply to the GPU. `IADLXApplication` / `IADLXApplicationList`
  (`IApplications.h`, via `IADLXGPU2::GetApplications`) only *lists processes currently using the GPU* (pid, name, path);
  it carries no settings. Headers: <https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/main/SDK/Include>
  (`I3DSettings.h`, `I3DSettings1.h`, `I3DSettings2.h`, `I3DSettings3.h`, `IApplications.h`, `ISystem2.h`).
- **Legacy ADL** (`ADL2_ApplicationProfiles_*`: `Profile_Create`, `Profile_Exist`, `Profile_Remove`, property records of binary /
  bool / dword / qword / enum / string type): these edit the driver's *application profile blob* (the system / user profile
  database used by the old Catalyst-era "3D application settings"), addressed by an internal driver "area" plus exe name and
  free-form property names. The current public `display-library` headers (`adl_sdk.h`, `adl_defines.h`, `adl_structures.h`)
  do not declare them any more, AMD marks ADL as superseded by ADLX
  (<https://gpuopen.com/archived/adl/>, API list <https://gpuopen-librariesandsdks.github.io/adl/APIS15.html>,
  <https://gpuopen-librariesandsdks.github.io/adl/group__define__appprofiles.html>), and nothing documents which property names
  Adrenalin's current per-game tab reads. Not implemented: unverifiable and likely not what Adrenalin shows.
- **Adrenalin's own per-game store** (the game database and per-user profile files under `%ProgramData%\AMD\CN` and
  `%LocalAppData%\AMD\CN`) is an undocumented, versioned binary format owned by the Radeon software service. Editing it
  is fragile and could corrupt the user's Adrenalin state. Not implemented. (Description from general knowledge of the install
  layout, not verified here.)

So the design is **apply at launch, restore at exit**, the same shape as the display-mode change:

1. `Runner.ApplyDriver` calls `Apply`, which reads each current value, writes the target only where it differs, and returns a
   JSON restore token (previous values of the settings it changed).
2. The token is saved in the run state file, so a crash is repaired by `RunState.RecoverStale` on the next launch.
3. After the game exits `RunState.Undo` calls `Restore(token)`, which writes the previous values back (reverse order).

No Runner change is needed for this; it already stores and undoes any non-null driver token.

Consequence: while a game with an `amd:` block runs, the global setting is changed for every other app too (a Radeon Chill cap
would affect a second game). That is acceptable for a handheld that runs one game at a time.

## `amd:` keys

Keys are case-insensitive. Booleans accept `true/false/on/off/yes/no/1/0`. Everything is validated by `optimus-vibe validate`
and again at apply time against the driver-reported range. Unsupported settings are skipped with a log warning.

| key | values | ADLX interface |
|---|---|---|
| `FrtcFps` | fps (1-1000) or `off` | Frame Rate Target Control (`IADLX3DFrameRateTargetControl`) |
| `ChillEnabled` | bool | Radeon Chill (`IADLX3DChill`) |
| `ChillMinFps`, `ChillMaxFps` | fps | Radeon Chill |
| `RsrEnabled` | bool | Radeon Super Resolution (`IADLX3DRadeonSuperResolution`, global, no GPU argument) |
| `RsrSharpness` | 0-100 | Radeon Super Resolution |
| `AfmfEnabled` | bool | AMD Fluid Motion Frames (`IADLX3DAMDFluidMotionFrames`, `...Services1`, global) |
| `AntiLag` | `off`, `on`, `next` | Anti-Lag (`IADLX3DAntiLag`; `next` = Anti-Lag 2 level via `IADLX3DAntiLag1`) |
| `RisEnabled`, `RisSharpness` | bool, 0-100 | Radeon Image Sharpening (`IADLX3DImageSharpening`) |
| `WaitForVSync` | `off`, `app-off`, `app-on`, `on` | Wait for Vertical Refresh (`IADLX3DWaitForVerticalRefresh`) |
| `EnhancedSync` | bool | `IADLX3DEnhancedSync` |
| `BoostEnabled`, `BoostMinResolution` | bool, 10-100 (%) | Radeon Boost (`IADLX3DBoost`) |

Short aliases accepted for older templates: `AFMF`, `RSR`, `RIS`, `Chill`, `FRTC`, `Boost`, `VSync`.

```yaml
ally-x:
  battery:
    amd:
      FrtcFps: 40          # a divisor of 120 Hz; the biggest driver-side battery lever
      AfmfEnabled: false
      AntiLag: off
```

Not exposed (not worth a key yet): tessellation, anti-aliasing, anisotropic filtering, shader-cache reset
(`IADLX3DTessellation`, `...AntiAliasing`, `...AnisotropicFiltering`, `...ResetShaderCache`), Sharpen Desktop
(`IADLX3DImageSharpenDesktop`, Services2), FidelityFX Super Resolution / Frame Generation upgrade (Services3), AFMF 2 algorithm /
search / performance modes (`IADLX3DAMDFluidMotionFrames1`). All are reachable the same way if a preset ever needs them.

## Which settings matter for the Ally presets

- **Battery**: `FrtcFps` 40 (or 30) so the APU idles between frames; Chill (min/max) saves more in slow scenes but gives
  variable pacing, so prefer a flat FRTC cap for 40 fps. `AfmfEnabled: false` (frame generation costs power and needs a high
  base rate).
- **Performance**: `AfmfEnabled` only when the base fps is above ~55, `AntiLag: on`, `WaitForVSync: app-off`. `RsrEnabled: true`
  only for games without in-game FSR/XeSS (RSR scales the whole frame including the HUD); `RsrSharpness` 50-70.
- **Big Picture**: usually the performance values; VSync per the TV.
- **Do not rely on RSR/AFMF being "supported" everywhere**: AFMF is absent on older drivers (reported `unsupported` by
  `driver status`) and RSR reported unsupported on the Ryzen iGPU desktop tested.

## Armoury Crate SE / TDP (research only, not implemented)

TDP (the operating mode: Silent 13 W, Performance 17 W, Turbo 25 W, Turbo+ 30 W on the Xbox Ally X per
[Windows Central](https://www.windowscentral.com/hardware/handheld-gaming-pc/xbox-ally-x-improvements-to-make-handheld-play-smoother))
is the largest battery and performance lever, and Optimus-Vibe does not set it. What is known:

- There is no public ASUS API or documented Armoury Crate SDK for setting it. ASUS firmware exposes power limits through the
  ATK ACPI/WMI interface (`ASUS ATKACPI`, device-id based `DEVS/DSTS` calls: performance mode and the SPL/SPPT/FPPT
  power limits). Open-source tools drive it on Windows (G-Helper) and Linux (`asus-wmi` / `asus_armoury` -> `platform_profile`,
  used by Handheld Daemon, SimpleDeckyTDP and Bazzite; see
  [Bazzite #5907](https://github.com/ublue-os/bazzite/issues/5907) on RC73XA TDP resetting after sleep/AC changes unless
  `platform_profile` is set first). The exact ids and whether the Xbox Ally X on Windows 11 (Xbox full-screen experience /
  Armoury Crate SE) honours or overwrites them were not verified here; the id values should be read from the G-Helper source,
  not from this page.
- Armoury Crate SE re-applies its own mode on game start, sleep and AC changes, so a one-shot write could be undone.
  Any implementation would be a separate, opt-in `device:`-level feature (`power: silent|performance|turbo` in
  devices.yaml) with a restore token and its own verification on the device. Preset descriptions keep stating the TDP assumption.

## Verify on the Ally (nothing is changed unless stated)

```
optimus-vibe detect
optimus-vibe driver status
optimus-vibe driver test-amd --dry-run
optimus-vibe driver test-amd --apply-and-restore      # writes image sharpness +/- 1, then puts it back
```

`driver status` prints the vendor, whether ADLX is available and why not, the ADLX version the driver reports, every GPU, and every
setting from the table with its current value, range and `unsupported` / `error:` where it cannot be read. Paste the output back.
`test-amd --apply-and-restore` exits 0 only if the value changed, read back as changed, was restored and read back as the original.
Then try a real preset: add an `amd:` block to one Ally preset, run `optimus-vibe apply <game> --profile ally-x --dry-run`
(prints `amd: <key>: <current> -> <target>`), launch it through the wrapper, and check Adrenalin shows the change during the game
and the original values afterward. The log (`%LocalAppData%\OptimusVibe\logs`) has one `AMD:` line per setting.

## NVIDIA driver settings (for comparison and additions)

The desktop uses per-exe DRS profiles, which are persistent and need no restore. See `NvidiaSettingTable` for the full list;
the new Profile Inspector-sourced names (`DlssPreset`, `DlssOverrideDll`, `DlssFgOverrideDll`, `DlssRrOverrideDll`,
`DlssQualityLevel`, `UltraLowLatency`, `ShaderCacheSize`, `SmoothMotion`) come from
<https://github.com/Orbmu2k/nvidiaProfileInspector> (`CustomSettingNames.xml`) rather than NVIDIA's header; confirm with
`optimus-vibe inspect` on the driver in use before shipping them in presets. DLSS Super Resolution preset override needs both
`DlssModelPresetProfile: Custom` and `DlssPreset: <letter>`.

## Implementation notes

- `Driver/AdlxNative.cs`: loads `System32\amdadlx64.dll`, `ADLXQueryFullVersion`, then `ADLXInitialize` with the driver's own version
  (the interfaces used exist since ADLX 1.0; newer ones are reached with `QueryInterface`). The C vtables are called through
  `delegate* unmanaged` pointers; every object and slot is checked for null before the call, every interface is released, a failing
  `IsSupported` probe is "unsupported", and any exception becomes an error status rather than propagating.
- `Driver/AdlxApi.cs`: the `IAdlxApi` / `IAdlxSession` seam; unit tests inject a fake (`AmdAdlxManagerTests`).
- `Driver/AmdSettingTable.cs`: keys, value parsing/formatting, aliases; also used by `validate`.
- `Driver/DriverDiagnostics.cs` + `Cli/DriverCommands.cs`: `driver status`, `driver test-amd`.
- Types: `ADLX_RESULT` is an int (0 OK, 1 ALREADY_ENABLED and 2 ALREADY_INITIALIZED are success, 6 UNKNOWN_INTERFACE and 12
  NOT_SUPPORTED mean not exposed); `adlx_bool` is one byte; `ADLX_IntRange` is `{min, max, step}` ints.

### Vtable slots (ADLX 2.0.0.125 C headers; slot 0-2 = Acquire, Release, QueryInterface unless noted)

- `IADLXSystem` (no Acquire/Release): 1 GetGPUs, 7 Get3DSettingsServices.
- `IADLXGPUList`: 3 Size, 11 At_GPUList. `IADLXGPU`: 3 VendorId, 7 Name.
- `IADLX3DSettingsServices` (and 1-3, which only append): 3 GetAntiLag, 4 GetChill, 5 GetBoost, 6 GetImageSharpening,
  7 GetEnhancedSync, 8 GetWaitForVerticalRefresh, 9 GetFrameRateTargetControl, 14 GetRadeonSuperResolution (no GPU arg),
  17 GetAMDFluidMotionFrames (Services1+, via QueryInterface; no GPU arg).
- Feature interfaces: `3 IsSupported`, `4 IsEnabled`; Chill `5 GetFPSRange 6 GetMinFPS 7 GetMaxFPS 8 SetEnabled 9 SetMinFPS 10 SetMaxFPS`;
  FRTC `5 GetFPSRange 6 GetFPS 7 SetEnabled 8 SetFPS`; RSR `5 SetEnabled 6 GetSharpnessRange 7 GetSharpness 8 SetSharpness`;
  RIS `5 GetSharpnessRange 6 GetSharpness 7 SetEnabled 8 SetSharpness`; Boost `5 GetResolutionRange 6 GetResolution 7 SetEnabled 8 SetResolution`;
  AFMF `5 SetEnabled`; Anti-Lag `5 SetEnabled`, Anti-Lag1 `6 GetLevel 7 SetLevel`; VSync `5 GetMode 6 SetMode`; Enhanced Sync `5 SetEnabled`.

## Sources

- ADLX SDK headers: <https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/main/SDK/Include>; manual: <https://gpuopen.com/manuals/adlx/>
- ADL (legacy): <https://gpuopen.com/archived/adl/>, <https://gpuopen-librariesandsdks.github.io/adl/group__define__appprofiles.html>
- NVIDIA Profile Inspector setting names: <https://github.com/Orbmu2k/nvidiaProfileInspector>
