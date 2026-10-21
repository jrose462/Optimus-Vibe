namespace OptimusVibe.Driver;

/// <summary>Logical AMD driver settings Optimus-Vibe can read and write. Each maps to one or two ADLX calls in <see cref="AdlxNative"/>.</summary>
internal enum AmdSettingId
{
    ChillEnabled,
    ChillMinFps,
    ChillMaxFps,
    FrtcFps,
    RsrEnabled,
    RsrSharpness,
    AfmfEnabled,
    AntiLag,
    RisEnabled,
    RisSharpness,
    WaitForVSync,
    EnhancedSync,
    BoostEnabled,
    BoostMinResolution,

    // Snapshot-only ids (not user keys): the raw state behind the composite FrtcFps / AntiLag settings, so a restore brings back
    // the stored fps / level as well as the enabled flag.
    FrtcEnabled,
    FrtcValue,
    AntiLagEnabled,
    AntiLagLevel,
}

internal enum AdlxStatus
{
    Ok,

    /// <summary>The driver / GPU does not expose this setting (interface missing or IsSupported == false). Not an error.</summary>
    Unsupported,

    /// <summary>The native call failed (see <see cref="IAdlxSession.LastError"/>).</summary>
    Error,
}

/// <summary>What `driver status` reports about the ADLX runtime.</summary>
internal sealed record AdlxInfo(string? DllPath, string? DriverAdlxVersion, IReadOnlyList<string> Gpus, string? GpuInUse);

/// <summary>The slice of ADLX Optimus-Vibe needs. Implemented by <see cref="AdlxNative"/>; tests inject a fake.</summary>
internal interface IAdlxApi
{
    /// <summary>Loads amdadlx64.dll, checks the version and initializes ADLX. False (with a reason) when the AMD driver is missing or too old.</summary>
    bool TryInitialize(out string reason);

    /// <summary>Valid after a successful <see cref="TryInitialize"/>.</summary>
    AdlxInfo Info { get; }

    /// <summary>Opens a session on the AMD GPU in use (global 3D settings: ADLX has no per-application scope).</summary>
    IAdlxSession OpenSession();
}

internal interface IAdlxSession : IDisposable
{
    string GpuName { get; }

    /// <summary>Last native error text for <see cref="AdlxStatus.Error"/> results.</summary>
    string? LastError { get; }

    /// <summary>Reads the setting as an integer (bool = 0/1, AntiLag = 0 off / 1 on / 2 next, WaitForVSync = 0..3, FrtcFps 0 = off).</summary>
    AdlxStatus Read(AmdSettingId id, out int value);

    AdlxStatus Write(AmdSettingId id, int value);

    /// <summary>The driver-reported allowed range (and step, at least 1) for numeric settings; false when unknown.</summary>
    bool TryGetRange(AmdSettingId id, out int min, out int max, out int step);
}
