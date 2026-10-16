using System.Diagnostics;
using System.Text;
using GameProfiles.Logging;

namespace GameProfiles.Engine;

public sealed record GitResult(bool Ok, string Message);

/// <summary>Thin wrapper over the git CLI for syncing the config repo (manual saves) between machines.</summary>
public sealed class GitSync(string repoRoot)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public bool IsRepo => Directory.Exists(Path.Combine(repoRoot, ".git"));

    public bool HasRemote => IsRepo && Run("remote").Output.Trim().Length > 0;

    /// <summary>Stage the given paths (relative or absolute) and commit them if anything changed.</summary>
    public GitResult Commit(string message, params string[] paths)
    {
        if (!IsRepo) return new(false, $"{repoRoot} is not a git repository");
        var add = Run(["add", "-A", "--", .. paths]);
        if (add.Code != 0) return new(false, add.Output);
        if (Run("diff", "--cached", "--quiet").Code == 0) return new(true, "nothing to commit");
        var commit = Run("commit", "-m", message);
        return new(commit.Code == 0, commit.Code == 0 ? "committed" : commit.Output);
    }

    /// <summary>Pull (rebase, autostash) then push. Safe to call when there is no remote.</summary>
    public GitResult Sync()
    {
        if (!IsRepo) return new(false, $"{repoRoot} is not a git repository");
        if (!HasRemote) return new(true, "no remote configured — saved locally only");
        var pull = Run("pull", "--rebase", "--autostash");
        if (pull.Code != 0) return new(false, "pull failed: " + pull.Output.Trim());
        var push = Run("push");
        return push.Code == 0 ? new(true, "synced") : new(false, "push failed: " + push.Output.Trim());
    }

    public GitResult CommitAndSync(string message, params string[] paths)
    {
        var commit = Commit(message, paths);
        if (!commit.Ok) return commit;
        var sync = Sync();
        Log.Info($"git: {commit.Message}; {sync.Message}");
        return sync;
    }

    private (int Code, string Output) Run(params string[] args)
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
            if (!p.WaitForExit(Timeout))
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
