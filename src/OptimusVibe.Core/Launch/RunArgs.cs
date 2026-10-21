namespace OptimusVibe.Launch;

/// <summary>
/// The command line of the headless wrapper: <c>run &lt;game&gt; [--preset p] [--profile d] [--config dir] [--no-prompt] [--no-big-picture]
/// [--verify] [--launch &lt;uri-or-exe&gt; [--launch-arg a]... [--install-dir d]] [-- %command%]</c>. args[0] is the verb and is skipped.
/// </summary>
public sealed class RunArgs
{
    public string? Game { get; private set; }
    public string? Preset { get; private set; }
    public string? Profile { get; private set; }
    public string? Config { get; private set; }
    public string? Launch { get; private set; }
    public string? InstallDir { get; private set; }
    public List<string> LaunchArgs { get; } = [];
    public bool Prompt { get; private set; } = true;
    public bool NoBigPicture { get; private set; }

    /// <summary>A test of the preset: nothing is asked afterwards, the result is stored as a verify run.</summary>
    public bool Verify { get; private set; }

    /// <summary>The game command line after "--" (Steam's %command%), or stray arguments.</summary>
    public List<string> Command { get; } = [];

    public static RunArgs Parse(string[] args)
    {
        var r = new RunArgs();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--")
            {
                r.Command.AddRange(args.Skip(i + 1));
                break;
            }
            switch (a.ToLowerInvariant())
            {
                case "--preset" when i + 1 < args.Length: r.Preset = args[++i]; break;
                case "--profile" when i + 1 < args.Length: r.Profile = args[++i]; break;
                case "--config" when i + 1 < args.Length: r.Config = args[++i]; break;
                case "--no-prompt": r.Prompt = false; break;
                case "--no-big-picture": r.NoBigPicture = true; break;
                case "--verify": r.Verify = true; break;
                case "--launch" when i + 1 < args.Length: r.Launch = args[++i]; break;           // app library: uri or exe when no %command%
                case "--launch-arg" when i + 1 < args.Length: r.LaunchArgs.Add(args[++i]); break;
                case "--install-dir" when i + 1 < args.Length: r.InstallDir = args[++i]; break; // to wait on a URI-launched game
                default:
                    if (r.Game is null && !a.StartsWith("--")) r.Game = a;
                    else r.Command.Add(a); // tolerate a missing "--" before the game command line
                    break;
            }
        }
        return r;
    }

    /// <summary>The run options these arguments describe.</summary>
    public RunOptions ToOptions() => new() { Launch = Launch, LaunchArgs = LaunchArgs, InstallDir = InstallDir, Verify = Verify };
}
