using System.Diagnostics;
using GameProfiles.Engine;

namespace GameProfiles.Tests;

/// <summary>GitSync against a throwaway `git init` repo. Each test passes trivially when git is not on PATH.</summary>
public sealed class GitSyncTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public static bool GitAvailable => Available.Value;

    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result;
    }

    /// <summary>`git init` with a local identity and no signing so commits work on any machine.</summary>
    public static void InitRepo(string dir)
    {
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q");
        Git(dir, "config", "user.name", "GameProfiles Tests");
        Git(dir, "config", "user.email", "tests@example.invalid");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "config", "core.autocrlf", "false");
    }

    private string NewRepo()
    {
        var dir = _tmp.Dir("repo");
        InitRepo(dir);
        return dir;
    }

    [Fact]
    public void NotARepo()
    {
        var git = new GitSync(_tmp.Dir("plain"));
        Assert.False(git.IsRepo);
        Assert.False(git.HasRemote);
        Assert.False(git.Commit("x").Ok);
        Assert.False(git.Sync().Ok);
    }

    [Fact]
    public void Commit_NothingToCommit()
    {
        if (!GitAvailable) return;
        var git = new GitSync(NewRepo());
        Assert.True(git.IsRepo);
        var r = git.Commit("empty");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("nothing to commit", r.Message);
    }

    [Fact]
    public void Commit_NewFile_ThenNothingToCommit()
    {
        if (!GitAvailable) return;
        var dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        var git = new GitSync(dir);

        var r = git.Commit("feat: add a", "a.txt");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("committed", r.Message);
        Assert.Equal("feat: add a", Git(dir, "log", "-1", "--format=%s").Trim());

        var again = git.Commit("no-op", "a.txt");
        Assert.Equal("nothing to commit", again.Message);
    }

    [Fact]
    public void Commit_OnlyStagesGivenPaths()
    {
        if (!GitAvailable) return;
        var dir = NewRepo();
        Directory.CreateDirectory(Path.Combine(dir, "saves"));
        File.WriteAllText(Path.Combine(dir, "saves", "s.yaml"), "x");
        File.WriteAllText(Path.Combine(dir, "other.txt"), "y");
        var r = new GitSync(dir).Commit("saves only", Path.Combine(dir, "saves"));
        Assert.True(r.Ok, r.Message);
        var files = Git(dir, "show", "--name-only", "--format=", "HEAD");
        Assert.Contains("saves/s.yaml", files);
        Assert.DoesNotContain("other.txt", files);
        Assert.Contains("?? other.txt", Git(dir, "status", "--porcelain"));
    }

    [Fact]
    public void Commit_StagesDeletions()
    {
        if (!GitAvailable) return;
        var dir = NewRepo();
        var file = Path.Combine(dir, "gone.txt");
        File.WriteAllText(file, "x");
        var git = new GitSync(dir);
        git.Commit("add", "gone.txt");
        File.Delete(file);
        var r = git.Commit("delete", "gone.txt");
        Assert.Equal("committed", r.Message);
        Assert.Equal("", Git(dir, "ls-files").Trim());
    }

    [Fact]
    public void Sync_NoRemote_IsOk()
    {
        if (!GitAvailable) return;
        var git = new GitSync(NewRepo());
        Assert.False(git.HasRemote);
        var r = git.Sync();
        Assert.True(r.Ok);
        Assert.Contains("no remote", r.Message);
    }

    [Fact]
    public void CommitAndSync_NoRemote()
    {
        if (!GitAvailable) return;
        var dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        var r = new GitSync(dir).CommitAndSync("msg", "a.txt");
        Assert.True(r.Ok);
        Assert.Contains("no remote", r.Message);
        Assert.Equal("msg", Git(dir, "log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public void Sync_WithBareRemote_Pushes()
    {
        if (!GitAvailable) return;
        var remote = _tmp.Dir("remote.git");
        Git(remote, "init", "-q", "--bare");
        var dir = NewRepo();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        var git = new GitSync(dir);
        git.Commit("first", "a.txt");
        var branch = Git(dir, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Git(dir, "remote", "add", "origin", remote);
        Git(dir, "push", "-q", "-u", "origin", branch);

        File.WriteAllText(Path.Combine(dir, "a.txt"), "2");
        var r = git.CommitAndSync("second", "a.txt");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("synced", r.Message);
        Assert.Equal("second", Git(remote, "log", "-1", "--format=%s", branch).Trim());
    }
}
