using System.Runtime.InteropServices;
using OptimusVibe.Logging;

namespace OptimusVibe.Driver;

/// <summary>The slice of the NVAPI driver-settings (DRS) API Optimus-Vibe needs. Implemented by <see cref="NvApiNative"/>; tests inject a fake.</summary>
internal interface INvDrsApi
{
    /// <summary>Loads nvapi64.dll and calls NvAPI_Initialize. False (with a reason) on machines without an NVIDIA driver.</summary>
    bool TryInitialize(out string error);

    /// <summary>Creates a DRS session and loads the driver's settings database into it.</summary>
    IDrsSession OpenSession();
}

internal interface IDrsSession : IDisposable
{
    /// <summary>The profile an executable (file name, case-insensitive) belongs to, or false when it is in no profile.</summary>
    bool FindApplicationProfile(string exe, out nint profile);

    bool FindProfileByName(string name, out nint profile);

    nint CreateProfile(string name);

    void CreateApplication(nint profile, string exe);

    string GetProfileName(nint profile);

    /// <summary>Effective DWORD value of a setting for the profile (may be inherited). False if not set anywhere or not a DWORD.</summary>
    bool TryGetDword(nint profile, uint id, out uint value, out bool inThisProfile);

    void SetDword(nint profile, uint id, uint value);

    void Save();
}

/// <summary>
/// Minimal P/Invoke layer over NVIDIA's nvapi64.dll (driver settings only). The DLL is loaded with <see cref="NativeLibrary"/> and
/// every function is resolved through the exported <c>nvapi_QueryInterface(uint id)</c> entry point.
/// <para>
/// Interface ids (NVAPI function ids): from NVIDIA's MIT-licensed NVAPI SDK, nvapi_interface.h
/// (https://github.com/NVIDIA/nvapi). The ids are stable across driver releases and were also verified against the installed
/// driver (QueryInterface returns non-zero; read-only calls returned the expected data). Struct layouts follow nvapi.h;
/// wrong struct versions return status -9.
/// </para>
/// <para>
/// Struct layouts follow nvapi.h / NvApiDriverSettings.h: NvAPI_UnicodeString = NvU16[2048]; NvAPI_ShortString = char[64];
/// version = sizeof(struct) | (version &lt;&lt; 16).
/// </para>
/// </summary>
internal sealed unsafe class NvApiNative : INvDrsApi
{
    internal static readonly NvApiNative Instance = new();

    // NVAPI function ids.
    private const uint IdInitialize = 0x0150E828;               // NvAPI_Initialize
    private const uint IdUnload = 0xD22BDD7E;                   // NvAPI_Unload
    private const uint IdGetErrorMessage = 0x6C2D048C;          // NvAPI_GetErrorMessage
    private const uint IdDrsCreateSession = 0x0694D52E;         // NvAPI_DRS_CreateSession
    private const uint IdDrsDestroySession = 0xDAD9CFF8;        // NvAPI_DRS_DestroySession
    private const uint IdDrsLoadSettings = 0x375DBD6B;          // NvAPI_DRS_LoadSettings
    private const uint IdDrsSaveSettings = 0xFCBC7E14;          // NvAPI_DRS_SaveSettings
    private const uint IdDrsFindApplicationByName = 0xEEE566B2; // NvAPI_DRS_FindApplicationByName
    private const uint IdDrsFindProfileByName = 0x7E4A9A0B;     // NvAPI_DRS_FindProfileByName
    private const uint IdDrsCreateProfile = 0xCC176068;         // NvAPI_DRS_CreateProfile
    private const uint IdDrsCreateApplication = 0x4347A9DE;     // NvAPI_DRS_CreateApplication
    private const uint IdDrsGetSetting = 0x73BF8338;            // NvAPI_DRS_GetSetting
    private const uint IdDrsSetSetting = 0x577DD202;            // NvAPI_DRS_SetSetting
    private const uint IdDrsGetProfileInfo = 0x61CD6FD6;        // NvAPI_DRS_GetProfileInfo

    internal const int StatusOk = 0;
    internal const int StatusSettingNotFound = -160;
    internal const int StatusProfileNotFound = -163;
    internal const int StatusExecutableNotFound = -166;

    private const int UnicodeChars = 2048;
    private const int SettingTypeDword = 0;
    private const int LocationCurrentProfile = 0;

    private const uint ProfileVersion = (uint)(4116 | (1 << 16));       // NVDRS_PROFILE_V1
    private const uint ApplicationVersion = (uint)(20492 | (4 << 16));  // NVDRS_APPLICATION_V4
    private const uint SettingVersion = (uint)(12320 | (1 << 16));      // NVDRS_SETTING_V1

    [StructLayout(LayoutKind.Sequential)]
    private struct NvdrsProfile
    {
        public uint Version;
        public fixed ushort ProfileName[UnicodeChars];
        public uint GpuSupport;
        public uint IsPredefined;
        public uint NumOfApps;
        public uint NumOfSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvdrsApplication
    {
        public uint Version;
        public uint IsPredefined;
        public fixed ushort AppName[UnicodeChars];
        public fixed ushort UserFriendlyName[UnicodeChars];
        public fixed ushort Launcher[UnicodeChars];
        public fixed ushort FileInFolder[UnicodeChars];
        public uint Flags; // isMetro:1, isCommandLine:1, reserved:30
        public fixed ushort CommandLine[UnicodeChars];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvdrsSetting
    {
        public uint Version;
        public fixed ushort SettingName[UnicodeChars];
        public uint SettingId;
        public uint SettingType;
        public uint SettingLocation;
        public uint IsCurrentPredefined;
        public uint IsPredefinedValid;
        public fixed byte PredefinedValue[4100]; // NVDRS_SETTING_VALUES union: u32 / {u32 len; u8[4096]} / wchar[2048]
        public fixed byte CurrentValue[4100];
    }

    private readonly object _gate = new();
    private bool? _initialized;
    private string _error = "";
    private nint _library;
    private delegate* unmanaged[Cdecl]<uint, nint> _queryInterface;

    private delegate* unmanaged[Cdecl]<int> _initialize;
    private delegate* unmanaged[Cdecl]<int> _unload;
    private delegate* unmanaged[Cdecl]<int, byte*, int> _getErrorMessage;
    private delegate* unmanaged[Cdecl]<nint*, int> _createSession;
    private delegate* unmanaged[Cdecl]<nint, int> _destroySession;
    private delegate* unmanaged[Cdecl]<nint, int> _loadSettings;
    private delegate* unmanaged[Cdecl]<nint, int> _saveSettings;
    private delegate* unmanaged[Cdecl]<nint, ushort*, nint*, NvdrsApplication*, int> _findApplicationByName;
    private delegate* unmanaged[Cdecl]<nint, ushort*, nint*, int> _findProfileByName;
    private delegate* unmanaged[Cdecl]<nint, NvdrsProfile*, nint*, int> _createProfile;
    private delegate* unmanaged[Cdecl]<nint, nint, NvdrsApplication*, int> _createApplication;
    private delegate* unmanaged[Cdecl]<nint, nint, uint, NvdrsSetting*, int> _getSetting;
    private delegate* unmanaged[Cdecl]<nint, nint, NvdrsSetting*, int> _setSetting;
    private delegate* unmanaged[Cdecl]<nint, nint, NvdrsProfile*, int> _getProfileInfo;

    public bool TryInitialize(out string error)
    {
        lock (_gate)
        {
            if (!_initialized.HasValue) _initialized = Load();
            error = _error;
            return _initialized.Value;
        }
    }

    private bool Load()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out _library))
            {
                _error = "NVIDIA driver API (nvapi64.dll) not found; no NVIDIA driver installed.";
                return false;
            }
            if (!NativeLibrary.TryGetExport(_library, "nvapi_QueryInterface", out var qi))
            {
                _error = "NVIDIA driver API: nvapi_QueryInterface not exported by nvapi64.dll.";
                return false;
            }
            _queryInterface = (delegate* unmanaged[Cdecl]<uint, nint>)qi;

            _initialize = (delegate* unmanaged[Cdecl]<int>)Query(IdInitialize);
            _unload = (delegate* unmanaged[Cdecl]<int>)Query(IdUnload);
            _getErrorMessage = (delegate* unmanaged[Cdecl]<int, byte*, int>)Query(IdGetErrorMessage);
            _createSession = (delegate* unmanaged[Cdecl]<nint*, int>)Query(IdDrsCreateSession);
            _destroySession = (delegate* unmanaged[Cdecl]<nint, int>)Query(IdDrsDestroySession);
            _loadSettings = (delegate* unmanaged[Cdecl]<nint, int>)Query(IdDrsLoadSettings);
            _saveSettings = (delegate* unmanaged[Cdecl]<nint, int>)Query(IdDrsSaveSettings);
            _findApplicationByName = (delegate* unmanaged[Cdecl]<nint, ushort*, nint*, NvdrsApplication*, int>)Query(IdDrsFindApplicationByName);
            _findProfileByName = (delegate* unmanaged[Cdecl]<nint, ushort*, nint*, int>)Query(IdDrsFindProfileByName);
            _createProfile = (delegate* unmanaged[Cdecl]<nint, NvdrsProfile*, nint*, int>)Query(IdDrsCreateProfile);
            _createApplication = (delegate* unmanaged[Cdecl]<nint, nint, NvdrsApplication*, int>)Query(IdDrsCreateApplication);
            _getSetting = (delegate* unmanaged[Cdecl]<nint, nint, uint, NvdrsSetting*, int>)Query(IdDrsGetSetting);
            _setSetting = (delegate* unmanaged[Cdecl]<nint, nint, NvdrsSetting*, int>)Query(IdDrsSetSetting);
            _getProfileInfo = (delegate* unmanaged[Cdecl]<nint, nint, NvdrsProfile*, int>)Query(IdDrsGetProfileInfo);

            var status = _initialize();
            if (status != StatusOk)
            {
                _error = $"NVIDIA driver API could not be initialized: {Describe(status)}";
                return false;
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _unload(); } catch { /* process is ending */ }
            };
            return true;
        }
        catch (Exception ex)
        {
            _error = $"NVIDIA driver API could not be initialized: {ex.Message}";
            return false;
        }
    }

    private nint Query(uint id)
    {
        var fn = _queryInterface(id);
        if (fn == 0) throw new EntryPointNotFoundException($"NVAPI interface 0x{id:X8} is not available in this driver.");
        return fn;
    }

    private string Describe(int status)
    {
        try
        {
            if (_getErrorMessage != null)
            {
                var buf = stackalloc byte[64];
                if (_getErrorMessage(status, buf) == StatusOk)
                {
                    var text = Marshal.PtrToStringAnsi((nint)buf);
                    if (!string.IsNullOrWhiteSpace(text)) return $"{text} (NVAPI status {status})";
                }
            }
        }
        catch
        {
            // fall through
        }
        return $"NVAPI status {status}";
    }

    internal void Check(int status, string call)
    {
        if (status != StatusOk) throw new NvApiException(status, $"{call} failed: {Describe(status)}");
    }

    public IDrsSession OpenSession()
    {
        if (!TryInitialize(out var error)) throw new InvalidOperationException(error);
        nint handle;
        Check(_createSession(&handle), "NvAPI_DRS_CreateSession");
        try
        {
            Check(_loadSettings(handle), "NvAPI_DRS_LoadSettings");
        }
        catch
        {
            _destroySession(handle);
            throw;
        }
        return new Session(this, handle);
    }

    private static void CopyString(string s, ushort* dest)
    {
        var n = Math.Min(s.Length, UnicodeChars - 1);
        for (var i = 0; i < n; i++) dest[i] = s[i];
        dest[n] = 0;
    }

    private static string ReadString(ushort* src)
    {
        var n = 0;
        while (n < UnicodeChars && src[n] != 0) n++;
        return new string((char*)src, 0, n);
    }

    private sealed class Session : IDrsSession
    {
        private readonly NvApiNative _api;
        private nint _handle;

        public Session(NvApiNative api, nint handle)
        {
            _api = api;
            _handle = handle;
        }

        public bool FindApplicationProfile(string exe, out nint profile)
        {
            var name = stackalloc ushort[UnicodeChars];
            CopyString(exe, name);
            var app = (NvdrsApplication*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsApplication));
            try
            {
                app->Version = ApplicationVersion;
                nint p;
                var status = _api._findApplicationByName(_handle, name, &p, app);
                if (status == StatusExecutableNotFound || status == StatusProfileNotFound) { profile = 0; return false; }
                _api.Check(status, "NvAPI_DRS_FindApplicationByName");
                profile = p;
                return true;
            }
            finally { NativeMemory.Free(app); }
        }

        public bool FindProfileByName(string name, out nint profile)
        {
            var buf = stackalloc ushort[UnicodeChars];
            CopyString(name, buf);
            nint p;
            var status = _api._findProfileByName(_handle, buf, &p);
            if (status == StatusProfileNotFound) { profile = 0; return false; }
            _api.Check(status, "NvAPI_DRS_FindProfileByName");
            profile = p;
            return true;
        }

        public nint CreateProfile(string name)
        {
            var info = (NvdrsProfile*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsProfile));
            try
            {
                info->Version = ProfileVersion;
                CopyString(name, info->ProfileName);
                nint p;
                _api.Check(_api._createProfile(_handle, info, &p), "NvAPI_DRS_CreateProfile");
                return p;
            }
            finally { NativeMemory.Free(info); }
        }

        public void CreateApplication(nint profile, string exe)
        {
            var app = (NvdrsApplication*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsApplication));
            try
            {
                app->Version = ApplicationVersion;
                CopyString(exe, app->AppName);
                _api.Check(_api._createApplication(_handle, profile, app), "NvAPI_DRS_CreateApplication");
            }
            finally { NativeMemory.Free(app); }
        }

        public string GetProfileName(nint profile)
        {
            var info = (NvdrsProfile*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsProfile));
            try
            {
                info->Version = ProfileVersion;
                _api.Check(_api._getProfileInfo(_handle, profile, info), "NvAPI_DRS_GetProfileInfo");
                return ReadString(info->ProfileName);
            }
            finally { NativeMemory.Free(info); }
        }

        public bool TryGetDword(nint profile, uint id, out uint value, out bool inThisProfile)
        {
            value = 0;
            inThisProfile = false;
            var s = (NvdrsSetting*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsSetting));
            try
            {
                s->Version = SettingVersion;
                var status = _api._getSetting(_handle, profile, id, s);
                if (status == StatusSettingNotFound) return false;
                _api.Check(status, "NvAPI_DRS_GetSetting");
                if (s->SettingType != SettingTypeDword) return false;
                value = *(uint*)s->CurrentValue;
                inThisProfile = s->SettingLocation == LocationCurrentProfile;
                return true;
            }
            finally { NativeMemory.Free(s); }
        }

        public void SetDword(nint profile, uint id, uint value)
        {
            var s = (NvdrsSetting*)NativeMemory.AllocZeroed((nuint)sizeof(NvdrsSetting));
            try
            {
                s->Version = SettingVersion;
                s->SettingId = id;
                s->SettingType = SettingTypeDword;
                s->SettingLocation = LocationCurrentProfile;
                *(uint*)s->CurrentValue = value;
                _api.Check(_api._setSetting(_handle, profile, s), "NvAPI_DRS_SetSetting");
            }
            finally { NativeMemory.Free(s); }
        }

        public void Save() => _api.Check(_api._saveSettings(_handle), "NvAPI_DRS_SaveSettings");

        public void Dispose()
        {
            if (_handle == 0) return;
            var h = _handle;
            _handle = 0;
            try { _api._destroySession(h); }
            catch (Exception ex) { Log.Warn($"NVIDIA: DRS session cleanup failed: {ex.Message}"); }
        }
    }
}

internal sealed class NvApiException : Exception
{
    public int Status { get; }

    public NvApiException(int status, string message) : base(message) => Status = status;
}
