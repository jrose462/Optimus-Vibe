using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace OptimusVibe.Sync;

/// <summary>Base for GitHub sign-in / API failures; the message is meant for the user (never contains the token).</summary>
public class GitHubException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Device-flow sign-in failed: denied, expired, device flow disabled on the OAuth App, bad client id...</summary>
public sealed class GitHubAuthException(string message, string? error = null) : GitHubException(message)
{
    /// <summary>GitHub's error code (access_denied, expired_token, ...), if any.</summary>
    public string? Error { get; } = error;
}

/// <summary>GitHub OAuth device flow (https://docs.github.com/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow).</summary>
public static class GitHubAuth
{
    /// <summary>Scopes requested: "repo" (private config repos need it).</summary>
    public const string Scopes = "repo";

    /// <summary>Windows Credential Manager target holding the token.</summary>
    public const string CredentialTarget = "OptimusVibe:github";

    /// <summary>Target used before the rename to Optimus-Vibe; read as a fallback and cleared on sign-out.</summary>
    public const string LegacyCredentialTarget = "GameProfiles:github";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string TokenUrl = "https://github.com/login/oauth/access_token";
    private const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly HttpClient Http = CreateHttp(new SocketsHttpHandler());

    internal static HttpClient CreateHttp(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Optimus-Vibe");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static Task<DeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken ct = default) =>
        StartDeviceFlowAsync(Http, clientId, ct);

    internal static async Task<DeviceCode> StartDeviceFlowAsync(HttpClient http, string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new GitHubAuthException("No GitHub OAuth App client id configured.");
        var json = await PostFormAsync(http, DeviceCodeUrl, new() { ["client_id"] = clientId.Trim(), ["scope"] = Scopes }, ct);
        if (json["error"]?.GetValue<string>() is { } error)
            throw new GitHubAuthException(error switch
            {
                "device_flow_disabled" => "Device flow is not enabled for this OAuth App (GitHub → Settings → Developer settings → the app → Enable Device Flow).",
                "incorrect_client_credentials" => "GitHub doesn't recognise that OAuth App client id.",
                _ => $"GitHub sign-in failed: {Describe(json, error)}",
            }, error);

        var device = json["device_code"]?.GetValue<string>();
        var user = json["user_code"]?.GetValue<string>();
        var uri = json["verification_uri"]?.GetValue<string>();
        if (device is null || user is null || uri is null)
            throw new GitHubAuthException("GitHub returned an unexpected device-code response.");
        return new DeviceCode(user, uri, Int(json["expires_in"], 900), Int(json["interval"], 5), device);
    }

    /// <summary>Poll until the user approves (returns the token), denies/expires (throws), or ct is cancelled.</summary>
    public static Task<string> PollForTokenAsync(string clientId, DeviceCode code, CancellationToken ct = default) =>
        PollForTokenAsync(Http, clientId, code, Task.Delay, ct);

    internal static async Task<string> PollForTokenAsync(HttpClient http, string clientId, DeviceCode code,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        var interval = Math.Max(1, code.IntervalSeconds);
        var waited = 0;
        while (true)
        {
            if (waited >= code.ExpiresInSeconds)
                throw new GitHubAuthException("The sign-in code expired before it was entered. Start sign-in again.", "expired_token");
            await delay(TimeSpan.FromSeconds(interval), ct);
            waited += interval;
            ct.ThrowIfCancellationRequested();

            var json = await PostFormAsync(http, TokenUrl, new()
            {
                ["client_id"] = clientId.Trim(),
                ["device_code"] = code.Code,
                ["grant_type"] = GrantType,
            }, ct);

            if (json["access_token"]?.GetValue<string>() is { Length: > 0 } token) return token;

            var error = json["error"]?.GetValue<string>();
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval = json["interval"] is { } i ? Math.Max(Int(i, interval + 5), interval + 5) : interval + 5;
                    continue;
                case "expired_token":
                    throw new GitHubAuthException("The sign-in code expired before it was entered. Start sign-in again.", error);
                case "access_denied":
                    throw new GitHubAuthException("Sign-in was cancelled on GitHub (access denied).", error);
                case null:
                    throw new GitHubAuthException("GitHub returned an unexpected token response.");
                default:
                    throw new GitHubAuthException($"GitHub sign-in failed: {Describe(json, error)}", error);
            }
        }
    }

    private static async Task<JsonNode> PostFormAsync(HttpClient http, string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync(url, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { }
        if (json is null)
            throw new GitHubAuthException($"GitHub sign-in failed (HTTP {(int)resp.StatusCode}).");
        if (!resp.IsSuccessStatusCode && json["error"] is null)
            throw new GitHubAuthException($"GitHub sign-in failed (HTTP {(int)resp.StatusCode}): {json["message"]?.GetValue<string>()}");
        return json;
    }

    private static string Describe(JsonNode json, string error) =>
        json["error_description"]?.GetValue<string>() is { Length: > 0 } d ? $"{d} ({error})" : error;

    private static int Int(JsonNode? node, int fallback)
    {
        try { return node?.GetValue<int>() ?? fallback; }
        catch (Exception) { return int.TryParse(node?.ToString(), out var v) ? v : fallback; }
    }

    // ------------------------------------------------------------ token storage (Windows Credential Manager)

    /// <summary>Token from Windows Credential Manager ("OptimusVibe:github", else the legacy "GameProfiles:github"), or null.</summary>
    public static string? LoadToken() => LoadToken(CredentialTarget) ?? LoadToken(LegacyCredentialTarget);
    public static void SaveToken(string token) => SaveToken(token, CredentialTarget);
    public static void ClearToken() { ClearToken(CredentialTarget); ClearToken(LegacyCredentialTarget); }

    internal static string? LoadToken(string target)
    {
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err, "Could not read the GitHub token from Windows Credential Manager");
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var token = Encoding.UTF8.GetString(bytes).Trim();
            return token.Length == 0 ? null : token;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    internal static void SaveToken(string token, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni("Optimus-Vibe");
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWriteW(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the GitHub token to Windows Credential Manager");
        }
        finally
        {
            // Wipe the plaintext copy before freeing.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    internal static void ClearToken(string target)
    {
        if (CredDeleteW(target, CRED_TYPE_GENERIC, 0)) return;
        var err = Marshal.GetLastWin32Error();
        if (err != ERROR_NOT_FOUND)
            throw new Win32Exception(err, "Could not delete the GitHub token from Windows Credential Manager");
    }

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
