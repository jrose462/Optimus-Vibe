using System.Globalization;
using GameProfiles.Config;
using GameProfiles.Logging;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.Native.DRS;

namespace GameProfiles.Driver;

/// <summary>
/// Desktop (NVIDIA): writes settings into the driver's per-application DRS profile for the game's executables.
/// The profile only affects that game, so the change is persistent and <see cref="Apply"/> returns null (nothing to undo).
/// <para>Setting keys accepted (case-insensitive):</para>
/// <list type="number">
/// <item>Friendly aliases: FrameRateLimiter (fps | off), VSync (app | off | on), PowerMode (adaptive | max | optimal).</item>
/// <item>Any <see cref="KnownSettingId"/> name, e.g. PreRenderLimit; value numeric (decimal or 0x hex) or a member name of the
/// matching NvAPIWrapper.DRS.SettingValues enum (e.g. VSyncMode: ForceOff).</item>
/// <item>A raw setting id in hex, e.g. "0x10835002", with a numeric value.</item>
/// </list>
/// </summary>
public sealed class NvidiaDrsManager : IDriverSettings
{
    private static readonly object InitGate = new();
    private static bool? _initialized;
    private static string _initError = "";

    public string Vendor => "nvidia";

    // ---------------------------------------------------------------------------------------------
    // Aliases. IDs/values verified against NvAPIWrapper 0.8.1.101 KnownSettingId / SettingValues enums
    // (VSync, PowerMode) and against the installed driver's NvAPI_DRS_GetSettingNameFromId (FrameRateLimiter).

    private sealed record Alias(string Name, uint Id, string Help, Func<string, uint?> Parse, Func<uint, string> Format);

    private static readonly Dictionary<string, uint> VSyncValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = 0x60925292, // VSYNCMODE_PASSIVE: use the 3D application setting
        ["off"] = 0x08416747, // VSYNCMODE_FORCEOFF
        ["on"] = 0x47814940,  // VSYNCMODE_FORCEON
    };

    private static readonly Dictionary<string, uint> PowerValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adaptive"] = 0,
        ["max"] = 1,     // PREFERRED_PSTATE_PREFER_MAX: "Prefer maximum performance"
        ["optimal"] = 5, // PREFERRED_PSTATE_OPTIMAL_POWER (shown as "Normal" in newer control panels)
    };

    private static readonly Dictionary<string, Alias> Aliases = new Alias[]
    {
        new("FrameRateLimiter", 0x10835002, "fps (1-1023) or off",
            v => IsOff(v) ? 0u : (TryParseUInt(v, out var fps) && fps <= 1023 ? fps : null),
            n => n == 0 ? "off" : n.ToString(CultureInfo.InvariantCulture)),
        new("VSync", 0x00A879CF, "app | off | on",
            v => VSyncValues.TryGetValue(v, out var x) ? x : null,
            n => NameFor(VSyncValues, n)),
        new("PowerMode", 0x1057EB71, "adaptive | max | optimal",
            v => PowerValues.TryGetValue(v, out var x) ? x
               : string.Equals(v, "normal", StringComparison.OrdinalIgnoreCase) ? 5u : null,
            n => NameFor(PowerValues, n)),
    }.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    private sealed record Resolved(string Key, uint Id, uint Target, Func<uint, string> Format);

    // ---------------------------------------------------------------------------------------------

    public bool IsAvailable(out string reason)
    {
        if (EnsureInitialized())
        {
            reason = "";
            return true;
        }
        reason = _initError;
        return false;
    }

    public IReadOnlyList<DriverChange> Plan(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        var resolved = Resolve(settings);
        if (resolved.Count == 0) return Array.Empty<DriverChange>();

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"NVIDIA: driver settings unavailable ({reason}); current values unknown.");
            return resolved.Select(r => new DriverChange(r.Key, null, r.Format(r.Target))).ToList();
        }

        if (game.Processes.Count == 0)
            Log.Warn($"NVIDIA: {game.Name} has no 'processes'; driver profile settings would be skipped.");

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = FindExistingProfile(session, game, out _);
            return resolved.Select(r =>
            {
                string? current = null;
                if (profile != null && TryReadDword(profile, r.Id, out var value, out _))
                    current = r.Format(value);
                return new DriverChange(r.Key, current, r.Format(r.Target));
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"NVIDIA: could not read the driver profile for {game.Name}: {ex.Message}");
            return resolved.Select(r => new DriverChange(r.Key, null, r.Format(r.Target))).ToList();
        }
    }

    public string? Apply(GameDef game, IReadOnlyDictionary<string, string> settings)
    {
        var resolved = Resolve(settings);
        if (resolved.Count == 0) return null;

        if (game.Processes.Count == 0)
        {
            Log.Warn($"NVIDIA: {game.Name} has no 'processes' (exe names); driver profile settings skipped.");
            return null;
        }

        if (!IsAvailable(out var reason))
        {
            Log.Warn($"NVIDIA: driver settings skipped for {game.Name}: {reason}");
            return null;
        }

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var profile = FindExistingProfile(session, game, out var missing);
            if (profile == null)
            {
                var name = $"GameProfiles - {game.Name}";
                profile = TryFindProfileByName(session, name) ?? DriverSettingsProfile.CreateProfile(session, name, null);
                Log.Info($"NVIDIA: created driver profile '{name}'.");
            }

            foreach (var exe in missing)
            {
                // (profile, applicationName, friendlyName, launcherName, fileInFolders, isMetro, commandLine)
                ProfileApplication.CreateApplication(profile, exe.ToLowerInvariant(), null, null, null, false, null);
                Log.Info($"NVIDIA: added {exe.ToLowerInvariant()} to driver profile '{profile.Name}'.");
            }

            var dirty = missing.Count > 0;
            foreach (var r in resolved)
            {
                var hasOwn = TryReadDword(profile, r.Id, out var current, out var inProfile) && inProfile;
                if (hasOwn && current == r.Target) continue;
                profile.SetSetting(r.Id, r.Target);
                dirty = true;
                Log.Info($"NVIDIA: {profile.Name}: {r.Key} = {r.Format(r.Target)}");
            }

            if (dirty) session.Save();
            else Log.Info($"NVIDIA: driver profile '{profile.Name}' already up to date.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"NVIDIA: applying driver profile settings for {game.Name} failed: {ex.Message}", ex);
        }

        return null; // per-application profile: persistent, nothing to undo
    }

    public void Restore(string token)
    {
        // Apply never returns a token (per-app profiles are persistent).
    }

    // ---------------------------------------------------------------------------------------------

    private static bool EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized.HasValue) return _initialized.Value;
            try
            {
                NVIDIA.Initialize();
                _initialized = true;
            }
            catch (DllNotFoundException)
            {
                _initError = "NVIDIA driver API (nvapi64.dll) not found; no NVIDIA driver installed.";
                _initialized = false;
            }
            catch (Exception ex)
            {
                _initError = $"NVIDIA driver API could not be initialized: {ex.Message}";
                _initialized = false;
            }
            return _initialized.Value;
        }
    }

    private static List<Resolved> Resolve(IReadOnlyDictionary<string, string> settings)
    {
        var list = new List<Resolved>();
        foreach (var (rawKey, rawValue) in settings)
        {
            var key = (rawKey ?? "").Trim();
            var value = (rawValue ?? "").Trim();
            try
            {
                if (Aliases.TryGetValue(key, out var alias))
                {
                    var target = alias.Parse(value);
                    if (target == null)
                    {
                        Log.Warn($"NVIDIA: invalid value '{value}' for {alias.Name} (expected {alias.Help}); skipped.");
                        continue;
                    }
                    list.Add(new Resolved(alias.Name, alias.Id, target.Value, alias.Format));
                    continue;
                }

                if (Enum.TryParse<KnownSettingId>(key, ignoreCase: true, out var known) &&
                    Enum.IsDefined(typeof(KnownSettingId), known) && !key.All(char.IsDigit))
                {
                    var id = (uint)known;
                    var valueType = typeof(KnownSettingId).Assembly.GetType($"NvAPIWrapper.DRS.SettingValues.{known}");
                    uint target;
                    if (TryParseUInt(value, out var n)) target = n;
                    else if (valueType is { IsEnum: true } && Enum.TryParse(valueType, value, ignoreCase: true, out var ev))
                        target = Convert.ToUInt32(ev, CultureInfo.InvariantCulture);
                    else
                    {
                        Log.Warn($"NVIDIA: invalid value '{value}' for {known}; expected a number" +
                                 (valueType is { IsEnum: true } ? $" or one of {string.Join(", ", Enum.GetNames(valueType))}" : "") + "; skipped.");
                        continue;
                    }
                    list.Add(new Resolved(known.ToString(), id, target, v => FormatKnown(valueType, v)));
                    continue;
                }

                if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && TryParseUInt(key, out var rawId))
                {
                    if (!TryParseUInt(value, out var target))
                    {
                        Log.Warn($"NVIDIA: invalid value '{value}' for setting {key}; expected a number (decimal or 0x hex); skipped.");
                        continue;
                    }
                    list.Add(new Resolved($"0x{rawId:X8}", rawId, target, FormatNumber));
                    continue;
                }

                Log.Warn($"NVIDIA: unknown driver setting '{key}' (use FrameRateLimiter, VSync, PowerMode, a KnownSettingId name, or a 0x setting id); skipped.");
            }
            catch (Exception ex)
            {
                Log.Warn($"NVIDIA: could not interpret setting '{key}': {ex.Message}; skipped.");
            }
        }
        return list;
    }

    /// <summary>The DRS profile any of the game's executables already belongs to (first match); lists exes not in any profile.</summary>
    private static DriverSettingsProfile? FindExistingProfile(DriverSettingsSession session, GameDef game, out List<string> missing)
    {
        missing = new List<string>();
        DriverSettingsProfile? found = null;
        foreach (var exe in game.Processes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
        {
            DriverSettingsProfile? profile = null;
            try { profile = session.FindApplicationProfile(exe); }
            catch { /* NVAPI_EXECUTABLE_NOT_FOUND */ }

            if (profile == null) missing.Add(exe);
            else found ??= profile;
        }
        return found;
    }

    private static DriverSettingsProfile? TryFindProfileByName(DriverSettingsSession session, string name)
    {
        try { return session.FindProfileByName(name); }
        catch { return null; }
    }

    /// <summary>Effective DWORD value of a setting for this profile (may be inherited from the global/base profile).</summary>
    private static bool TryReadDword(DriverSettingsProfile profile, uint id, out uint value, out bool inThisProfile)
    {
        value = 0;
        inThisProfile = false;
        try
        {
            var setting = profile.GetSetting(id);
            if (setting == null || setting.SettingType != DRSSettingType.Integer) return false;
            value = Convert.ToUInt32(setting.CurrentValue, CultureInfo.InvariantCulture);
            inThisProfile = setting.SettingLocation == DRSSettingLocation.CurrentProfile;
            return true;
        }
        catch
        {
            return false; // NVAPI_SETTING_NOT_FOUND: not set anywhere
        }
    }

    private static string FormatKnown(Type? valueType, uint value)
    {
        if (valueType is { IsEnum: true })
        {
            try
            {
                var boxed = Enum.ToObject(valueType, value);
                if (Enum.IsDefined(valueType, boxed)) return Enum.GetName(valueType, boxed) ?? FormatNumber(value);
            }
            catch
            {
                // fall through
            }
        }
        return FormatNumber(value);
    }

    private static string FormatNumber(uint value) =>
        value < 0x10000 ? value.ToString(CultureInfo.InvariantCulture) : $"0x{value:X8}";

    private static string NameFor(Dictionary<string, uint> map, uint value) =>
        map.FirstOrDefault(kv => kv.Value == value).Key ?? FormatNumber(value);

    private static bool IsOff(string v) =>
        v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
        v == "0";

    private static bool TryParseUInt(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
