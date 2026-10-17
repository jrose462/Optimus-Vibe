using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using OptimusVibe.Library;

namespace OptimusVibe.App.ViewModels;

/// <summary>One row of the review panel: a planned launch option change with a checkbox (ticked = apply it).</summary>
public sealed class SteamReviewItem : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked = true;

    public SteamReviewItem(LaunchOptionChange change, string name, Action changed)
    {
        Change = change;
        Name = name;
        _changed = changed;
    }

    public LaunchOptionChange Change { get; }
    public string Name { get; }
    public string Key => $"steam:{Change.AppId}";

    /// <summary>The first row takes the keyboard / gamepad focus when the panel opens.</summary>
    public bool IsFirst { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) _changed(); }
    }

    public string BeforeText => Compact(Change.Before);
    public string AfterText => Compact(Change.After);
    public string ToolTipText => $"Before: {Change.Before ?? "(none)"}\nAfter: {Change.After ?? "(none)"}";

    private const int MaxLength = 64;

    /// <summary>Short form for a row: "(none)" when empty, our wrapper's long exe path reduced to the file name, cut with an ellipsis.</summary>
    public static string Compact(string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return "(none)";
        var s = option.Trim();
        if (SteamLaunchOptions.WrappedExe(s) is { Length: > 0 } exe)
            s = s.Replace($"\"{exe}\"", Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)
                .Replace(exe, Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase);
        return s.Length <= MaxLength ? s : s[..(MaxLength - 1)] + "…";
    }
}

/// <summary>
/// "Review Steam launch options" panel: every planned change with a checkbox. Applying writes only the ticked ones
/// (after closing Steam, same as the banner's apply-all); unticked games go to steamLaunchOptionsSkip in library.yaml so
/// they are not proposed again.
/// </summary>
public sealed class SteamReviewViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isOpen;

    public SteamReviewViewModel(MainViewModel main)
    {
        _main = main;
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !main.IsBusy && SelectedCount > 0);
        CancelCommand = new RelayCommand(Close);
    }

    public ObservableCollection<SteamReviewItem> Items { get; } = new();

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }

    public int SelectedCount => Items.Count(i => i.IsChecked);
    public string SummaryText => $"{SelectedCount} of {Items.Count} selected";
    public string ApplyLabel => $"Apply selected ({SelectedCount})";

    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public void Open(IReadOnlyList<LaunchOptionChange> changes)
    {
        Items.Clear();
        foreach (var c in changes
                     .Select(c => (c, name: _main.Library.SteamGameName(c.AppId, c.GameId)))
                     .OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase))
            Items.Add(new SteamReviewItem(c.c, c.name, RaiseSelection) { IsFirst = Items.Count == 0 });
        RaiseSelection();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetAll(bool value)
    {
        foreach (var i in Items) i.IsChecked = value;
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ApplyLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ApplyAsync()
    {
        var selected = Items.Where(i => i.IsChecked).Select(i => i.Change).ToList();
        var skipKeys = Items.Where(i => !i.IsChecked).Select(i => i.Key).ToList();
        if (selected.Count == 0) return;
        if (await _main.ApplySteamChangesAsync(selected, skipKeys)) Close();
    }
}
