using System.Globalization;
using System.Windows.Input;
using GameProfiles.Engine;

namespace GameProfiles.App.ViewModels;

/// <summary>A recommended preset or manual save card. Manual-only actions (rename, update, delete) are hidden for recommended.</summary>
public sealed class PresetCardViewModel : ObservableObject
{
    private bool _isRenaming;
    private bool _isConfirmingDelete;
    private string _renameText = "";

    public PresetCardViewModel(GameDetailViewModel owner, PresetOption option, bool gameHasSelection)
    {
        Option = option;
        IsPinned = option.IsActive && gameHasSelection;

        bool Idle() => !owner.Shell.IsBusy;
        UseCommand = new AsyncRelayCommand(() => owner.UseAsync(this), () => Idle() && !HasError && !IsPinned);
        ApplyCommand = new AsyncRelayCommand(() => owner.OpenPreviewAsync(this, applyMode: true), () => Idle() && !HasError);
        PreviewCommand = new AsyncRelayCommand(() => owner.OpenPreviewAsync(this, applyMode: false), () => Idle() && !HasError);
        UpdateFromCurrentCommand = new AsyncRelayCommand(() => owner.UpdateFromCurrentAsync(this), Idle);
        BeginRenameCommand = new RelayCommand(() =>
        {
            owner.CancelTransient();
            RenameText = Label;
            IsRenaming = true;
        }, Idle);
        ConfirmRenameCommand = new AsyncRelayCommand(async () =>
        {
            if (await owner.RenameAsync(this, RenameText)) IsRenaming = false;
        }, () => Idle() && !string.IsNullOrWhiteSpace(RenameText));
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        BeginDeleteCommand = new RelayCommand(() =>
        {
            owner.CancelTransient();
            IsConfirmingDelete = true;
        }, Idle);
        ConfirmDeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this), Idle);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public PresetOption Option { get; private set; }
    public bool IsManual => Option.Save is not null;
    public string Label => Option.Label;
    public string? Description => Option.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string? FpsText => Option.TargetFps is { } f ? $"{f} fps" : null;
    public bool IsActive => Option.IsActive;

    /// <summary>Active because it was picked (not just the device default) — "Use" has nothing left to do.</summary>
    public bool IsPinned { get; private set; }

    public bool IsDefault => Option.IsDefault;
    public string? Error => Option.Error;
    public bool HasError => Option.Error is not null;
    public string UseText => IsPinned ? "In use" : "Use";

    /// <summary>Manual saves: "Updated Oct 3 14:05 · based on recommended:performance".</summary>
    public string? MetaText
    {
        get
        {
            if (Option.Save is not { } s) return null;
            var parts = new List<string>();
            if (s.Updated > DateTime.MinValue) parts.Add("Updated " + s.Updated.ToString("MMM d, yyyy HH:mm", CultureInfo.CurrentCulture));
            if (!string.IsNullOrWhiteSpace(s.BasedOn)) parts.Add("based on " + s.BasedOn);
            return parts.Count == 0 ? null : string.Join("  ·  ", parts);
        }
    }

    public string? Slug => Option.Save?.Slug;

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (Set(ref _isRenaming, value)) OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        set
        {
            if (Set(ref _isConfirmingDelete, value)) OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsRenaming && !IsConfirmingDelete;

    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }

    public ICommand UseCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand UpdateFromCurrentCommand { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand ConfirmRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand BeginDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    /// <summary>Refresh from a reloaded catalog without recreating the card (keeps keyboard/gamepad focus).</summary>
    public void Update(PresetOption option, bool gameHasSelection)
    {
        Option = option;
        IsPinned = option.IsActive && gameHasSelection;
        OnPropertyChanged(string.Empty);
    }

    public bool CancelTransient()
    {
        bool any = IsRenaming || IsConfirmingDelete;
        IsRenaming = false;
        IsConfirmingDelete = false;
        return any;
    }
}
