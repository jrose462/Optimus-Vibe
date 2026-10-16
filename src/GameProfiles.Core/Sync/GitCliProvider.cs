using System.Diagnostics;
using System.Text;
using GameProfiles.Engine;
using GameProfiles.Logging;

namespace GameProfiles.Sync;

/// <summary>The "git" provider: the config folder is inside a git checkout synced with the git CLI.</summary>
public sealed class GitCliProvider(string repoRoot) : ISyncProvider
{
    private readonly GitSync _git = new(repoRoot);

    public string Name => SyncProviders.Git;

    public string RepoRoot => repoRoot;

    public string Describe() => $"git checkout at {repoRoot}";

    public bool IsReady(out string reason)
    {
        if (!_git.IsRepo)
        {
            reason = $"{repoRoot} is not a git repository";
            return false;
        }
        reason = "";
        return true;
    }

    public GitResult Pull()
    {
        if (!IsReady(out var why)) return new(false, why);
        if (!_git.HasRemote) return new(true, "no remote configured — nothing to pull");
        var (code, output) = RunGit("pull", "--rebase", "--autostash");
        Log.Info($"git pull: exit {code}");
        return code == 0 ? new(true, output.Trim().Length > 0 ? output.Trim() : "pulled") : new(false, "pull failed: " + output.Trim());
    }

    public GitResult CommitAndSync(string message, params string[] paths) => _git.CommitAndSync(message, paths);

    private (int Code, string Output) RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var output = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(TimeSpan.FromSeconds(90)))
            {
                p.Kill(entireProcessTree: true);
                return (-1, $"git {args[0]} timed out");
            }
            p.WaitForExit();
            return (p.ExitCode, output.ToString());
        }
        catch (Exception ex)
        {
            return (-1, $"git not available: {ex.Message}");
        }
    }
}
