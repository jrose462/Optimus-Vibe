using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using OptimusVibe.App.Services;
using OptimusVibe.Config;
using OptimusVibe.Logging;
using OptimusVibe.Sync;

namespace OptimusVibe.App.ViewModels;

public sealed class RepoItemViewModel(GitHubRepoInfo info, bool isCurrent) : ObservableObject
{
    public GitHubRepoInfo Info { get; } = info;
    public string FullName => Info.FullName;
    public string? Description => Info.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Info.Description);
    public bool IsPrivate => Info.Private;
    public bool IsCurrent { get; } = isCurrent;
    public override string ToString() => FullName;
}

/// <summary>
/// GitHub sign-in (device code or personal access token), the signed-in account, and the config repo picker.
/// Used by Settings and by the first-run screen. Network calls are async; credential-store work runs off the UI thread.
/// </summary>
public sealed class GitHubAccountViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _signInCts;
    private string? _login;
    private string _clientId = "";
    private bool _hasClientId;
    private DeviceCode? _deviceCode;
    private bool _isSigningIn;
    private bool _showPat;
    private string _pat = "";
    private bool _isLoadingRepos;
    private string _repoFilter = "";
    private RepoItemViewModel? _selectedRepo;
    private string _repoName = AppSettings.DefaultGitHubRepo;
    private string _branch = "main";
    private string _currentRepo = AppSettings.DefaultGitHubRepo;
    private string? _signInError;
    private bool _isCreateOpen;
    private string _newRepoName = "optimus-vibe-config";
    private bool _newRepoPrivate = true;
    private string? _seedPrompt;
    private string? _seedRepo;
    private string _seedBranch = "main";

    public GitHubAccountViewModel(MainViewModel main)
    {
        _main = main;
        bool Idle() => !main.IsBusy;
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => Idle() && !IsSigningIn && HasClientId);
        CancelSignInCommand = new RelayCommand(CancelSignIn, () => IsSigningIn);
        OpenVerificationCommand = new RelayCommand(() => OpenUrl(DeviceCode?.VerificationUri ?? "https://github.com/login/device"));
        CopyCodeCommand = new RelayCommand(CopyCode, () => DeviceCode is not null);
        SaveClientIdCommand = new AsyncRelayCommand(SaveClientIdAsync, () => Idle() && !string.IsNullOrWhiteSpace(ClientId));
        EditClientIdCommand = new RelayCommand(() => HasClientId = false);
        TogglePatCommand = new RelayCommand(() => ShowPat = !ShowPat);
        UsePatCommand = new AsyncRelayCommand(UsePatAsync, () => Idle() && !string.IsNullOrWhiteSpace(Pat));
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => Idle() && IsSignedIn);
        LoadReposCommand = new AsyncRelayCommand(LoadReposAsync, () => Idle() && IsSignedIn && !IsLoadingRepos);
        UseRepoCommand = new AsyncRelayCommand(UseRepoAsync, () => Idle() && IsSignedIn && !string.IsNullOrWhiteSpace(EffectiveRepo));
        OpenCreateCommand = new RelayCommand(() => { SeedPrompt = null; IsCreateOpen = true; }, () => Idle() && IsSignedIn);
        CancelCreateCommand = new RelayCommand(() => IsCreateOpen = false);
        CreateRepoCommand = new AsyncRelayCommand(CreateRepoAsync, () => Idle() && IsSignedIn && !string.IsNullOrWhiteSpace(NewRepoName));
        ConfirmSeedCommand = new AsyncRelayCommand(ConfirmSeedAsync, () => Idle() && IsSignedIn && SeedPrompt is not null);
        CancelSeedCommand = new RelayCommand(() => SeedPrompt = null);
        OpenDeveloperSettingsCommand = new RelayCommand(() => OpenUrl("https://github.com/settings/developers"));
        OpenTokenSettingsCommand = new RelayCommand(() => OpenUrl("https://github.com/settings/tokens/new?scopes=repo&description=Optimus-Vibe"));
    }

    // ---------------------------------------------------------------- account

    public string? Login
    {
        get => _login;
        private set
        {
            if (!Set(ref _login, value)) return;
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(AccountText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSignedIn => Login is not null;
    public string AccountText => Login is { } l ? $"Signed in as {l}" : "Not signed in";

    public string ClientId { get => _clientId; set { if (Set(ref _clientId, value)) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>A client id is saved: the sign-in button is offered; otherwise the how-to + box.</summary>
    public bool HasClientId { get => _hasClientId; private set { if (Set(ref _hasClientId, value)) CommandManager.InvalidateRequerySuggested(); } }

    public DeviceCode? DeviceCode
    {
        get => _deviceCode;
        private set
        {
            if (!Set(ref _deviceCode, value)) return;
            OnPropertyChanged(nameof(UserCode));
            OnPropertyChanged(nameof(VerificationUri));
            OnPropertyChanged(nameof(HasDeviceCode));
        }
    }

    public bool HasDeviceCode => DeviceCode is not null;
    public string UserCode => DeviceCode?.UserCode ?? "";
    public string VerificationUri => DeviceCode?.VerificationUri ?? "https://github.com/login/device";

    public bool IsSigningIn
    {
        get => _isSigningIn;
        private set { if (Set(ref _isSigningIn, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string? SignInError { get => _signInError; private set { if (Set(ref _signInError, value)) OnPropertyChanged(nameof(HasSignInError)); } }
    public bool HasSignInError => SignInError is not null;

    public bool ShowPat { get => _showPat; set => Set(ref _showPat, value); }
    public string Pat { get => _pat; set => Set(ref _pat, value); }

    // ---------------------------------------------------------------- repos

    public ObservableCollection<RepoItemViewModel> Repos { get; } = new();
    public ObservableCollection<RepoItemViewModel> FilteredRepos { get; } = new();

    public bool IsLoadingRepos
    {
        get => _isLoadingRepos;
        private set { if (Set(ref _isLoadingRepos, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HasRepos => Repos.Count > 0;

    public string RepoFilter
    {
        get => _repoFilter;
        set { if (Set(ref _repoFilter, value)) ApplyRepoFilter(); }
    }

    public RepoItemViewModel? SelectedRepo
    {
        get => _selectedRepo;
        set
        {
            if (!Set(ref _selectedRepo, value) || value is null) return;
            RepoName = value.FullName;
            Branch = value.FullName.Equals(_currentRepo, StringComparison.OrdinalIgnoreCase) ? Branch : value.Info.DefaultBranch;
        }
    }

    /// <summary>owner/name typed or picked (first-run uses the text box; settings the list).</summary>
    public string RepoName { get => _repoName; set { if (Set(ref _repoName, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Branch { get => _branch; set => Set(ref _branch, value); }
    public string CurrentRepo { get => _currentRepo; private set => Set(ref _currentRepo, value); }
    private string EffectiveRepo => RepoName.Trim();

    public ICommand SignInCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public ICommand OpenVerificationCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand SaveClientIdCommand { get; }
    public ICommand EditClientIdCommand { get; }
    public ICommand TogglePatCommand { get; }
    public ICommand UsePatCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand LoadReposCommand { get; }
    public ICommand UseRepoCommand { get; }
    public ICommand OpenCreateCommand { get; }
    public ICommand CancelCreateCommand { get; }
    public ICommand CreateRepoCommand { get; }
    public ICommand ConfirmSeedCommand { get; }
    public ICommand CancelSeedCommand { get; }

    /// <summary>"Create a new config repo" form is open.</summary>
    public bool IsCreateOpen { get => _isCreateOpen; private set => Set(ref _isCreateOpen, value); }

    public string NewRepoName { get => _newRepoName; set { if (Set(ref _newRepoName, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool NewRepoPrivate { get => _newRepoPrivate; set => Set(ref _newRepoPrivate, value); }

    /// <summary>Set while asking whether to seed the picked repo (reachable, but no config yet).</summary>
    public string? SeedPrompt
    {
        get => _seedPrompt;
        private set
        {
            if (!Set(ref _seedPrompt, value)) return;
            OnPropertyChanged(nameof(HasSeedPrompt));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSeedPrompt => SeedPrompt is not null;
    public ICommand OpenDeveloperSettingsCommand { get; }
    public ICommand OpenTokenSettingsCommand { get; }

    // ---------------------------------------------------------------- load

    /// <summary>Read settings + whether a token is stored. Cheap; safe to call whenever the page opens.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var s = await SettingsStore.LoadAsync();
            ClientId = s.GitHubClientId ?? AppPaths.Env("GITHUB_CLIENT_ID") ?? "";
            HasClientId = !string.IsNullOrWhiteSpace(ClientId);
            CurrentRepo = string.IsNullOrWhiteSpace(s.GitHubRepo) ? AppSettings.DefaultGitHubRepo : s.GitHubRepo;
            if (string.IsNullOrWhiteSpace(RepoName) || Repos.Count == 0) RepoName = CurrentRepo;
            Branch = string.IsNullOrWhiteSpace(s.GitHubBranch) ? "main" : s.GitHubBranch;
            bool hasToken = await Task.Run(() =>
            {
                try { return GitHubAuth.LoadToken() is not null; }
                catch (NotImplementedException) { return false; }
            });
            Login = hasToken ? s.GitHubLogin ?? "GitHub account" : null;
        }
        catch (Exception ex)
        {
            Log.Error("Reading GitHub account state failed", ex);
        }
    }

    // ---------------------------------------------------------------- sign-in

    private async Task SaveClientIdAsync()
    {
        var id = ClientId.Trim();
        await SettingsStore.UpdateAsync(s => s.GitHubClientId = id);
        HasClientId = true;
        _main.Notify("GitHub client id saved — now Sign in with GitHub.");
    }

    private async Task SignInAsync()
    {
        CancelSignIn();
        var clientId = ClientId.Trim();
        var cts = _signInCts = new CancellationTokenSource();
        IsSigningIn = true;
        SignInError = null;
        try
        {
            DeviceCode = await StartDeviceFlowAsync(clientId, cts.Token);
            Log.Info($"GitHub device flow started ({DeviceCode.VerificationUri})");
            CopyCode();
            var token = await PollAsync(clientId, DeviceCode, cts.Token);
            DeviceCode = null;
            await CompleteSignInAsync(token, "GitHub sign-in");
        }
        catch (OperationCanceledException)
        {
            Log.Info("GitHub sign-in cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("GitHub sign-in failed", ex);
            SignInError = Friendly(ex);
            _main.Notify(SignInError, error: true);
        }
        finally
        {
            if (_signInCts == cts) _signInCts = null;
            DeviceCode = null;
            IsSigningIn = false;
        }
    }

    private static Task<DeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken ct)
    {
#if DEBUG
        // Screenshot / UI testing without an OAuth App: client id "demo" shows a fake code that never completes.
        if (clientId == "demo") return Task.FromResult(new DeviceCode("WDJB-MJHT", "https://github.com/login/device", 900, 5, "demo"));
#endif
        return GitHubAuth.StartDeviceFlowAsync(clientId, ct);
    }

    private static async Task<string> PollAsync(string clientId, DeviceCode code, CancellationToken ct)
    {
#if DEBUG
        if (code.Code == "demo")
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException();
        }
#endif
        return await GitHubAuth.PollForTokenAsync(clientId, code, ct);
    }

    private void CancelSignIn()
    {
        _signInCts?.Cancel();
        _signInCts = null;
        DeviceCode = null;
        IsSigningIn = false;
    }

    /// <summary>B / Escape while the code is showing.</summary>
    public bool CancelTransient()
    {
        bool any = IsSigningIn || ShowPat || IsCreateOpen || HasSeedPrompt;
        CancelSignIn();
        ShowPat = false;
        IsCreateOpen = false;
        SeedPrompt = null;
        return any;
    }

    private async Task UsePatAsync()
    {
        var token = Pat.Trim();
        var ok = await _main.RunAsync("Checking the token with GitHub", () => CompleteSignInAsync(token, "token"));
        if (ok)
        {
            Pat = "";
            ShowPat = false;
        }
    }

    /// <summary>Validate the token (GetLogin) before storing it, remember the login, then list repos.</summary>
    private async Task CompleteSignInAsync(string token, string how)
    {
        var login = await Task.Run(() => new GitHubApi(token).GetLoginAsync());
        await Task.Run(() => GitHubAuth.SaveToken(token));
        await SettingsStore.UpdateAsync(s => s.GitHubLogin = login);
        Login = login;
        SignInError = null;
        Log.Info($"GitHub: signed in as {login} ({how})");
        _main.Notify($"Signed in to GitHub as {login}.");
        await LoadReposAsync();
    }

    private async Task SignOutAsync()
    {
        var ok = await _main.RunAsync("Signing out of GitHub", async () =>
        {
            await Task.Run(GitHubAuth.ClearToken);
            await SettingsStore.UpdateAsync(s => s.GitHubLogin = null);
        });
        if (!ok) return;
        Login = null;
        Repos.Clear();
        FilteredRepos.Clear();
        OnPropertyChanged(nameof(HasRepos));
        _main.Notify("Signed out of GitHub. The token was removed from Windows Credential Manager.");
    }

    // ---------------------------------------------------------------- repos

    public async Task LoadReposAsync()
    {
        if (IsLoadingRepos) return;
        IsLoadingRepos = true;
        try
        {
            var token = await Task.Run(GitHubAuth.LoadToken) ?? throw new InvalidOperationException("Sign in to GitHub first.");
            var repos = await Task.Run(() => new GitHubApi(token).ListReposAsync());
            Repos.Clear();
            foreach (var r in repos)
                Repos.Add(new RepoItemViewModel(r, r.FullName.Equals(CurrentRepo, StringComparison.OrdinalIgnoreCase)));
            ApplyRepoFilter();
            SelectedRepo = FilteredRepos.FirstOrDefault(r => r.IsCurrent) ?? SelectedRepo;
            OnPropertyChanged(nameof(HasRepos));
        }
        catch (Exception ex)
        {
            Log.Error("Listing GitHub repos failed", ex);
            _main.Notify($"Couldn't list your GitHub repos: {Friendly(ex)}", error: true);
        }
        finally
        {
            IsLoadingRepos = false;
        }
    }

    private void ApplyRepoFilter()
    {
        var f = RepoFilter.Trim();
        FilteredRepos.Clear();
        foreach (var r in Repos.Where(r => f.Length == 0 || r.FullName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                            (r.Description?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false))
                     .OrderByDescending(r => r.IsCurrent))
            FilteredRepos.Add(r);
    }

    /// <summary>Download the repo into the app's own copy, switch sync to the GitHub account, reload everything.</summary>
    private async Task UseRepoAsync()
    {
        var repo = EffectiveRepo;
        var branch = string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();
        SeedPrompt = null;
        ConfigRepoStatus? status = null;
        var probed = await _main.RunAsync($"Checking {repo}", () => Task.Run(async () =>
        {
            if (!GitHubApi.IsValidRepoName(repo)) throw new InvalidOperationException($"“{repo}” isn't an owner/name repository.");
            var token = GitHubAuth.LoadToken() ?? throw new InvalidOperationException("Sign in to GitHub first.");
            status = await ConfigRepoProbe.CheckAsync(new GitHubApi(token), repo, branch);
        }));
        if (!probed || status is null) return;
        if (status.IsConfigRepo)
        {
            await SwitchToRepoAsync(repo, branch, seedDir: null);
        }
        else if (status.CanSeed)
        {
            // Reachable, but not a config repo (empty, or other content): ask before writing anything to it.
            SeedPrompt = $"{status.Message} Seed this repo with your current devices and games?";
            _seedRepo = repo;
            _seedBranch = branch;
        }
        else
        {
            _main.Notify(status.Message, error: true);
        }
    }

    private async Task ConfirmSeedAsync()
    {
        var repo = _seedRepo;
        var branch = _seedBranch;
        SeedPrompt = null;
        if (repo is null) return;
        await SwitchToRepoAsync(repo, branch, SeedSourceDir());
    }

    /// <summary>The config folder in use right now: what a new or empty repo gets seeded with.</summary>
    private string SeedSourceDir() => _main.Session?.Paths.ConfigRoot
        ?? throw new InvalidOperationException("There is no config folder loaded to seed from — pick a local git checkout first.");

    private async Task CreateRepoAsync()
    {
        var name = NewRepoName.Trim();
        if (name.Length == 0) return;
        string seedDir;
        try { seedDir = SeedSourceDir(); }
        catch (InvalidOperationException ex)
        {
            _main.Notify(ex.Message, error: true);
            return;
        }
        var ok = await SwitchToRepoAsync(name, "main", seedDir, create: true);
        if (ok) IsCreateOpen = false;
    }

    /// <summary>
    /// Optionally create the repo (<paramref name="create"/>: <paramref name="repo"/> is a bare name) and/or seed it, then
    /// download the repo into the app's own copy, switch sync to the GitHub account and reload everything.
    /// </summary>
    private async Task<bool> SwitchToRepoAsync(string repo, string branch, string? seedDir, bool create = false)
    {
        string? configRoot = null;
        var login = Login;
        var seeded = false;
        var title = create ? $"Creating {repo}" : seedDir is not null ? $"Seeding {repo}" : $"Downloading {repo}";
        var ok = await _main.RunAsync(title, () => Task.Run(async () =>
        {
            if (!create && !GitHubApi.IsValidRepoName(repo)) throw new InvalidOperationException($"“{repo}” isn't an owner/name repository.");
            var token = GitHubAuth.LoadToken() ?? throw new InvalidOperationException("Sign in to GitHub first.");
            var api = new GitHubApi(token);
            if (create)
            {
                var info = await api.CreateRepoAsync(repo, NewRepoPrivate, "Optimus-Vibe config: device profiles, game presets and saves");
                repo = info.FullName;
                branch = string.IsNullOrWhiteSpace(info.DefaultBranch) ? "main" : info.DefaultBranch;
                Log.Info($"Created GitHub repo {repo} ({(NewRepoPrivate ? "private" : "public")})");
            }
            if (seedDir is not null)
            {
                try
                {
                    var seed = await ConfigRepoSeeder.SeedAsync(api, repo, branch, seedDir);
                    seeded = true;
                    Log.Info($"Seeded {repo} ({branch}): {seed.Paths.Count} file(s), commit {seed.CommitSha}");
                }
                catch (Exception ex) when (create)
                {
                    throw new InvalidOperationException($"{repo} was created, but seeding it failed: {ex.Message} Reload the list, pick it and choose Use this repo to retry.", ex);
                }
            }
            configRoot = SyncService.CloneMirror(SettingsStore.DataDir, repo, branch, token);
            if (!File.Exists(Path.Combine(configRoot, "devices.yaml")))
                throw new InvalidOperationException($"{repo} has no config/devices.yaml — is it an Optimus-Vibe repo?");
            SettingsStore.Update(s =>
            {
                s.SyncProvider = SyncProviders.GitHub;
                s.GitHubRepo = repo;
                s.GitHubBranch = branch;
                if (login is not null) s.GitHubLogin = login;
            });
            Log.Info($"Config repo set to GitHub {repo} ({branch}) → {configRoot}");
        }));
        if (!ok)
        {
            if (create) _ = LoadReposAsync(); // the repo may exist now even though the rest failed
            return false;
        }
        CurrentRepo = repo;
        RepoName = repo;
        Branch = branch;
        if (AppPaths.Env("CONFIG") is { Length: > 0 } env)
            _main.Notify($"Using {repo}, but OPTIMUSVIBE_CONFIG ({env}) overrides it for this session.", error: true);
        else
            _main.Notify(seeded
                ? $"Seeded {repo} with your devices and games, now using it ({branch})."
                : $"Using {repo} ({branch}) through your GitHub account.");
        await _main.ReloadAllAsync();
        if (create || seeded) _ = LoadReposAsync();
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private void CopyCode()
    {
        if (DeviceCode is null) return;
        try { Clipboard.SetText(DeviceCode.UserCode); }
        catch (Exception ex) { Log.Warn($"Clipboard: {ex.Message}"); }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error($"Opening {url} failed", ex);
            _main.Notify($"Couldn't open the browser — go to {url}", error: true);
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        NotImplementedException or NotSupportedException => "GitHub sign-in isn't available in this build yet.",
        System.Net.Http.HttpRequestException => "Couldn't reach GitHub — check the internet connection.",
        _ => ex.Message,
    };
}
