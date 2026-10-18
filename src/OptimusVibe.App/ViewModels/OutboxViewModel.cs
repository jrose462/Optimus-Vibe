using System.Collections.ObjectModel;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.GameLab;
using OptimusVibe.Logging;
using OptimusVibe.Outbox;

namespace OptimusVibe.App.ViewModels;

/// <summary>One waiting item in the outbox panel. "Discard" asks for a second click (no dialog).</summary>
public sealed class OutboxItemViewModel : ObservableObject
{
    private bool _confirming;

    public OutboxItemViewModel(OutboxItem item, Action<OutboxItemViewModel> discard)
    {
        Item = item;
        DiscardCommand = new RelayCommand(() =>
        {
            if (!_confirming) { Confirming = true; return; }
            discard(this);
        });
    }

    public OutboxItem Item { get; }
    public string Id => Item.Id;
    public string Summary => Item.Summary;
    public string KindText => Item.Kind == OutboxKind.BugReport ? "Bug report" : "Game lab request";
    public string WhenText => $"Saved {Item.CreatedUtc.ToLocalTime():g}";
    public bool HasError => !string.IsNullOrWhiteSpace(Item.LastError);
    public string ErrorText => HasError ? $"Last try failed: {Item.LastError}" : "";

    public bool Confirming
    {
        get => _confirming;
        set { if (Set(ref _confirming, value)) OnPropertyChanged(nameof(DiscardLabel)); }
    }

    public string DiscardLabel => _confirming ? "Really discard?" : "Discard";
    public ICommand DiscardCommand { get; }
}

/// <summary>The header indicator ("N waiting") and the panel that lists the offline outbox (Send now / Discard).</summary>
public sealed class OutboxViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isOpen;
    private bool _isSending;

    public OutboxViewModel(MainViewModel main, GitHubAccountViewModel account)
    {
        _main = main;
        Service = new OutboxService(main);
        Service.Changed += Reload;
        account.SignedIn += (_, _) => Service.ScheduleDrain(); // a queued "as the signed-in user" report / lab request can go now
        OpenCommand = new RelayCommand(Open);
        CloseCommand = new RelayCommand(Close);
        SendNowCommand = new AsyncRelayCommand(SendNowAsync, () => !IsSending && Items.Count > 0);
        Reload();
    }

    public OutboxService Service { get; }
    public OutboxStore Store => Service.Store;
    public ObservableCollection<OutboxItemViewModel> Items { get; } = new();

    public int Count => Items.Count;
    public bool HasItems => Items.Count > 0;
    public string BadgeText => $"{Items.Count} waiting";

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsSending { get => _isSending; private set { if (Set(ref _isSending, value)) CommandManager.InvalidateRequerySuggested(); } }

    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SendNowCommand { get; }

    /// <summary>Start the triggers (call once the config session has loaded).</summary>
    public void Start() => Service.Start();

    public void Open()
    {
        Reload();
        IsOpen = true;
    }

    public void Close()
    {
        foreach (var i in Items) i.Confirming = false;
        IsOpen = false;
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Re-reads the store (UI thread).</summary>
    public void Reload()
    {
        IReadOnlyList<OutboxItem> list;
        try { list = Store.List(); }
        catch (Exception ex)
        {
            Log.Warn($"Reading the outbox failed: {ex.Message}");
            list = [];
        }
        Items.Clear();
        foreach (var i in list) Items.Add(new OutboxItemViewModel(i, Discard));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(BadgeText));
        if (Items.Count == 0 && IsOpen) Close();
        CommandManager.InvalidateRequerySuggested();
    }

    private void Discard(OutboxItemViewModel vm)
    {
        try { Store.Remove(vm.Id); }
        catch (Exception ex) { Log.Warn($"Discarding outbox item failed: {ex.Message}"); }
        Reload();
    }

    private async Task SendNowAsync()
    {
        IsSending = true;
        try { await Service.DrainAsync(manual: true); }
        finally { IsSending = false; }
    }
}
