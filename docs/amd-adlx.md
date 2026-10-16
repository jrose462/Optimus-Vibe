# AMD driver settings on the ROG Xbox Ally X (ADLX)

Status: **not implemented**. `AmdAdlxManager.IsAvailable` returns false with
"AMD ADLX support not implemented yet (see docs/amd-adlx.md)". `Plan` echoes every requested key with
`Current = null`, and `Apply` logs a warning and returns null. This page records what the research found and the
recommended way to finish the work on the Ally itself.

## What ADLX can and cannot do

- AMD has **no supported API for Adrenalin per-game profiles**. ADLX (AMD Device Library eXtra) exposes only the
  **global** 3D graphics settings, plus display, GPU-tuning and performance-monitoring services.
- Per-game behaviour therefore has to be **apply before launch, restore after exit**. `Apply` would read the current
  values, write the targets, and return them as a JSON restore token. `Restore(token)` writes them back. The runner's
  stale-state check covers crashes.
- 3D settings available through `IADLX3DSettingsServices` and its later versions: Anti-Lag, Chill (min/max FPS), Boost,
  Image Sharpening, Enhanced Sync, Wait-for-VSync, Frame Rate Target Control (FRTC), anti-aliasing, MLAA, anisotropic
  filtering, tessellation, Radeon Super Resolution (RSR), shader-cache reset. `IADLX3DSettingsServices1` adds
  **AMD Fluid Motion Frames (AFMF)**. Most getters take an `IADLXGPU*`, but **AFMF is global** (no GPU argument).
  The header comment states: "AMD Fluid Motion Frames settings are global for all the supported GPUs." RSR is believed to
  be global as well, but that was not verified.

## Can C# call it without AMD's SWIG binding DLL? Yes

AMD's documented C# route is the SDK's SWIG sample (`ADLXCSharpBind`). It needs a native glue DLL built from the SDK
with C++ and SWIG, which this project avoids. That glue is not required, for these reasons:

1. **The runtime ships with the driver.** `C:\Windows\System32\amdadlx64.dll` is installed by Adrenalin. It is present
   on this desktop as v1.4.0.121, because of the AMD iGPU driver. Nothing has to be bundled.
2. **The exports are plain C (`__cdecl`).** From `ADLX.h`:
   ```c
   #define ADLX_DLL_NAMEW L"amdadlx64.dll"
   "ADLXQueryFullVersion"  ADLX_RESULT ADLXQueryFullVersion(adlx_uint64* fullVersion);
   "ADLXQueryVersion"      ADLX_RESULT ADLXQueryVersion(const char** version);
   "ADLXInitialize"        ADLX_RESULT ADLXInitialize(adlx_uint64 version, IADLXSystem** ppSystem);
   "ADLXInitializeWithIncompatibleDriver"   (same signature)
   "ADLXTerminate"         ADLX_RESULT ADLXTerminate();
   ```
3. **The interfaces have a C ABI.** Every header has a C branch (`#else`) that declares
   `struct IADLXxxx { const IADLXxxxVtbl* pVtbl; }` with explicit function-pointer tables (`__stdcall`, which is the
   same as the default convention on x64). The object pointer's first field is the vtable pointer, so C# can call slot
   *n* with `((delegate* unmanaged<...>)(*(void***)obj)[n])(obj, ...)`. The project already has `AllowUnsafeBlocks`.

Types: `ADLX_RESULT` is an `int` enum (`ADLX_OK = 0`). `adlx_bool` is **1 byte** (`adlx_uint8`) in the C ABI, so use
`byte`, not `bool`/`BOOL`. `adlx_int` and `adlx_uint` are 32-bit. Interface IDs used with `QueryInterface` are wide
strings equal to the interface name, for example `L"IADLX3DSettingsServices1"`.

## Vtable slots (from the SDK `main` branch C headers, fetched 2026-10)

All interfaces except `IADLXSystem` start with the `IADLXInterface` block:
`0 Acquire`, `1 Release`, `2 QueryInterface(pThis, const wchar_t* iid, void** pp)`.

**IADLXSystem** (singleton, *no* Acquire/Release):

| slot | function |
|---|---|
| 0 | GetHybridGraphicsType(pThis, ADLX_HG_TYPE*) |
| 1 | GetGPUs(pThis, IADLXGPUList** ppGPUs) |
| 2 | QueryInterface(pThis, const wchar_t*, void**) |
| 3 | GetDisplaysServices |
| 4 | GetDesktopsServices |
| 5 | GetGPUsChangedHandling |
| 6 | EnableLog |
| 7 | **Get3DSettingsServices(pThis, IADLX3DSettingsServices** pp)** |
| 8 | GetGPUTuningServices |
| 9 | GetPerformanceMonitoringServices |
| 10 | TotalSystemRAM |
| 11 | GetI2C |

**IADLXGPUList**: 0-2 IADLXInterface, `3 Size(pThis) -> adlx_uint`, 4 Empty, 5 Begin, 6 End, 7 At, 8 Clear,
9 Remove_Back, 10 Add_Back, `11 At_GPUList(pThis, adlx_uint location, IADLXGPU** ppItem)`, 12 Add_Back_GPUList.

**IADLX3DSettingsServices1** (obtain via `QueryInterface(L"IADLX3DSettingsServices1")` on the services object):

| slot | function |
|---|---|
| 0-2 | Acquire / Release / QueryInterface |
| 3 | GetAntiLag(pThis, IADLXGPU*, IADLX3DAntiLag**) |
| 4 | **GetChill(pThis, IADLXGPU*, IADLX3DChill**)** |
| 5 | GetBoost |
| 6 | GetImageSharpening |
| 7 | GetEnhancedSync |
| 8 | GetWaitForVerticalRefresh |
| 9 | GetFrameRateTargetControl(pThis, IADLXGPU*, IADLX3DFrameRateTargetControl**) |
| 10 | GetAntiAliasing |
| 11 | GetMorphologicalAntiAliasing |
| 12 | GetAnisotropicFiltering |
| 13 | GetTessellation |
| 14 | GetRadeonSuperResolution |
| 15 | GetResetShaderCache |
| 16 | Get3DSettingsChangedHandling |
| 17 | **GetAMDFluidMotionFrames(pThis, IADLX3DAMDFluidMotionFrames**)** (Services1 only) |

Slots 0-16 are identical in the base `IADLX3DSettingsServices`.

**IADLX3DChill**: 0-2 IADLXInterface, `3 IsSupported(adlx_bool*)`, `4 IsEnabled(adlx_bool*)`,
`5 GetFPSRange(ADLX_IntRange*)`, `6 GetMinFPS(adlx_int*)`, `7 GetMaxFPS(adlx_int*)`, `8 SetEnabled(adlx_bool)`,
`9 SetMinFPS(adlx_int)`, `10 SetMaxFPS(adlx_int)`.

**IADLX3DAMDFluidMotionFrames**: 0-2 IADLXInterface, `3 IsSupported(adlx_bool*)`, `4 IsEnabled(adlx_bool*)`,
`5 SetEnabled(adlx_bool)`.

**IADLX3DFrameRateTargetControl**: 0-2, `3 IsSupported`, `4 IsEnabled`, `5 GetFPSRange`, `6 GetFPS(adlx_int*)`,
`7 SetEnabled(adlx_bool)`, `8 SetFPS(adlx_int)`.

**IADLX3DAntiLag**: 0-2, `3 IsSupported`, `4 IsEnabled`, `5 SetEnabled`. Anti-Lag 2 and the level setting are on
`IADLX3DAntiLag1` and later, through QueryInterface.

`ADLX_IntRange` is `{ adlx_int minValue; adlx_int maxValue; adlx_int step; }`. Check this against `ADLXStructures.h`
before relying on it.

## Call sequence (Chill example)

```
lib  = NativeLibrary.Load("amdadlx64.dll")                   // System32; fail -> IsAvailable=false
ADLXQueryFullVersion(&ver)                                    // driver's ADLX version
ADLXInitialize(ver, &sys)                                     // or the version the code was written against
sys->vtbl[1]  GetGPUs(sys, &gpuList)
gpuList->vtbl[3] Size ; gpuList->vtbl[11] At_GPUList(gpuList, 0, &gpu)   // the Ally has one GPU
sys->vtbl[7]  Get3DSettingsServices(sys, &svc)
svc->vtbl[4]  GetChill(svc, gpu, &chill)
chill->vtbl[3] IsSupported ; [4] IsEnabled ; [6] GetMinFPS ; [7] GetMaxFPS   // -> restore token
chill->vtbl[8] SetEnabled(1) ; [9] SetMinFPS(55) ; [10] SetMaxFPS(60)
svc->vtbl[2]  QueryInterface(svc, L"IADLX3DSettingsServices1", &svc1)    // AFMF
svc1->vtbl[17] GetAMDFluidMotionFrames(svc1, &afmf) ; afmf->vtbl[5] SetEnabled(0)
Release (slot 1) every interface obtained (chill, afmf, svc1, svc, gpu, gpuList), then ADLXTerminate()
```

Pass the version to `ADLXInitialize` as `ADLX_MAKE_FULL_VER(major, minor, release, build)`, which packs four 16-bit
fields into a `uint64`, major first. Confirm the macro in `ADLXDefines.h`. The simplest choice is the value returned
by `ADLXQueryFullVersion`. If the driver is older than a feature's interface (for example AFMF on a pre-Services1
driver), `QueryInterface` returns an error code. Treat that as "unsupported" and do not fail.

## Recommended implementation path

1. Add `Driver/Adlx/AdlxNative.cs`: `NativeLibrary.TryLoad("amdadlx64.dll")`, `GetExport`, the three exports as
   `delegate* unmanaged[Cdecl]`, and a small `AdlxObject : IDisposable` wrapper that holds the `IntPtr` and calls
   slot 1 (Release) on dispose. Vtable calls go through one helper: `Slot(IntPtr obj, int n)`.
2. Map YAML keys to (getter slot, setter calls). Start with `Chill: {min,max}` / `Chill: false`, `FRTC: <fps>|off`,
   `AFMF: true|false`, `AntiLag: true|false`, and `RSR: true|false`. Everything else stays "unknown key" until it is
   needed.
3. `Plan` reads the current values. `Apply` captures every value it changes into a JSON token,
   `{"Chill":{"enabled":true,"min":30,"max":144},"AFMF":false}`, before writing, and `Restore` writes the token back.
   Apply only the deltas, and always check `IsSupported` first.
4. **Test on the Ally only.** This desktop's AMD iGPU is secondary and must not have its settings changed. A read-only
   smoke test (initialize, list GPUs, read Chill/AFMF state) is safe here.
5. Cross-check every slot number against the SDK headers for the ADLX version being targeted. The tables above come
   from the SDK's current `main` branch. The vtables only grow by appending (`...1`, `...2` interfaces through
   QueryInterface), so the slots should be stable, but nothing was executed against the driver to confirm them.

## Sources

- ADLX SDK headers: <https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/tree/main/SDK/Include>
  (`ADLX.h`, `ADLXDefines.h`, `ADLXVersion.h`, `ISystem.h`, `I3DSettings.h`, `I3DSettings1.h`)
- ADLX documentation: <https://gpuopen.com/manuals/adlx/>
