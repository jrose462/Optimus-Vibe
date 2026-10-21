using OptimusVibe.GameLab;
using OptimusVibe.Logging;

namespace OptimusVibe.App.ViewModels;

public enum MapPhase
{
    /// <summary>Instructions for launch 1 (change nothing).</summary>
    Intro1,

    /// <summary>The game is running (the window is out of the way).</summary>
    Running1,

    /// <summary>Instructions for launch 2 (change many options).</summary>
    Intro2,
    Running2,

    /// <summary>What the two launches found, with Send / Keep.</summary>
    Summary,

    /// <summary>The summary was sent to the game lab.</summary>
    Sent,

    /// <summary>The game could not be started.</summary>
    Failed,
}

/// <summary>What the "Map this game" window can do; the lab panel supplies the real thing, debug scenes a fake.</summary>
public interface IMapGameActions
{
    /// <summary>Runs one launch (1 = change nothing, 2 = change many options): snapshot, launch, wait, snapshot. Null when the game could not be started.</summary>
    Task<ProbeDiff?> RunPassAsync(int pass);

    /// <summary>The evidence from both launches; also remembered so later requests carry it.</summary>
    ProbeEvidence BuildEvidence();

    /// <summary>The summary can be sent (the game lab is set up and the game can be described).</summary>
    bool CanSend { get; }

    /// <summary>Files a request that carries the mapping. Returns a one-line result, or null when nothing was sent.</summary>
    Task<string?> SendAsync();

    /// <summary>Keeps the settings the game now has as a manual save (only offered when the game has a config). Returns a one-line result, or null on failure.</summary>
    Task<string?> KeepAsync();

    bool CanKeep { get; }

    /// <summary>Called when the game starts / has exited, so the app can step aside and come back.</summary>
    void GameStarting();
    void GameExited();
}

/// <summary>
/// The "Map this game" step card: launch 1 (change nothing), launch 2 (change many graphics options), then a summary with Send to
/// game lab / Keep my changes as a manual save. A plain model; <see cref="Views.MapGameWindow"/> shows it full screen and maps A, Y, B to
/// <see cref="Primary"/>, <see cref="Secondary"/> and <see cref="Back"/>.
/// </summary>
public sealed class MapGameModel : ObservableObject
{
    private readonly IMapGameActions _actions;
    private MapPhase _phase = MapPhase.Intro1;
    private ProbeDiff? _passA;
    private ProbeEvidence? _evidence;
    private string _message = "";
    private bool _busy;
    private bool _kept;

    public MapGameModel(string gameName, IMapGameActions actions)
    {
        GameName = gameName;
        _actions = actions;
    }

    public string GameName { get; }

    /// <summary>The window hides while the game runs and comes back (and takes the foreground) afterwards.</summary>
    public event Action? HideRequested;
    public event Action? ShowRequested;
    public event Action? CloseRequested;

    public MapPhase Phase
    {
        get => _phase;
        private set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(string.Empty);
        }
    }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) OnPropertyChanged(string.Empty); } }

    // ---------------------------------------------------------------- words

    public string StepText => Phase switch
    {
        MapPhase.Intro1 or MapPhase.Running1 => "Launch 1 of 2",
        MapPhase.Intro2 or MapPhase.Running2 => "Launch 2 of 2",
        MapPhase.Summary or MapPhase.Sent => "Done",
        _ => "",
    };

    public string Title => Phase switch
    {
        MapPhase.Intro1 => $"Map {GameName}",
        MapPhase.Intro2 => "Now change the settings",
        MapPhase.Summary => $"{GameName} is mapped",
        MapPhase.Sent => "Sent to the game lab",
        MapPhase.Failed => "The game didn't start",
        _ => "Playing…",
    };

    public string Lead => Phase switch
    {
        MapPhase.Intro1 => "Launch the game, change nothing, and quit at the main menu or after loading in.",
        MapPhase.Intro2 => "Launch again. In the graphics menu change as many options as you can (for example the overall preset High → Low, the resolution and the upscaler), apply, and quit.",
        MapPhase.Running1 => "Play until the main menu or a loaded game, then quit. Don't change any settings.",
        MapPhase.Running2 => "Change as many graphics options as you can, apply them, then quit the game.",
        MapPhase.Summary => SummaryLead,
        MapPhase.Sent => "Claude will use it to find the file or key your presets should write. You'll get a notice when the presets arrive.",
        MapPhase.Failed => string.IsNullOrEmpty(_message) ? "Optimus-Vibe couldn't start the game." : _message,
        _ => "",
    };

    public string Note => Phase switch
    {
        MapPhase.Intro1 or MapPhase.Intro2 =>
            "Optimus-Vibe looks at the game's settings files and registry keys before and after each launch. It changes and restores nothing.",
        MapPhase.Summary => _actions.CanSend
            ? "Only file names, setting names and values are sent, to your private config repo. Personal details are removed first, and binary files are never sent, only their size and hash."
            : "The game lab isn't set up yet. Set it up in Settings and you can send this from the game's page.",
        MapPhase.Sent => _message,
        _ => "",
    };

    public string PrimaryText => Phase switch
    {
        MapPhase.Intro1 or MapPhase.Intro2 => "Launch the game",
        MapPhase.Summary => "Send to game lab",
        MapPhase.Sent => "Done",
        MapPhase.Failed => "Try again",
        _ => "",
    };

    public string? SecondaryText => Phase == MapPhase.Summary && _actions.CanKeep && !_kept ? "Keep my changes as a manual save" : null;
    public string BackText => Phase is MapPhase.Summary or MapPhase.Sent or MapPhase.Failed ? "Close" : "Cancel";

    public bool HasPrimary => !IsBusy && Phase is not (MapPhase.Running1 or MapPhase.Running2) && (Phase != MapPhase.Summary || _actions.CanSend);
    public bool HasSecondary => !IsBusy && SecondaryText is not null;
    public bool HasBack => Phase is not (MapPhase.Running1 or MapPhase.Running2);
    public bool ShowBusy => IsBusy;

    public string BusyText => Phase == MapPhase.Summary ? "Working…" : "";
    public bool KeptNote => _kept;
    public string KeptText => _kept ? _message : "";

    // ---------------------------------------------------------------- results

    public IReadOnlyList<string> StateLines => _evidence?.StateLines ?? [];
    public IReadOnlyList<string> SettingsLines => _evidence?.SettingsLines ?? [];
    public bool IsSummary => Phase is MapPhase.Summary or MapPhase.Sent;
    public bool HasStateLines => IsSummary && StateLines.Count > 0;
    public bool HasSettingsLines => IsSummary && SettingsLines.Count > 0;
    public bool NoSettingsFound => IsSummary && SettingsLines.Count == 0;

    public string StateHeading => "The game rewrites by itself (progress and state, never restored)";
    public string SettingsHeading => "Settings changed";
    public string NoSettingsText => "Nothing changed beyond what the game rewrites by itself. The settings may live outside the folders Optimus-Vibe watches; the game lab will look further.";

    public bool HasPassAResult => Phase is MapPhase.Intro2 && _passA is not null;
    public string PassAResult => _passA is null
        ? ""
        : _passA.IsEmpty
            ? "Launch 1: the game changed nothing on its own."
            : $"Launch 1: the game rewrote {_passA.Files.Count} file(s) and {_passA.Registry.Count} registry value(s) by itself.";

    private string SummaryLead
    {
        get
        {
            if (_evidence is null) return "";
            var n = _evidence.SettingsLines.Count;
            return n == 0
                ? "Launch 2 found no settings outside what the game rewrites by itself."
                : n == 1 ? "Your changes landed in 1 place." : $"Your changes landed in {n} places.";
        }
    }

    // ---------------------------------------------------------------- input (A, Y, B)

    public void Primary()
    {
        if (IsBusy) return;
        switch (Phase)
        {
            case MapPhase.Intro1 or MapPhase.Failed when _passA is null:
                _ = RunAsync(1);
                break;
            case MapPhase.Intro2 or MapPhase.Failed:
                _ = RunAsync(2);
                break;
            case MapPhase.Summary:
                _ = SendAsync();
                break;
            case MapPhase.Sent:
                CloseRequested?.Invoke();
                break;
        }
    }

    public void Secondary()
    {
        if (IsBusy || Phase != MapPhase.Summary || !_actions.CanKeep || _kept) return;
        _ = KeepAsync();
    }

    public void Back()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke();
    }

    private async Task RunAsync(int pass)
    {
        IsBusy = true;
        Phase = pass == 1 ? MapPhase.Running1 : MapPhase.Running2;
        _actions.GameStarting();
        HideRequested?.Invoke();
        ProbeDiff? diff = null;
        try
        {
            diff = await _actions.RunPassAsync(pass);
        }
        catch (Exception ex)
        {
            Log.Warn($"Map this game: pass {pass} failed: {ex.Message}");
        }
        finally
        {
            _actions.GameExited();
            IsBusy = false;
        }

        if (diff is null)
        {
            _message = "Optimus-Vibe couldn't start the game. Start it from its launcher and try again.";
            Phase = MapPhase.Failed;
            ShowRequested?.Invoke();
            return;
        }
        if (pass == 1)
        {
            _passA = diff;
            Phase = MapPhase.Intro2;
        }
        else
        {
            _evidence = _actions.BuildEvidence();
            Phase = MapPhase.Summary;
        }
        ShowRequested?.Invoke();
    }

    private async Task SendAsync()
    {
        if (!_actions.CanSend) return;
        IsBusy = true;
        try
        {
            var result = await _actions.SendAsync();
            if (result is null) return;
            _message = result;
            Phase = MapPhase.Sent;
        }
        catch (Exception ex)
        {
            Log.Warn($"Map this game: sending failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task KeepAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _actions.KeepAsync();
            if (result is null) return;
            _message = result;
            _kept = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Map this game: saving failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(string.Empty);
        }
    }

#if DEBUG
    /// <summary>Debug scenes only: jump to a phase with sample data (intro1 | running | intro2 | summary | sent | failed).</summary>
    public void ShowDemo(string phase, ProbeDiff? passA = null, ProbeEvidence? evidence = null, bool kept = false)
    {
        _passA = passA;
        _evidence = evidence;
        _kept = kept;
        _message = phase switch
        {
            "sent" => "Filed as request #7 in your config repo.",
            "failed" => "",
            _ => kept ? "Saved “Game's own Oct 20 21:40”: 0 setting(s), 1 whole file(s) - now active." : "",
        };
        Phase = phase switch
        {
            "running" => MapPhase.Running1,
            "intro2" => MapPhase.Intro2,
            "summary" => MapPhase.Summary,
            "sent" => MapPhase.Sent,
            "failed" => MapPhase.Failed,
            _ => MapPhase.Intro1,
        };
        OnPropertyChanged(string.Empty);
    }
#endif
}
