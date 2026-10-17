using System.Text;
using System.Text.Json;
using OptimusVibe.Logging;
using OptimusVibe.Ui;

namespace OptimusVibe.Launch;

/// <summary>What the player chose after the game exited and settings had drifted.</summary>
public enum CaptureChoice
{
    /// <summary>Leave the saves alone; the active preset is re-applied next launch.</summary>
    DontSave,

    /// <summary>Update the active manual save, or create a new one when the active setting is a recommended preset.</summary>
    Save,

    /// <summary>Always a new manual save, named <see cref="CaptureDecision.Name"/>.</summary>
    SaveAs,

    /// <summary>Don't save, and don't ask again for this game on this device.</summary>
    DontAskAgain,
}

/// <param name="Name">The name typed for <see cref="CaptureChoice.SaveAs"/>.</param>
public sealed record CaptureDecision(CaptureChoice Choice, string? Name = null);

/// <summary>Everything a prompt needs to show; no engine types, so UIs stay out of Core.</summary>
/// <param name="Lines">The first changed settings as "Shadows: High → Medium" (at most <see cref="CapturePrompts.MaxLines"/>).</param>
/// <param name="TotalChanges">All changed settings, including the ones not listed.</param>
/// <param name="ActiveSaveName">The manual save that Save would update; null when Save creates a new one.</param>
/// <param name="SuggestedName">Prefill for Save as.</param>
public sealed record CaptureRequest(string GameId, string GameName, string DeviceName, string PresetLabel,
    IReadOnlyList<string> Lines, int TotalChanges, string? ActiveSaveName, string SuggestedName)
{
    public int More => Math.Max(0, TotalChanges - Lines.Count);
}

/// <summary>Asks the player what to do with settings they changed in-game. Blocks until answered.</summary>
public interface ICapturePrompt
{
    CaptureDecision Ask(CaptureRequest request);
}

public static class CapturePrompts
{
    public const int MaxLines = 12;

    /// <summary>
    /// The prompt used by <see cref="Runner"/>. The CLI keeps the message-box default; the app registers its full-screen,
    /// controller-friendly window at startup.
    /// </summary>
    public static ICapturePrompt Current { get; set; } = new MessageBoxCapturePrompt();
}

/// <summary>The fallback: a Win32 Yes/No box (Yes = Save, No = Don't save).</summary>
public sealed class MessageBoxCapturePrompt : ICapturePrompt
{
    public CaptureDecision Ask(CaptureRequest r)
    {
        var lines = string.Join("\n", r.Lines.Select(l => "• " + l));
        var more = r.More > 0 ? $"\n…and {r.More} more" : "";
        var question = r.ActiveSaveName is { } current
            ? $"Update your manual save '{current}' with these changes?"
            : $"Keep them as a new manual save for {r.DeviceName}? It becomes this game's active setting.";
        var message = $"{r.TotalChanges} setting(s) changed while playing {r.GameName}:\n\n{lines}{more}\n\n" +
                      $"{question}\n(No = '{r.PresetLabel}' is re-applied next launch)";
        return new CaptureDecision(NativeUi.AskYesNo("Optimus-Vibe", message) ? CaptureChoice.Save : CaptureChoice.DontSave);
    }
}

/// <summary>
/// "Don't ask again for this game": a small per-device json in the data dir (<c>capture-prompt.json</c>), not synced. Never throws.
/// </summary>
public static class CapturePromptStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Model
    {
        public List<string> DontAsk { get; set; } = [];
    }

    public static string FileFor(string dataDir) => Path.Combine(dataDir, "capture-prompt.json");

    public static bool IsSkipped(string dataDir, string gameId)
    {
        lock (Gate) return Load(dataDir).DontAsk.Contains(gameId, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Skipped(string dataDir)
    {
        lock (Gate) return Load(dataDir).DontAsk.ToList();
    }

    public static void SetSkipped(string dataDir, string gameId, bool skipped)
    {
        lock (Gate)
        {
            var m = Load(dataDir);
            var had = m.DontAsk.RemoveAll(g => g.Equals(gameId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (skipped) m.DontAsk.Add(gameId);
            if (had == skipped) return;
            m.DontAsk.Sort(StringComparer.OrdinalIgnoreCase);
            try
            {
                Directory.CreateDirectory(dataDir);
                var tmp = FileFor(dataDir) + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts), new UTF8Encoding(false));
                File.Move(tmp, FileFor(dataDir), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not save {FileFor(dataDir)}: {ex.Message}");
            }
        }
    }

    private static Model Load(string dataDir)
    {
        try
        {
            var file = FileFor(dataDir);
            if (!File.Exists(file)) return new Model();
            var m = JsonSerializer.Deserialize<Model>(File.ReadAllText(file), Opts) ?? new Model();
            m.DontAsk ??= [];
            return m;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Could not read {FileFor(dataDir)}: {ex.Message}");
            return new Model();
        }
    }
}
