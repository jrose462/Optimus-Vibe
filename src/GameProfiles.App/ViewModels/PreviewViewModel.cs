using System.Windows.Input;
using GameProfiles.Engine;

namespace GameProfiles.App.ViewModels;

public sealed record KeyChangeItem(string Key, string Current, string Target);

public sealed class FilePreviewItem
{
    public required string Id { get; init; }
    public string? Path { get; init; }
    public string? SkipReason { get; init; }
    public bool IsSkipped => SkipReason is not null;
    public bool RestoreSnapshot { get; init; }
    public List<KeyChangeItem> Changes { get; init; } = new();
    public bool HasChanges => Changes.Count > 0;

    public string StatusText =>
        IsSkipped ? "skipped" :
        RestoreSnapshot ? $"restore saved snapshot{(Changes.Count > 0 ? $" + {Changes.Count} setting(s)" : "")}" :
        Changes.Count == 0 ? "up to date" : $"{Changes.Count} change(s)";
}

/// <summary>The diff panel shown before "Apply now" (or on "Preview"): file key changes, skipped files, driver, display.</summary>
public sealed class PreviewViewModel : ObservableObject
{
    private readonly GameDetailViewModel _owner;
    private bool _isApplyMode;
    private bool _alsoUse = true;

    public PreviewViewModel(GameDetailViewModel owner, PresetCardViewModel card, ApplyPreview preview, bool applyMode)
    {
        _owner = owner;
        Card = card;
        Preview = preview;
        _isApplyMode = applyMode;

        Files = preview.Files.Select(p => new FilePreviewItem
        {
            Id = p.File.Id,
            Path = p.File.Path,
            SkipReason = p.Skip,
            RestoreSnapshot = p.RestoreSnapshot,
            Changes = p.Changes.Select(c => new KeyChangeItem(c.Key, c.Current ?? "(missing)", c.Target)).ToList(),
        }).ToList();

        if (preview.DriverUnavailableReason is { } why)
            DriverLines.Add($"{preview.DriverVendor} driver settings skipped — {why}");
        foreach (var c in preview.DriverChanges)
            DriverLines.Add($"{preview.DriverVendor}: {c.Setting}: {c.Current ?? "(unset)"} → {c.Target}" +
                            (preview.DriverAppliedNow ? "" : "  (applied only when launched)"));
        if (preview.Display is { } d)
            DisplayNote = $"Display {d} is applied only when the game is launched through GameProfiles, and restored on exit.";

        ConfirmCommand = new AsyncRelayCommand(() => _owner.ApplyAsync(this), () => !_owner.Shell.IsBusy);
        SwitchToApplyCommand = new RelayCommand(() => IsApplyMode = true);
        CloseCommand = new RelayCommand(() => _owner.ClosePreview());
    }

    public PresetCardViewModel Card { get; }
    public ApplyPreview Preview { get; }
    public List<FilePreviewItem> Files { get; }
    public List<string> DriverLines { get; } = new();
    public bool HasDriverLines => DriverLines.Count > 0;
    public string? DisplayNote { get; }
    public bool HasDisplayNote => DisplayNote is not null;

    public int ChangeCount => Files.Sum(f => f.Changes.Count);
    public bool HasWork => Preview.HasFileWork || (Preview.DriverAppliedNow && Preview.DriverChanges.Any(c => c.Current != c.Target));

    public string Title => IsApplyMode ? $"Apply “{Card.Label}” now?" : $"Preview “{Card.Label}”";

    public string Summary
    {
        get
        {
            int files = Files.Count(f => !f.IsSkipped && (f.HasChanges || f.RestoreSnapshot));
            int skipped = Files.Count(f => f.IsSkipped);
            var s = ChangeCount == 0 && files == 0
                ? "Everything already matches this preset."
                : $"{ChangeCount} setting(s) change in {files} file(s).";
            if (skipped > 0) s += $" {skipped} file(s) skipped.";
            return s;
        }
    }

    public bool IsApplyMode
    {
        get => _isApplyMode;
        set
        {
            if (!Set(ref _isApplyMode, value)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShowAlsoUse));
        }
    }

    /// <summary>Offer "also use for future launches" unless it is already the picked preset.</summary>
    public bool ShowAlsoUse => IsApplyMode && !Card.IsPinned;

    public bool AlsoUse { get => _alsoUse; set => Set(ref _alsoUse, value); }

    public ICommand ConfirmCommand { get; }
    public ICommand SwitchToApplyCommand { get; }
    public ICommand CloseCommand { get; }
}
