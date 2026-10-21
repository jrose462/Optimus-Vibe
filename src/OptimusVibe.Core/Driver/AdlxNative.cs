using System.Runtime.InteropServices;
using System.Text;
using OptimusVibe.Logging;

namespace OptimusVibe.Driver;

/// <summary>
/// Minimal, defensive ADLX (AMD Device Library eXtra) client: loads <c>amdadlx64.dll</c> from the installed Adrenalin driver and calls the
/// 3D-graphics-settings interfaces through their C vtables (no SWIG glue DLL). Slot numbers come from the ADLX SDK C headers
/// (ADLX 2.0.0.125, GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include/I3DSettings*.h); see docs/amd-adlx.md for the table.
/// <para>
/// ADLX has NO per-application scope: every setting here is the GLOBAL 3D setting of the GPU. Callers apply before launch and restore after exit.
/// </para>
/// <para>
/// Every object pointer and vtable slot is checked before use, every native result code is mapped (unsupported vs error), and every
/// acquired interface is released. Nothing here throws to callers except through <see cref="IAdlxSession.Read"/>/<c>Write</c> status values.
/// UNVERIFIED on real hardware at the time of writing: run `optimus-vibe driver status` on the Ally.
/// </para>
/// </summary>
internal sealed unsafe class AdlxNative : IAdlxApi
{
    public static readonly AdlxNative Instance = new();

    internal delegate bool InitFunc(out string reason);

    private readonly InitFunc? _initOverride; // tests

    private AdlxNative()
    {
    }

    internal AdlxNative(InitFunc init) => _initOverride = init;

    private const string DllName = "amdadlx64.dll";
    private const string AmdVendorId = "1002";

    // ADLX_RESULT: 0 OK, 1 ALREADY_ENABLED, 2 ALREADY_INITIALIZED are success codes. 6 UNKNOWN_INTERFACE and 12 NOT_SUPPORTED mean "not exposed here".
    private static bool Succeeded(int r) => r is >= 0 and <= 2;
    private static bool IsUnsupportedCode(int r) => r is 6 or 12;

    // ADLX_RESULT 7 TERMINATED and 10 INVALID_OBJECT: the ADLX system we hold is dead (driver restart / reset); initialize again.
    private static bool IsStaleCode(int r) => r is 7 or 10;

    private readonly object _gate = new();
    private bool _ok;
    private string _reason = "";
    private string? _lastLoggedReason;
    private nint _system;
    private nint _terminateFn;
    private bool _exitHooked;
    private AdlxInfo _info = new(null, null, [], null);
    private int _gpuIndex = -1;

    public AdlxInfo Info => _info;

    /// <summary>Only a success is cached: a failure (driver not ready yet, ADLX terminated) is retried on the next call.</summary>
    public bool TryInitialize(out string reason)
    {
        lock (_gate)
        {
            if (!_ok)
            {
                try { _ok = _initOverride is { } init ? init(out _reason) : Initialize(out _reason); }
                catch (Exception ex)
                {
                    _ok = false;
                    _reason = $"AMD ADLX initialization failed: {ex.GetType().Name}: {ex.Message}";
                }
                if (!_ok && _reason != _lastLoggedReason) Log.Info($"AMD: {_reason}");
                _lastLoggedReason = _ok ? null : _reason;
                if (_ok && !_exitHooked)
                {
                    _exitHooked = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                }
            }
            reason = _ok ? "" : _reason;
            return _ok;
        }
    }

    /// <summary>Forgets the ADLX system (after a TERMINATED / INVALID_OBJECT result) so the next <see cref="TryInitialize"/> starts over.</summary>
    private void Invalidate()
    {
        lock (_gate)
        {
            _ok = false;
            _system = 0;
        }
    }

    private void Shutdown()
    {
        lock (_gate)
        {
            try
            {
                if (_ok && _terminateFn != 0) ((delegate* unmanaged[Cdecl]<int>)_terminateFn)();
            }
            catch (Exception) { /* process is exiting */ }
            _ok = false;
            _system = 0;
        }
    }

    /// <summary>ADLX_GPU_TYPE: 0 undefined, 1 integrated, 2 discrete.</summary>
    private const int GpuTypeDiscrete = 2;

    /// <summary>Index of the GPU to manage: the first discrete AMD GPU, else the first AMD GPU (else -1). Global 3D settings follow the discrete card on hybrid systems.</summary>
    internal static int ChooseGpu(IReadOnlyList<(string? Vendor, int Type)> gpus)
    {
        var firstAmd = -1;
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!string.Equals(gpus[i].Vendor, AmdVendorId, StringComparison.OrdinalIgnoreCase)) continue;
            if (gpus[i].Type == GpuTypeDiscrete) return i;
            if (firstAmd < 0) firstAmd = i;
        }
        return firstAmd;
    }

    private bool Initialize(out string reason)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            reason = "AMD ADLX needs 64-bit Windows.";
            return false;
        }

        var path = Path.Combine(Environment.SystemDirectory, DllName);
        nint lib = 0;
        if (File.Exists(path) && !NativeLibrary.TryLoad(path, out lib)) lib = 0;
        if (lib == 0)
        {
            reason = $"{DllName} not found or not loadable; no AMD Adrenalin driver installed.";
            return false;
        }

        if (!NativeLibrary.TryGetExport(lib, "ADLXQueryFullVersion", out var qv) ||
            !NativeLibrary.TryGetExport(lib, "ADLXInitialize", out var init))
        {
            reason = $"{DllName} does not export ADLXQueryFullVersion/ADLXInitialize (driver too old?).";
            return false;
        }

        ulong ver = 0;
        var r = ((delegate* unmanaged[Cdecl]<ulong*, int>)qv)(&ver);
        if (!Succeeded(r) || ver == 0)
        {
            reason = $"ADLXQueryFullVersion failed (ADLX_RESULT {r}).";
            return false;
        }
        var verText = $"{(ver >> 48) & 0xFFFF}.{(ver >> 32) & 0xFFFF}.{(ver >> 16) & 0xFFFF}.{ver & 0xFFFF}";

        nint system = 0;
        // Initialize with the driver's own ADLX version: the interfaces we use exist since ADLX 1.0 and later ones are reached through QueryInterface.
        r = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)init)(ver, &system);
        if (!Succeeded(r) || system == 0)
        {
            reason = $"ADLXInitialize failed (ADLX_RESULT {r}; ADLX {verText}). The AMD driver may be too old or the GPU is not an AMD Radeon.";
            return false;
        }

        _system = system;
        _terminateFn = NativeLibrary.TryGetExport(lib, "ADLXTerminate", out var term) ? term : 0;
        var gpus = new List<string>();
        var found = new List<(string? Vendor, int Type)>();
        var names = new List<string>();
        nint list = 0;
        try
        {
            // IADLXSystem has no Acquire/Release: slot 1 = GetGPUs.
            r = Call_PtrOut(system, 1, out list);
            if (!Succeeded(r) || list == 0)
            {
                reason = $"ADLX could not list GPUs (ADLX_RESULT {r}).";
                return false;
            }
            var count = (int)Call_UIntRet(list, 3); // IADLXGPUList::Size
            for (var i = 0; i < count; i++)
            {
                if (!Succeeded(Call_UIntPtrOut(list, 11, (uint)i, out var gpu)) || gpu == 0) continue; // At_GPUList
                try
                {
                    var vendor = ReadAnsi(gpu, 3);
                    var name = ReadAnsi(gpu, 7) ?? "(unnamed GPU)";
                    // IADLXGPU slot 5 = Type (ADLX_GPU_TYPE; ISystem.h IADLXGPUVtbl: 3 VendorId, 4 ASICFamilyType, 5 Type, 6 IsExternal, 7 Name)
                    var type = Succeeded(Call_IntOut(gpu, 5, out var t)) ? t : 0;
                    gpus.Add($"{name} (vendor {vendor ?? "?"}{(type == GpuTypeDiscrete ? ", discrete" : type == 1 ? ", integrated" : "")})");
                    found.Add((vendor, type));
                    names.Add(name);
                }
                finally { Release(gpu); }
            }
        }
        finally { if (list != 0) Release(list); }

        var chosen = ChooseGpu(found);
        var chosenName = chosen >= 0 ? names[chosen] : null;
        _info = new AdlxInfo(path, verText, gpus, chosenName);
        if (chosen < 0)
        {
            reason = "ADLX initialized but reports no AMD GPU.";
            return false;
        }
        _gpuIndex = chosen;
        reason = "";
        return true;
    }

    public IAdlxSession OpenSession()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!TryInitialize(out var reason)) throw new InvalidOperationException(reason);
            try { return new Session(this, _system, _gpuIndex, _info.GpuInUse ?? "AMD GPU"); }
            catch (Session.Failed f) when (attempt == 0 && IsStaleCode(f.Result))
            {
                Log.Info($"AMD: ADLX session is stale ({f.Message}); re-initializing.");
                Invalidate();
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ session

    private sealed class Session : IAdlxSession
    {
        private readonly AdlxNative _owner;
        private readonly nint _system;
        private nint _list, _gpu, _services, _services1;
        private bool _services1Tried;

        public string GpuName { get; }
        public string? LastError { get; private set; }

        public Session(AdlxNative owner, nint system, int gpuIndex, string gpuName)
        {
            _owner = owner;
            _system = system;
            GpuName = gpuName;
            try
            {
                Check(Call_PtrOut(_system, 1, out _list), "GetGPUs");
                Check(Call_UIntPtrOut(_list, 11, (uint)gpuIndex, out _gpu), "At_GPUList");
                Check(Call_PtrOut(_system, 7, out _services), "Get3DSettingsServices"); // IADLXSystem slot 7
                Require(_services, "3D settings services");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Release(_services1);
            Release(_services);
            Release(_gpu);
            Release(_list);
            _services1 = _services = _gpu = _list = 0;
        }

        private sealed class Unsupported : Exception { }

        internal sealed class Failed(string message, int result = 3) : Exception(message)
        {
            public int Result { get; } = result;
        }

        /// <summary>A TERMINATED / INVALID_OBJECT result means the cached ADLX system is dead: the next session re-initializes.</summary>
        private void NoteStale(Exception ex)
        {
            if (ex is Failed { Result: var r } && IsStaleCode(r)) _owner.Invalidate();
        }

        private static void Check(int result, string what)
        {
            if (!Succeeded(result)) throw new Failed($"{what} failed (ADLX_RESULT {result})", result);
        }

        private static void Require(nint p, string what)
        {
            if (p == 0) throw new Failed($"{what}: null interface");
        }

        /// <summary>Gets a feature interface from the services object; throws <see cref="Unsupported"/> when the driver does not expose it or IsSupported is false.</summary>
        private nint Feature(Feat feat)
        {
            int r;
            nint ifc;
            switch (feat)
            {
                case Feat.Rsr: // no GPU argument: RSR is global
                    r = Call_PtrOut(_services, 14, out ifc);
                    break;
                case Feat.Afmf: // IADLX3DSettingsServices1::GetAMDFluidMotionFrames (global)
                    if (!_services1Tried)
                    {
                        _services1Tried = true;
                        if (!Succeeded(Call_QueryInterface(_services, "IADLX3DSettingsServices1", out _services1))) _services1 = 0;
                    }
                    if (_services1 == 0) throw new Unsupported();
                    r = Call_PtrOut(_services1, 17, out ifc);
                    break;
                default:
                    r = Call_GpuPtrOut(_services, (int)feat, _gpu, out ifc);
                    break;
            }
            if (IsUnsupportedCode(r)) throw new Unsupported();
            Check(r, feat.ToString());
            Require(ifc, feat.ToString());
            try
            {
                var sr = Call_BoolOut(ifc, 3, out var supported);
                // A failing capability probe (seen as ADLX_FAIL for RSR on a Ryzen iGPU) means "not usable here", not a hard error.
                if (!Succeeded(sr) || !supported) throw new Unsupported();
            }
            catch
            {
                Release(ifc);
                throw;
            }
            return ifc;
        }

        public bool TryGetRange(AmdSettingId id, out int min, out int max, out int step)
        {
            min = max = 0;
            step = 1;
            try
            {
                var (feat, slot) = id switch
                {
                    AmdSettingId.ChillMinFps or AmdSettingId.ChillMaxFps => (Feat.Chill, 5),
                    AmdSettingId.FrtcFps or AmdSettingId.FrtcValue => (Feat.Frtc, 5),
                    AmdSettingId.RsrSharpness => (Feat.Rsr, 6),
                    AmdSettingId.RisSharpness => (Feat.Sharpening, 5),
                    AmdSettingId.BoostMinResolution => (Feat.Boost, 5),
                    _ => (Feat.None, 0),
                };
                if (feat == Feat.None) return false;
                var ifc = Feature(feat);
                try
                {
                    if (!Succeeded(Call_RangeOut(ifc, slot, out var range))) return false;
                    min = range.Min;
                    max = range.Max;
                    step = range.Step > 0 ? range.Step : 1;
                    return max >= min;
                }
                finally { Release(ifc); }
            }
            catch (Exception) { return false; }
        }

        public AdlxStatus Read(AmdSettingId id, out int value)
        {
            value = 0;
            LastError = null;
            try
            {
                value = id switch
                {
                    AmdSettingId.ChillEnabled => ReadBool(Feat.Chill, 4),
                    AmdSettingId.ChillMinFps => ReadInt(Feat.Chill, 6),
                    AmdSettingId.ChillMaxFps => ReadInt(Feat.Chill, 7),
                    AmdSettingId.FrtcFps => ReadBool(Feat.Frtc, 4) != 0 ? ReadInt(Feat.Frtc, 6) : 0,
                    AmdSettingId.RsrEnabled => ReadBool(Feat.Rsr, 4),
                    AmdSettingId.RsrSharpness => ReadInt(Feat.Rsr, 7),
                    AmdSettingId.AfmfEnabled => ReadBool(Feat.Afmf, 4),
                    AmdSettingId.AntiLag => ReadAntiLag(),
                    AmdSettingId.FrtcEnabled => ReadBool(Feat.Frtc, 4),
                    AmdSettingId.FrtcValue => ReadInt(Feat.Frtc, 6),
                    AmdSettingId.AntiLagEnabled => ReadBool(Feat.AntiLag, 4),
                    AmdSettingId.AntiLagLevel => ReadAntiLagLevel(),
                    AmdSettingId.RisEnabled => ReadBool(Feat.Sharpening, 4),
                    AmdSettingId.RisSharpness => ReadInt(Feat.Sharpening, 6),
                    AmdSettingId.WaitForVSync => ReadInt(Feat.Vsync, 5),
                    AmdSettingId.EnhancedSync => ReadBool(Feat.EnhancedSync, 4),
                    AmdSettingId.BoostEnabled => ReadBool(Feat.Boost, 4),
                    AmdSettingId.BoostMinResolution => ReadInt(Feat.Boost, 6),
                    _ => throw new Failed($"no ADLX mapping for {id}"),
                };
                return AdlxStatus.Ok;
            }
            catch (Unsupported) { return AdlxStatus.Unsupported; }
            catch (Exception ex) { LastError = ex.Message; NoteStale(ex); return AdlxStatus.Error; }
        }

        public AdlxStatus Write(AmdSettingId id, int value)
        {
            LastError = null;
            try
            {
                switch (id)
                {
                    case AmdSettingId.ChillEnabled: WriteBool(Feat.Chill, 8, value); break;
                    case AmdSettingId.ChillMinFps: WriteInt(Feat.Chill, 9, value); break;
                    case AmdSettingId.ChillMaxFps: WriteInt(Feat.Chill, 10, value); break;
                    case AmdSettingId.FrtcFps:
                        if (value == 0) WriteBool(Feat.Frtc, 7, 0);
                        else
                        {
                            WriteInt(Feat.Frtc, 8, value);
                            WriteBool(Feat.Frtc, 7, 1);
                        }
                        break;
                    case AmdSettingId.RsrEnabled: WriteBool(Feat.Rsr, 5, value); break;
                    case AmdSettingId.RsrSharpness: WriteInt(Feat.Rsr, 8, value); break;
                    case AmdSettingId.AfmfEnabled: WriteBool(Feat.Afmf, 5, value); break;
                    case AmdSettingId.AntiLag: WriteAntiLag(value); break;
                    case AmdSettingId.FrtcEnabled: WriteBool(Feat.Frtc, 7, value); break;
                    case AmdSettingId.FrtcValue: WriteInt(Feat.Frtc, 8, value); break;
                    case AmdSettingId.AntiLagEnabled: WriteBool(Feat.AntiLag, 5, value); break;
                    case AmdSettingId.AntiLagLevel: WriteAntiLagLevel(value); break;
                    case AmdSettingId.RisEnabled: WriteBool(Feat.Sharpening, 7, value); break;
                    case AmdSettingId.RisSharpness: WriteInt(Feat.Sharpening, 8, value); break;
                    case AmdSettingId.WaitForVSync: WriteInt(Feat.Vsync, 6, value); break;
                    case AmdSettingId.EnhancedSync: WriteBool(Feat.EnhancedSync, 5, value); break;
                    case AmdSettingId.BoostEnabled: WriteBool(Feat.Boost, 7, value); break;
                    case AmdSettingId.BoostMinResolution: WriteInt(Feat.Boost, 8, value); break;
                    default: throw new Failed($"no ADLX mapping for {id}");
                }
                return AdlxStatus.Ok;
            }
            catch (Unsupported) { return AdlxStatus.Unsupported; }
            catch (Exception ex) { LastError = ex.Message; NoteStale(ex); return AdlxStatus.Error; }
        }

        private int ReadBool(Feat feat, int slot)
        {
            var ifc = Feature(feat);
            try
            {
                Check(Call_BoolOut(ifc, slot, out var b), $"{feat} slot {slot}");
                return b ? 1 : 0;
            }
            finally { Release(ifc); }
        }

        private int ReadInt(Feat feat, int slot)
        {
            var ifc = Feature(feat);
            try
            {
                Check(Call_IntOut(ifc, slot, out var v), $"{feat} slot {slot}");
                return v;
            }
            finally { Release(ifc); }
        }

        private void WriteBool(Feat feat, int slot, int value)
        {
            var ifc = Feature(feat);
            try { Check(Call_BoolIn(ifc, slot, value != 0), $"{feat} slot {slot}"); }
            finally { Release(ifc); }
        }

        private void WriteInt(Feat feat, int slot, int value)
        {
            var ifc = Feature(feat);
            try { Check(Call_IntIn(ifc, slot, value), $"{feat} slot {slot}"); }
            finally { Release(ifc); }
        }

        // IADLX3DAntiLag1 (QueryInterface "IADLX3DAntiLag1"): slot 6 GetLevel, 7 SetLevel; ADLX_ANTILAG_STATE 0 = ANTILAG, 1 = ANTILAGNEXT.
        private int ReadAntiLag()
        {
            var ifc = Feature(Feat.AntiLag);
            try
            {
                Check(Call_BoolOut(ifc, 4, out var enabled), "AntiLag.IsEnabled");
                if (!enabled) return 0;
                if (!Succeeded(Call_QueryInterface(ifc, "IADLX3DAntiLag1", out var al1)) || al1 == 0) return 1;
                try { return Succeeded(Call_IntOut(al1, 6, out var level)) && level == 1 ? 2 : 1; }
                finally { Release(al1); }
            }
            finally { Release(ifc); }
        }

        // Raw level (0 = ANTILAG, 1 = ANTILAGNEXT), independent of the enabled flag; Unsupported when IADLX3DAntiLag1 is missing.
        private int ReadAntiLagLevel()
        {
            var ifc = Feature(Feat.AntiLag);
            try
            {
                if (!Succeeded(Call_QueryInterface(ifc, "IADLX3DAntiLag1", out var al1)) || al1 == 0) throw new Unsupported();
                try { Check(Call_IntOut(al1, 6, out var level), "AntiLag1.GetLevel"); return level; }
                finally { Release(al1); }
            }
            finally { Release(ifc); }
        }

        private void WriteAntiLagLevel(int value)
        {
            var ifc = Feature(Feat.AntiLag);
            try
            {
                if (!Succeeded(Call_QueryInterface(ifc, "IADLX3DAntiLag1", out var al1)) || al1 == 0) throw new Unsupported();
                try { Check(Call_IntIn(al1, 7, value), "AntiLag1.SetLevel"); }
                finally { Release(al1); }
            }
            finally { Release(ifc); }
        }

        private void WriteAntiLag(int value)
        {
            var ifc = Feature(Feat.AntiLag);
            try
            {
                if (value != 0)
                {
                    if (!Succeeded(Call_QueryInterface(ifc, "IADLX3DAntiLag1", out var al1)) || al1 == 0)
                    {
                        if (value == 2) throw new Unsupported(); // Anti-Lag 2 ("next") needs IADLX3DAntiLag1
                    }
                    else
                    {
                        try { Check(Call_IntIn(al1, 7, value == 2 ? 1 : 0), "AntiLag1.SetLevel"); }
                        finally { Release(al1); }
                    }
                }
                Check(Call_BoolIn(ifc, 5, value != 0), "AntiLag.SetEnabled");
            }
            finally { Release(ifc); }
        }
    }

    // Feature interfaces. Values 3..9 are the IADLX3DSettingsServices vtable slots of their Get* methods (all take an IADLXGPU*).
    private enum Feat { None = 0, AntiLag = 3, Chill = 4, Boost = 5, Sharpening = 6, EnhancedSync = 7, Vsync = 8, Frtc = 9, Rsr = 100, Afmf = 101 }

    // ------------------------------------------------------------------------------------------------ raw vtable calls

    [StructLayout(LayoutKind.Sequential)]
    private struct IntRange { public int Min, Max, Step; }

    private static nint Slot(nint obj, int slot)
    {
        if (obj == 0) throw new InvalidOperationException("ADLX: null interface pointer");
        var vtbl = *(nint*)obj;
        if (vtbl == 0) throw new InvalidOperationException("ADLX: interface has no vtable");
        var fn = ((nint*)vtbl)[slot];
        if (fn == 0) throw new InvalidOperationException($"ADLX: vtable slot {slot} is empty");
        return fn;
    }

    /// <summary>IADLXInterface::Release (slot 1). Never throws.</summary>
    private static void Release(nint obj)
    {
        if (obj == 0) return;
        try { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, 1))(obj); }
        catch (Exception) { /* best effort */ }
    }

    private static int Call_PtrOut(nint obj, int slot, out nint result)
    {
        nint r = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(obj, slot))(obj, &r);
        result = r;
        return rc;
    }

    private static int Call_GpuPtrOut(nint obj, int slot, nint gpu, out nint result)
    {
        nint r = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(obj, slot))(obj, gpu, &r);
        result = r;
        return rc;
    }

    private static uint Call_UIntRet(nint obj, int slot) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, slot))(obj);

    private static int Call_UIntPtrOut(nint obj, int slot, uint index, out nint result)
    {
        nint r = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(obj, slot))(obj, index, &r);
        result = r;
        return rc;
    }

    private static int Call_QueryInterface(nint obj, string iid, out nint result)
    {
        nint r = 0;
        var buf = Marshal.StringToHGlobalUni(iid);
        try
        {
            var rc = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(obj, 2))(obj, buf, &r);
            result = r;
            return rc;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int Call_BoolOut(nint obj, int slot, out bool value)
    {
        byte b = 0; // adlx_bool is adlx_uint8 in the C ABI
        var rc = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(obj, slot))(obj, &b);
        value = b != 0;
        return rc;
    }

    private static int Call_IntOut(nint obj, int slot, out int value)
    {
        int v = 0;
        var rc = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(obj, slot))(obj, &v);
        value = v;
        return rc;
    }

    private static int Call_RangeOut(nint obj, int slot, out IntRange range)
    {
        IntRange v = default;
        var rc = ((delegate* unmanaged[Stdcall]<nint, IntRange*, int>)Slot(obj, slot))(obj, &v);
        range = v;
        return rc;
    }

    private static int Call_BoolIn(nint obj, int slot, bool value) =>
        ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(obj, slot))(obj, value ? (byte)1 : (byte)0);

    private static int Call_IntIn(nint obj, int slot, int value) =>
        ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(obj, slot))(obj, value);

    /// <summary>Calls a <c>(pThis, const char** out)</c> getter; the string is owned by the interface, so copy it immediately.</summary>
    private static string? ReadAnsi(nint obj, int slot)
    {
        byte* p = null;
        var rc = ((delegate* unmanaged[Stdcall]<nint, byte**, int>)Slot(obj, slot))(obj, &p);
        if (!Succeeded(rc) || p == null) return null;
        var len = 0;
        while (len < 512 && p[len] != 0) len++;
        return Encoding.UTF8.GetString(p, len);
    }
}
