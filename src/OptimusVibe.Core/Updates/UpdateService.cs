using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.Updates;

/// <summary>An update check/download/apply failed; the message is user-facing.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : GitHubException(message, inner);

/// <summary>One downloadable file of a release.</summary>
/// <param name="ApiUrl">REST asset URL (api.github.com/.../releases/assets/{id}); downloaded with Accept: application/octet-stream so private assets work.</param>
/// <param name="Sha256">Expected SHA-256 (lowercase hex) when the release notes list it, else null.</param>
/// <param name="Sha256ApiUrl">API URL of a "&lt;name&gt;.sha256" companion asset, if the release has one.</param>
public sealed record ReleaseAsset(string Name, long Size, string ApiUrl, string BrowserUrl, string? Sha256, string? Sha256ApiUrl);

/// <summary>The latest published release of the app repo.</summary>
public sealed record UpdateInfo(
    string Tag,
    string Version,
    string CurrentVersion,
    string Name,
    string Notes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The release is newer than the running build.</summary>
    public bool IsNewer => AppVersion.IsNewer(Version, CurrentVersion);

    public ReleaseAsset? App => Find(AppRepo.AppAsset);
    public ReleaseAsset? Cli => Find(AppRepo.CliAsset);

    public ReleaseAsset? Find(string assetName) =>
        Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase));
}

public enum UpdateStatus
{
    /// <summary>A newer release exists (<see cref="UpdateCheckResult.Update"/> is set).</summary>
    UpdateAvailable,
    /// <summary>The running build is the latest (or newer); Update is set.</summary>
    UpToDate,
    /// <summary>Not signed in (or the sign-in was rejected) and the app repo is private.</summary>
    SignInRequired,
    /// <summary>Signed in, but the account can't see the app repo.</summary>
    NoAccess,
    /// <summary>The repo has no published (non-draft, non-prerelease) release yet.</summary>
    NoReleases,
    /// <summary>Network / rate limit / unexpected response; see Message.</summary>
    Failed,
}

/// <summary>Outcome of <see cref="UpdateService.CheckAsync(string?, CancellationToken)"/>. Never an exception for expected cases.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, string Message, UpdateInfo? Update)
{
    public bool UpdateAvailable => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>What <see cref="UpdateService.StageAllAsync"/> downloaded.</summary>
/// <param name="Staged">Target exe paths that now have a verified "&lt;exe&gt;.new" next to them.</param>
/// <param name="Notes">Human-readable notes about anything skipped (sibling missing from the release, app running...).</param>
public sealed record StageResult(IReadOnlyList<string> Staged, IReadOnlyList<string> Notes);

/// <summary>
/// Self-update from GitHub Releases of the APP repo (<see cref="AppRepo"/>): check → download + verify into
/// "&lt;exe&gt;.new" → swap (running exe renamed to "&lt;exe&gt;.old", which Windows allows) → restart → delete .old on the
/// next start (<see cref="CleanupOld"/>).
/// </summary>
public static partial class UpdateService
{
    public const string StagedSuffix = ".new";
    public const string OldSuffix = ".old";
    private const string PartSuffix = ".new.part";
    private const string ApiBase = "https://api.github.com/";

    // Redirects are followed by hand so the Authorization header never leaks to the asset CDN.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // ------------------------------------------------------------ check

    /// <summary>Latest non-draft, non-prerelease release of the app repo vs <see cref="AppVersion.Current"/>.</summary>
    /// <param name="token">The signed-in GitHub token (needed while the app repo is private), or null.</param>
    public static Task<UpdateCheckResult> CheckAsync(string? token, CancellationToken ct = default) =>
        CheckAsync(SharedHandler, token, AppVersion.Current, ct);

    internal static async Task<UpdateCheckResult> CheckAsync(HttpMessageHandler handler, string? token, string currentVersion, CancellationToken ct)
    {
        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        using var http = CreateHttp(handler);
        try
        {
            using var resp = await SendAsync(http, $"{ApiBase}repos/{AppRepo.FullName}/releases/latest", token, json: true, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (token is null)
                    return new(UpdateStatus.SignInRequired,
                        "Sign in to GitHub to check for updates (the app repo is private).", null);
                // With a token: either no release yet, or no access to the repo.
                using var repo = await SendAsync(http, $"{ApiBase}repos/{AppRepo.FullName}", token, json: true, ct).ConfigureAwait(false);
                return repo.IsSuccessStatusCode
                    ? new(UpdateStatus.NoReleases, "No app release has been published yet.", null)
                    : new(UpdateStatus.NoAccess,
                        $"This GitHub account can't see {AppRepo.FullName} (private) — ask the owner for access to get updates.", null);
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return new(UpdateStatus.SignInRequired,
                    "GitHub rejected the saved sign-in (token revoked or expired). Sign in again to check for updates.", null);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await GitHubApi.ErrorAsync(resp, AppRepo.FullName, ct).ConfigureAwait(false);
                return new(UpdateStatus.Failed, "Update check failed: " + err.Message, null);
            }

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                       ?? throw new UpdateException("GitHub returned an empty release.");
            var info = ParseRelease(json, currentVersion);
            if (info is null)
                return new(UpdateStatus.NoReleases, "The latest release has no version tag (expected vX.Y.Z).", null);
            return info.IsNewer
                ? new(UpdateStatus.UpdateAvailable, $"Version {info.Version} is available (you have {currentVersion}).", info)
                : new(UpdateStatus.UpToDate, $"You have the latest version ({currentVersion}).", info);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is GitHubException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            Log.Warn($"update check failed: {ex.Message}");
            return new(UpdateStatus.Failed, ex switch
            {
                HttpRequestException => $"Can't reach GitHub: {ex.Message}",
                OperationCanceledException => "GitHub didn't respond in time.",
                System.Text.Json.JsonException => "GitHub returned a release that isn't valid JSON.",
                _ => ex.Message,
            }, null);
        }
    }

    internal static UpdateInfo? ParseRelease(JsonNode json, string currentVersion)
    {
        var tag = json["tag_name"]?.GetValue<string>() ?? "";
        if (!AppVersion.TryParse(tag, out var version)) return null;
        var notes = json["body"]?.GetValue<string?>() ?? "";
        var raw = new List<(string Name, long Size, string Url, string Browser)>();
        foreach (var a in json["assets"]?.AsArray() ?? [])
        {
            if (a is null) continue;
            raw.Add((a["name"]?.GetValue<string>() ?? "", a["size"]?.GetValue<long>() ?? 0,
                a["url"]?.GetValue<string>() ?? "", a["browser_download_url"]?.GetValue<string>() ?? ""));
        }
        var assets = raw
            .Where(a => !a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            .Select(a => new ReleaseAsset(a.Name, a.Size, a.Url, a.Browser, ShaFromNotes(notes, a.Name),
                raw.FirstOrDefault(s => s.Name.Equals(a.Name + ".sha256", StringComparison.OrdinalIgnoreCase)).Url is { Length: > 0 } u ? u : null))
            .ToList();
        DateTimeOffset? published = DateTimeOffset.TryParse(json["published_at"]?.GetValue<string?>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        return new UpdateInfo(tag, version.ToString(), AppVersion.StripBuild(currentVersion),
            json["name"]?.GetValue<string?>() is { Length: > 0 } n ? n : tag, notes,
            json["html_url"]?.GetValue<string>() ?? AppRepo.ReleasesUrl, published, assets);
    }

    /// <summary>A 64-hex SHA-256 on a release-notes line that mentions the asset name (sha256sum / "name: hash" / table row).</summary>
    internal static string? ShaFromNotes(string notes, string assetName)
    {
        foreach (var line in notes.Split('\n'))
        {
            if (line.IndexOf(assetName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            // Don't let "OptimusVibe.exe" match a line about "optimus-vibe.exe" or vice versa: require the name as a token.
            if (!Regex.IsMatch(line, @"(?<![A-Za-z0-9_.-])" + Regex.Escape(assetName) + @"(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase)) continue;
            var m = Sha256Hex().Match(line);
            if (m.Success) return m.Value.ToLowerInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")]
    private static partial Regex Sha256Hex();

    // ------------------------------------------------------------ download

    /// <summary>
    /// Downloads the release asset named like <paramref name="targetExe"/> (or <paramref name="assetName"/>) to
    /// "&lt;targetExe&gt;.new", verifying the size and, when the release provides one, the SHA-256. Returns the .new path.
    /// </summary>
    public static Task<string> DownloadAndStageAsync(UpdateInfo update, string targetExe, IProgress<double>? progress, string? token,
        CancellationToken ct = default, string? assetName = null) =>
        DownloadAndStageAsync(SharedHandler, update, targetExe, progress, token, ct, assetName);

    internal static async Task<string> DownloadAndStageAsync(HttpMessageHandler handler, UpdateInfo update, string targetExe,
        IProgress<double>? progress, string? token, CancellationToken ct, string? assetName = null)
    {
        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        targetExe = Path.GetFullPath(targetExe);
        var name = assetName ?? Path.GetFileName(targetExe);
        var asset = update.Find(name)
                    ?? throw new UpdateException($"Release {update.Tag} has no {name} to download.");
        if (string.IsNullOrEmpty(asset.ApiUrl)) throw new UpdateException($"Release {update.Tag}: {name} has no download URL.");

        using var http = CreateHttp(handler);
        var expectedSha = asset.Sha256;
        if (expectedSha is null && asset.Sha256ApiUrl is not null)
        {
            using var shaResp = await DownloadAsync(http, asset.Sha256ApiUrl, token, ct).ConfigureAwait(false);
            var text = await shaResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            expectedSha = Sha256Hex().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant()
                : throw new UpdateException($"{name}.sha256 in release {update.Tag} doesn't contain a SHA-256.");
        }

        var part = targetExe + PartSuffix;
        var staged = targetExe + StagedSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(targetExe)!);
        try
        {
            using var resp = await DownloadAsync(http, asset.ApiUrl, token, ct).ConfigureAwait(false);
            var total = asset.Size > 0 ? asset.Size : resp.Content.Headers.ContentLength ?? 0;
            long written = 0;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    written += read;
                    if (total > 0) progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (asset.Size > 0 && written != asset.Size)
                throw new UpdateException($"Download of {name} is incomplete ({written:N0} of {asset.Size:N0} bytes). Try again.");
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha is not null && actual != expectedSha)
                throw new UpdateException($"Downloaded {name} doesn't match the release's SHA-256 (expected {expectedSha[..12]}…, got {actual[..12]}…). Not installed.");

            File.Move(part, staged, overwrite: true);
            progress?.Report(1.0);
            Log.Info($"update: staged {name} {update.Version} ({written:N0} bytes{(expectedSha is null ? "" : ", sha256 ok")}) at {staged}");
            return staged;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>
    /// Stages <paramref name="runningExe"/> and, when <paramref name="includeSibling"/>, the other exe of the pair
    /// (OptimusVibe.exe ↔ optimus-vibe.exe) if it sits in the same folder and the release has it. The app sibling is
    /// skipped (with a note) while OptimusVibe is running.
    /// </summary>
    public static Task<StageResult> StageAllAsync(UpdateInfo update, string runningExe, IProgress<double>? progress, string? token,
        CancellationToken ct = default, bool includeSibling = true) =>
        StageAllAsync(SharedHandler, update, runningExe, progress, token, ct, includeSibling, IsRunning);

    internal static async Task<StageResult> StageAllAsync(HttpMessageHandler handler, UpdateInfo update, string runningExe,
        IProgress<double>? progress, string? token, CancellationToken ct, bool includeSibling, Func<string, bool> isRunning)
    {
        runningExe = Path.GetFullPath(runningExe);
        var targets = new List<(string Exe, ReleaseAsset Asset)>();
        var notes = new List<string>();
        var self = update.Find(Path.GetFileName(runningExe))
                   ?? throw new UpdateException($"Release {update.Tag} has no {Path.GetFileName(runningExe)}.");
        targets.Add((runningExe, self));

        if (includeSibling && SiblingOf(runningExe) is { } sibling && File.Exists(sibling))
        {
            var siblingName = Path.GetFileName(sibling);
            if (update.Find(siblingName) is not { } asset) notes.Add($"Release {update.Tag} has no {siblingName}; left it as is.");
            else if (siblingName.Equals(AppRepo.AppAsset, StringComparison.OrdinalIgnoreCase) && isRunning(sibling))
                notes.Add($"{siblingName} is running; close it and update it from the app (or run update-app again).");
            else targets.Add((sibling, asset));
        }

        var totalBytes = Math.Max(1, targets.Sum(t => Math.Max(1, t.Asset.Size)));
        long done = 0;
        var staged = new List<string>();
        foreach (var (exe, asset) in targets)
        {
            var offset = done;
            var size = Math.Max(1, asset.Size);
            var sub = progress is null ? null : new SyncProgress(p => progress.Report((offset + p * size) / totalBytes));
            await DownloadAndStageAsync(handler, update, exe, sub, token, ct, asset.Name).ConfigureAwait(false);
            staged.Add(exe);
            done += size;
        }
        return new StageResult(staged, notes);
    }

    // ------------------------------------------------------------ apply

    /// <summary>
    /// Swaps every staged "&lt;exe&gt;.new" in place for <paramref name="exePath"/> and its sibling exe (running exe →
    /// "&lt;exe&gt;.old"). Returns the exe paths that were replaced; throws when nothing is staged for exePath.
    /// </summary>
    public static IReadOnlyList<string> ApplyStaged(string exePath)
    {
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath + StagedSuffix))
            throw new UpdateException($"No downloaded update for {Path.GetFileName(exePath)} — download it first.");
        var replaced = new List<string> { SwapIn(exePath) };
        if (SiblingOf(exePath) is { } sibling && File.Exists(sibling + StagedSuffix))
        {
            try { replaced.Add(SwapIn(sibling)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"update: couldn't replace {sibling}: {ex.Message}");
            }
        }
        return replaced;
    }

    /// <summary>
    /// <see cref="ApplyStaged"/>, then starts the new <paramref name="exePath"/> with <paramref name="restartArgs"/>.
    /// The caller exits right after (the old process keeps running from the renamed .old file until then).
    /// </summary>
    public static void ApplyStagedAndRestart(string exePath, string[] restartArgs) =>
        ApplyStagedAndRestart(exePath, restartArgs, psi => Process.Start(psi));

    internal static void ApplyStagedAndRestart(string exePath, string[] restartArgs, Func<ProcessStartInfo, Process?> start)
    {
        exePath = Path.GetFullPath(exePath);
        ApplyStaged(exePath);
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        foreach (var a in restartArgs ?? []) psi.ArgumentList.Add(a);
        try
        {
            start(psi);
            Log.Info($"update: restarted {exePath}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new UpdateException($"The update is installed, but {Path.GetFileName(exePath)} couldn't be restarted: {ex.Message}. Start it again by hand.", ex);
        }
    }

    /// <summary>Deletes leftovers of a previous update (".old" copies, partial downloads) for the exe and its sibling. Never throws.</summary>
    public static void CleanupOld(string exePath)
    {
        try
        {
            exePath = Path.GetFullPath(exePath);
            var dir = Path.GetDirectoryName(exePath)!;
            if (!Directory.Exists(dir)) return;
            foreach (var exe in new[] { exePath, SiblingOf(exePath) })
            {
                if (exe is null) continue;
                var name = Path.GetFileName(exe);
                foreach (var f in Directory.EnumerateFiles(dir, name + OldSuffix + "*")) TryDelete(f);
                TryDelete(exe + PartSuffix);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"update cleanup: {ex.Message}");
        }
    }

    /// <summary>The other exe of the pair in the same folder (path only; may not exist), or null for an unknown exe name.</summary>
    public static string? SiblingOf(string exePath)
    {
        var name = Path.GetFileName(exePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        if (name.Equals(AppRepo.AppAsset, StringComparison.OrdinalIgnoreCase)) return Path.Combine(dir, AppRepo.CliAsset);
        if (name.Equals(AppRepo.CliAsset, StringComparison.OrdinalIgnoreCase)) return Path.Combine(dir, AppRepo.AppAsset);
        return null;
    }

    /// <summary>True when a process (other than this one) is running from that exe (matched by name, then path when readable).</summary>
    public static bool IsRunning(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(full)))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    var file = p.MainModule?.FileName;
                    if (file is null || string.Equals(Path.GetFullPath(file), full, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception)
                {
                    return true; // can't read its path (elevated?) — assume it's ours
                }
            }
        }
        return false;
    }

    /// <summary>Running exe → .old (allowed on Windows), .new → exe; rolls back if the second move fails.</summary>
    private static string SwapIn(string exe)
    {
        var staged = exe + StagedSuffix;
        var old = exe + OldSuffix;
        if (File.Exists(old))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                old = exe + OldSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture); // previous .old still in use
            }
        }
        var hadExe = File.Exists(exe);
        if (hadExe) File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            if (hadExe && !File.Exists(exe)) File.Move(old, exe);
            throw;
        }
        Log.Info($"update: replaced {exe}");
        return exe;
    }

    // ------------------------------------------------------------ http

    private static HttpClient CreateHttp(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Optimus-Vibe/" + AppVersion.StripBuild(AppVersion.Current));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string url, string? token, bool json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(json ? "application/vnd.github+json" : "application/octet-stream"));
        if (token is not null && IsGitHubApi(req.RequestUri!)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req, json ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>GET an asset as bytes, following GitHub's redirect to its CDN by hand (token only ever sent to api.github.com).</summary>
    private static async Task<HttpResponseMessage> DownloadAsync(HttpClient http, string url, string? token, CancellationToken ct)
    {
        var current = new Uri(url);
        for (var hop = 0; hop < 6; hop++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await SendAsync(http, current.ToString(), token, json: false, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException($"Can't reach GitHub: {ex.Message}", ex);
            }
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                resp.Dispose();
                continue;
            }
            if (resp.IsSuccessStatusCode) return resp;
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound && token is null)
                    throw new UpdateException("Sign in to GitHub to download the update (the app repo is private).");
                throw await GitHubApi.ErrorAsync(resp, AppRepo.FullName, ct).ConfigureAwait(false);
            }
        }
        throw new UpdateException("Too many redirects downloading the update.");
    }

    private static bool IsGitHubApi(Uri uri) => uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception) { /* in use; next cleanup */ }
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts to a sync context, which reorders reports).</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
