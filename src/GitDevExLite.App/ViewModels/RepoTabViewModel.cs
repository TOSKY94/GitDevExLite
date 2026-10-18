using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public partial class RepoTabViewModel : ObservableObject, IDisposable
{
    public sealed record PullRequestFilterOption(PullRequestFilterKind Value, string Label);

    private readonly IGitService _git;
    private readonly AiAssistantService _aiAssistant;
    private readonly IPullRequestService _pullRequests;
    private readonly ISecretScanner _secretScanner;
    private readonly IDialogService _dialogs;
    private readonly RemoteOptions _options;
    private CancellationTokenSource? _cts;
    private RepoWatcher? _watcher;
    private bool _disposed;
    private IReadOnlyList<CommitInfo> _commits = Array.Empty<CommitInfo>();
    private bool _suppressFileSelection;

    public RepoTabViewModel(IGitService git, AiAssistantService aiAssistant, IPullRequestService pullRequests, ISecretScanner secretScanner, IDialogService dialogs, RemoteOptions options)
    {
        _git = git;
        _aiAssistant = aiAssistant;
        _pullRequests = pullRequests;
        _secretScanner = secretScanner;
        _dialogs = dialogs;
        _options = options;
        Selection.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedLineCount));
            OnPropertyChanged(nameof(SelectionActionText));
                OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
            StageSelectedCommand.NotifyCanExecuteChanged();
                AddInlinePullRequestCommentCommand.NotifyCanExecuteChanged();
        };
        MenuProvider = BuildMenu;
    }

    public string? RepositoryPath => _git.RepositoryPath?.TrimEnd('/', '\\');

    public ObservableCollection<RefInfo> LocalBranches { get; } = new();
    public ObservableCollection<RefInfo> RemoteBranches { get; } = new();
    public ObservableCollection<RefInfo> Tags { get; } = new();
    public ObservableCollection<StashInfo> Stashes { get; } = new();
    public ObservableCollection<ChangedFile> CommitFiles { get; } = new();
    public ObservableCollection<FileChange> Unstaged { get; } = new();
    public ObservableCollection<FileChange> Staged { get; } = new();
    public ObservableCollection<PullRequestListItem> PullRequests { get; } = new();
    public ObservableCollection<PullRequestCommit> PullRequestCommits { get; } = new();
    public ObservableCollection<PullRequestStatusCheck> PullRequestStatusChecks { get; } = new();
    public ObservableCollection<PullRequestThread> PullRequestThreads { get; } = new();
    public IReadOnlyList<PullRequestFilterOption> PullRequestFilters { get; } =
    [
        new(PullRequestFilterKind.All, "All"),
        new(PullRequestFilterKind.Mine, "Mine"),
        new(PullRequestFilterKind.ReviewRequested, "Review requested")
    ];
    public LineSelection Selection { get; } = new();
    public Func<GraphRow, IReadOnlyList<ContextAction>> MenuProvider { get; }
    public int SelectedLineCount => Selection.Lines.Count;
    public string SelectionActionText => $"{(DiffStaged ? "Unstage" : "Stage")} selected lines ({SelectedLineCount})";
    public bool HasHostedRepository => HostedRepository is not null;
    public bool CanSignInToProvider => HostedRepository is not null && !IsProviderAuthenticated;
    public bool IsProviderAuthenticated => ProviderSession?.IsAuthenticated == true;
    public bool HasPullRequestDetail => PullRequestDetail is not null;
    public bool CanCreatePullRequest => ProviderCapabilities.CanCreatePullRequest && HostedRepository is not null;
    public bool CanCheckoutSelectedPullRequest => ProviderCapabilities.CanCheckoutPullRequest && SelectedPullRequest is not null;
    public bool CanCheckoutSelectedBranch => SelectedBranch is { IsHead: false } && !OperationState.InProgress;
    public bool CanReviewSelectedPullRequest => ProviderCapabilities.CanReview && SelectedPullRequest is not null;
    public bool CanMergeSelectedPullRequest => ProviderCapabilities.CanMerge && SelectedPullRequest is not null;
        public bool CanCommentSelectedPullRequestInline =>
            ProviderCapabilities.CanCommentInline &&
            SelectedPullRequest is not null &&
            !ShowWorkingDirectory &&
            ActiveDiff is not null &&
            Selection.Lines.Count > 0;
    public string ProviderSummary => HostedRepository is null
        ? "No supported PR provider detected"
        : IsProviderAuthenticated
            ? $"{HostedRepository.Provider}: signed in as {ProviderSession?.Account.DisplayName ?? HostedRepository.DisplayName}"
            : $"{HostedRepository.Provider}: sign in required";
    public string PullRequestDetailTitle => PullRequestDetail is null
        ? ""
        : $"PR #{PullRequestDetail.Summary.Number}: {PullRequestDetail.Summary.Title}";
    public string PullRequestDetailSummary => PullRequestDetail is null
        ? ""
        : $"{PullRequestDetail.Summary.Author}  {PullRequestDetail.Summary.SourceBranch} -> {PullRequestDetail.Summary.TargetBranch}  {PullRequestDetail.Summary.Status}";

    [ObservableProperty] private string _header = "(empty)";
    [ObservableProperty] private Dictionary<string, List<RefInfo>> _refsByCommit = new();
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private GraphLayoutResult? _graph;
    [ObservableProperty] private GraphRow? _selectedRow;
    [ObservableProperty] private RefInfo? _selectedBranch;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HashSet<string>? _matchedShas;
    [ObservableProperty] private string _searchSummary = "";

    [ObservableProperty] private ChangedFile? _selectedCommitFile;
    [ObservableProperty] private FileChange? _selectedUnstaged;
    [ObservableProperty] private FileChange? _selectedStaged;
    [ObservableProperty] private bool _showWorkingDirectory;
    [ObservableProperty] private HostedRepositoryId? _hostedRepository;
    [ObservableProperty] private ProviderSession? _providerSession;
    [ObservableProperty] private ProviderCapabilities _providerCapabilities = ProviderCapabilities.None;
    [ObservableProperty] private PullRequestFilterKind _pullRequestFilter = PullRequestFilterKind.All;
    [ObservableProperty] private PullRequestListItem? _selectedPullRequest;
    [ObservableProperty] private PullRequestDetail? _pullRequestDetail;

    [ObservableProperty] private FileDiff? _activeDiff;
    [ObservableProperty] private bool _diffStaged;
    [ObservableProperty] private bool _diffEditable;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private ConflictViewModel? _activeConflict;
    [ObservableProperty] private StashInfo? _selectedStash;
    [ObservableProperty] private string _detailHeading = "";
    private string? _detailSha;

    public event Action<string>? OpenRepositoryRequested;

    /// <summary>The file shown in the detail pane, for history and blame.</summary>
    public string? CurrentFilePath => ActiveConflict?.Path ?? ActiveDiff?.Path
        ?? (ShowWorkingDirectory ? (SelectedUnstaged ?? SelectedStaged)?.Path : SelectedCommitFile?.Path);

    public bool HasCurrentFile => CurrentFilePath is not null;

    [ObservableProperty] private RefInfo? _selectedTag;
    [ObservableProperty] private string _operationText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIndeterminate = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(FetchCommand), nameof(PullMergeCommand),
         nameof(PullRebaseCommand), nameof(PullFastForwardCommand), nameof(PushCommand),
         nameof(ContinueOperationCommand), nameof(SkipOperationCommand), nameof(AbortOperationCommand))]
    private bool _isBusy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasOperation), nameof(OperationBanner), nameof(CanSkipOperation))]
    private RepoOperationState _operationState = RepoOperationState.None;

    public bool HasOperation => OperationState.InProgress;
    public bool CanSkipOperation => OperationState.CanSkip;
    public string OperationBanner =>
        $"{OperationState.Describe()}. Resolve any conflicts, stage the files, then Continue \u2014 or Abort.";

    /// <summary>Set after a background fetch hit an auth failure; cleared by the next successful manual operation.</summary>
    public bool AutoFetchPaused { get; private set; }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private bool _amend;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(GenerateAiCommitMessageCommand))]
    private bool _isGeneratingAiCommitMessage;

    private bool _trustAiPreviewForSession;

    public bool CanGenerateAiCommitMessage => !IsGeneratingAiCommitMessage && (Staged.Count > 0 || Amend);
    public bool IsAiConfigured => _aiAssistant.LoadSettings().IsConfigured;

    public void Open(string path)
    {
        _git.Open(path);
        Refresh();
        StartWatching();
    }

    private void StartWatching()
    {
        if (_git.RepositoryPath is not { } work || _git.GitDirectory is not { } gitDir) return;
        try
        {
            _watcher = new RepoWatcher(work, gitDir);
            _watcher.Changed += change => Dispatcher.UIThread.Post(() => ApplyExternalChange(change));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Changes will only be picked up by Refresh and in-app actions.
        }
    }

    private void ApplyExternalChange(RepoChange change)
    {
        if (_disposed || IsBusy || _git.RepositoryPath is null) return;
        try
        {
            if (change.HasFlag(RepoChange.RepoState)) RefreshCore(keepSelection: true, includeStatus: true, quiet: true);
            else RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void Refresh() => RefreshCore(keepSelection: false, includeStatus: true);

    [RelayCommand]
    private async Task SignInProvider()
    {
        if (HostedRepository is null) return;
        try
        {
            if (HostedRepository.Provider is HostingProviderKind.GitHub or HostingProviderKind.AzureDevOps)
            {
                var credentialUrl = HostedRepository.Provider == HostingProviderKind.AzureDevOps
                    ? $"https://{HostedRepository.Host}/{HostedRepository.Owner}/{HostedRepository.Project}/_git/{HostedRepository.Name}"
                    : $"https://{HostedRepository.Host}/{HostedRepository.Owner}/{HostedRepository.Name}";
                var choice = await _dialogs.PromptCredentialsAsync(new CredentialRequest(
                    credentialUrl,
                    $"Sign in to {HostedRepository.Provider} for {HostedRepository.DisplayName} using a personal access token.",
                    false,
                    false));
                if (choice is null) return;
                if (choice.UseGcm)
                    throw new InvalidOperationException($"{HostedRepository.Provider} pull request sign-in currently requires a personal access token.");
                if (HostedRepository.Provider == HostingProviderKind.AzureDevOps)
                {
                    var organization = string.IsNullOrWhiteSpace(choice.Organization)
                        ? HostedRepository.Owner
                        : choice.Organization.Trim();
                    if (RepositoryPath is { } repositoryPath)
                        await GitCredentials.EnableHttpPathAsync(repositoryPath, credentialUrl, CancellationToken.None, organization);
                    await GitCredentials.StoreTokenAsync(credentialUrl, choice.Username ?? "PAT", choice.Token ?? "", CancellationToken.None, organization);
                }
                else
                    await GitCredentials.StoreTokenAsync("https", HostedRepository.Host, choice.Username ?? "PAT", choice.Token ?? "", CancellationToken.None);
            }

            ProviderSession = await _pullRequests.SignInAsync(HostedRepository, CancellationToken.None);
            ProviderCapabilities = _pullRequests.GetCapabilities(HostedRepository, ProviderSession);
            OnPropertyChanged(nameof(CanSignInToProvider));
            OnPropertyChanged(nameof(IsProviderAuthenticated));
            OnPropertyChanged(nameof(ProviderSummary));
            StatusText = $"Signed in to {HostedRepository.Provider}.";
            await RefreshPullRequestsAsync();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private Task RefreshPullRequests() => RefreshPullRequestsAsync();

    [RelayCommand(CanExecute = nameof(CanCreatePullRequest))]
    private async Task CreatePullRequest()
    {
        if (HostedRepository is null) return;

        var sourceBranch = CurrentBranchName;
        var targetBranches = GetPullRequestTargetBranches(sourceBranch);
        var targetBranch = targetBranches.FirstOrDefault(branch => string.Equals(branch, "main", StringComparison.OrdinalIgnoreCase))
            ?? targetBranches.FirstOrDefault(branch => string.Equals(branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? targetBranches.FirstOrDefault()
            ?? "main";

        CreatePullRequestDraft draft;
        try
        {
            draft = await _pullRequests.CreateDraftAsync(HostedRepository, sourceBranch, targetBranch, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        var form = await _dialogs.ShowPullRequestCreationAsync(
            HostedRepository,
            LocalBranches.Select(branch => branch.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            targetBranches,
            draft,
            FindPullRequestTemplates(draft.TargetBranch));
        if (form is null) return;

        var secretMatches = _secretScanner.Scan(_git, Staged.Concat(Unstaged).ToList());
        if (secretMatches.Count > 0)
        {
            var preview = string.Join("\n", secretMatches.Take(5).Select(m =>
                $"{m.Path}:{m.LineNumber} [{m.Rule}] {m.Preview}"));
            var suffix = secretMatches.Count > 5 ? $"\n...and {secretMatches.Count - 5} more" : "";
            var proceed = await _dialogs.ConfirmAsync(
                "Potential secrets detected",
                $"Potential secrets were detected in the files included in this pull request:\n\n{preview}{suffix}\n\nCreate the pull request anyway?");
            if (!proceed)
            {
                StatusText = "PR creation cancelled due to potential secrets.";
                return;
            }
        }

        try
        {
            IsBusy = true;
            var created = await _pullRequests.CreateAsync(
                HostedRepository,
                new CreatePullRequestRequest(form.Title, form.Description, form.SourceBranch, form.TargetBranch, form.IsDraft),
                CancellationToken.None);
            await RefreshPullRequestsAsync();
            SelectedPullRequest = PullRequests.FirstOrDefault(pr => pr.Id == created.Id) ?? created;
            StatusText = $"Created PR #{created.Number}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private IReadOnlyList<string> GetPullRequestTargetBranches(string sourceBranch)
    {
        var names = LocalBranches
            .Where(branch => !string.Equals(branch.Name, sourceBranch, StringComparison.OrdinalIgnoreCase))
            .Select(branch => branch.Name)
            .Concat(RemoteBranches.Select(branch =>
            {
                var slash = branch.Name.IndexOf('/');
                return slash >= 0 && slash < branch.Name.Length - 1 ? branch.Name[(slash + 1)..] : branch.Name;
            }))
            .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "HEAD", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names;
    }

    private IReadOnlyList<PullRequestTemplateOption> FindPullRequestTemplates(string targetBranch)
    {
        var repositoryPath = RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return Array.Empty<PullRequestTemplateOption>();

        var templates = new List<PullRequestTemplateOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(repositoryPath);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        void Add(string relativePath, bool isDefault = false)
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath) || !seen.Add(fullPath))
                    return;

                var content = File.ReadAllText(fullPath);
                if (string.IsNullOrWhiteSpace(content)) return;
                templates.Add(new PullRequestTemplateOption(Path.GetRelativePath(root, fullPath), content, isDefault));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        IEnumerable<string> TemplateFiles(string directory)
        {
            var fullPath = Path.Combine(root, directory);
            if (!Directory.Exists(fullPath)) return Array.Empty<string>();
            try
            {
                return Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                                   Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.GetRelativePath(root, path))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        if (HostedRepository?.Provider == HostingProviderKind.AzureDevOps)
        {
            var folders = new[] { ".azuredevops", ".vsts", "docs", "" };
            var branchParts = targetBranch.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part is not "." and not "..")
                .ToArray();
            foreach (var folder in folders)
            {
                var branchDirectory = Path.Combine(folder, "pull_request_template", "branches");
                for (var partCount = branchParts.Length; partCount > 0; partCount--)
                {
                    var branchPath = Path.Combine(branchParts.Take(partCount).ToArray());
                    Add(Path.Combine(branchDirectory, branchPath + ".md"), isDefault: templates.Count == 0);
                    if (templates.Count > 0) break;
                    Add(Path.Combine(branchDirectory, branchPath + ".txt"), isDefault: templates.Count == 0);
                    if (templates.Count > 0) break;
                }
            }

            if (!templates.Any(template => template.IsDefault))
            {
                foreach (var folder in folders)
                {
                    Add(Path.Combine(folder, "pull_request_template.md"), isDefault: true);
                    if (templates.Any(template => template.IsDefault)) break;
                    Add(Path.Combine(folder, "pull_request_template.txt"), isDefault: true);
                    if (templates.Any(template => template.IsDefault)) break;
                }
            }

            foreach (var folder in folders)
                foreach (var templatePath in TemplateFiles(Path.Combine(folder, "pull_request_template")))
                    if (!templatePath.Contains($"{Path.DirectorySeparatorChar}branches{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        Add(templatePath);
        }
        else
        {
            var defaults = new[] { ".github/pull_request_template.md", "pull_request_template.md", "docs/pull_request_template.md" };
            foreach (var templatePath in defaults)
            {
                Add(templatePath, isDefault: templates.Count == 0);
                if (templates.Any(template => template.IsDefault)) break;
            }

            foreach (var directory in new[] { ".github/PULL_REQUEST_TEMPLATE", "PULL_REQUEST_TEMPLATE", "docs/PULL_REQUEST_TEMPLATE" })
                foreach (var templatePath in TemplateFiles(directory))
                    Add(templatePath);
        }

        return templates;
    }

    [RelayCommand(CanExecute = nameof(CanCheckoutSelectedPullRequest))]
    private async Task CheckoutPullRequest()
    {
        if (HostedRepository is null || SelectedPullRequest is null) return;

        var localName = (await _dialogs.PromptAsync(
            "Check out pull request",
            "Local branch name:",
            SelectedPullRequest.SourceBranch))?.Trim();
        if (string.IsNullOrWhiteSpace(localName)) return;

        try
        {
            await RunRemoteAsync(
                $"Checkout PR #{SelectedPullRequest.Number}",
                (p, ct) => _pullRequests.CheckoutAsync(
                    HostedRepository,
                    new PullRequestCheckoutRequest(SelectedPullRequest.Id, SelectedPullRequest.SourceBranch, localName),
                    _git,
                    p,
                    ct),
                includeStatus: true);
            StatusText = $"Checked out PR #{SelectedPullRequest.Number} to '{localName}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReviewSelectedPullRequest))]
    private Task ApprovePullRequest() => SubmitPullRequestReviewAsync(PullRequestReviewDecision.Approve, "Approve pull request", "Approval comment (optional):");

    [RelayCommand(CanExecute = nameof(CanReviewSelectedPullRequest))]
    private Task RequestPullRequestChanges() => SubmitPullRequestReviewAsync(PullRequestReviewDecision.RequestChanges, "Request changes", "Review comment:");

    [RelayCommand(CanExecute = nameof(CanReviewSelectedPullRequest))]
    private Task CommentOnPullRequest() => SubmitPullRequestReviewAsync(PullRequestReviewDecision.Comment, "Comment on pull request", "Comment:");

    [RelayCommand(CanExecute = nameof(CanMergeSelectedPullRequest))]
    private async Task MergePullRequest()
    {
        if (HostedRepository is not { } repository || SelectedPullRequest is not { } pullRequest) return;

        var strategy = await _dialogs.ChoosePullRequestMergeStrategyAsync(ProviderCapabilities.SupportedMergeStrategies);
        if (strategy is null) return;
        if (!ProviderCapabilities.SupportedMergeStrategies.Contains(strategy.Value))
        {
            StatusText = $"The selected provider does not support {strategy.Value}.";
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            "Merge pull request",
            $"Merge PR #{pullRequest.Number} '{pullRequest.Title}' using {strategy.Value}?");
        if (!confirmed) return;

        try
        {
            IsBusy = true;
            await _pullRequests.MergeAsync(
                repository,
                pullRequest.Id,
                new PullRequestMergeRequest(strategy.Value, DeleteSourceBranch: false),
                CancellationToken.None);
            await RefreshPullRequestsAsync();
            var refreshedPullRequest = PullRequests.FirstOrDefault(pr => pr.Id == pullRequest.Id);
            if (refreshedPullRequest is not null)
            {
                SelectedPullRequest = refreshedPullRequest;
                await LoadPullRequestDetailAsync(refreshedPullRequest);
            }
            else
            {
                SelectedPullRequest = null;
                ClearPullRequestDetail();
            }
            StatusText = $"Merged PR #{pullRequest.Number} using {strategy.Value}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommentSelectedPullRequestInline))]
    private async Task AddInlinePullRequestComment()
    {
        if (HostedRepository is null || SelectedPullRequest is null || ActiveDiff is null || Selection.Lines.Count == 0)
            return;

        var anchor = BuildSelectedPullRequestAnchor(ActiveDiff, Selection.Lines);
        if (anchor is null)
        {
            StatusText = "Select changed diff lines before adding an inline comment.";
            return;
        }

        var body = await _dialogs.PromptAsync("Inline pull request comment", "Comment:", "");
        if (string.IsNullOrWhiteSpace(body)) return;

        try
        {
            IsBusy = true;
            await _pullRequests.AddInlineCommentAsync(
                HostedRepository,
                SelectedPullRequest.Id,
                new PullRequestCommentDraft(anchor, body.Trim()),
                CancellationToken.None);
            await LoadPullRequestDetailAsync(SelectedPullRequest);
            Selection.Clear();
            StatusText = $"Added an inline comment to PR #{SelectedPullRequest.Number}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCore(bool keepSelection, bool includeStatus, bool quiet = false)
    {
        if (_git.RepositoryPath is null) return;

        var keepSha = keepSelection && !ShowWorkingDirectory ? SelectedRow?.Commit.Sha : null;

        var refs = _git.GetRefs();
        Replace(LocalBranches, refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.Name));
        Replace(RemoteBranches, refs.Where(r => r.Kind == RefKind.RemoteBranch).OrderBy(r => r.Name));
        Replace(Tags, refs.Where(r => r.Kind == RefKind.Tag).OrderByDescending(r => r.Name));
        OnPropertyChanged(nameof(CurrentBranchLabel));
        OnPropertyChanged(nameof(CurrentBranchSync));
        Replace(Stashes, _git.GetStashes());
        RefsByCommit = refs.GroupBy(r => r.TargetSha).ToDictionary(g => g.Key, g => g.ToList());
        RefreshPullRequestContext();

        _commits = _git.GetCommits();
        Graph = GraphLayout.Build(_commits);
        SelectedRow = keepSha is null ? null : Graph.Rows.FirstOrDefault(r => r.Commit.Sha == keepSha);
        ApplySearch();
        if (includeStatus) RefreshStatus();
        OperationState = _git.GetOperationState();

        Header = new DirectoryInfo(RepositoryPath!).Name;
        if (!quiet) StatusText = $"{_commits.Count} commits, {Graph.LaneCount} lanes";
        OnPropertyChanged(nameof(RepositoryPath));
    }

    private void RefreshPullRequestContext()
    {
        HostedRepository = _pullRequests.ResolveRepository(_git);
        ProviderSession = HostedRepository is null ? null : _pullRequests.GetSession(HostedRepository);
        ProviderCapabilities = HostedRepository is null
            ? ProviderCapabilities.None
            : _pullRequests.GetCapabilities(HostedRepository, ProviderSession);
        OnPropertyChanged(nameof(HasHostedRepository));
        OnPropertyChanged(nameof(CanSignInToProvider));
        OnPropertyChanged(nameof(IsProviderAuthenticated));
        OnPropertyChanged(nameof(ProviderSummary));

        if (!ProviderCapabilities.CanListPullRequests)
        {
            PullRequests.Clear();
            SelectedPullRequest = null;
        }
    }

    partial void OnProviderSessionChanged(ProviderSession? value)
    {
        OnPropertyChanged(nameof(CanSignInToProvider));
        OnPropertyChanged(nameof(IsProviderAuthenticated));
        OnPropertyChanged(nameof(ProviderSummary));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        NotifyPullRequestActionStateChanged();
    }

    partial void OnProviderCapabilitiesChanged(ProviderCapabilities value)
    {
        OnPropertyChanged(nameof(CanCreatePullRequest));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        NotifyPullRequestActionStateChanged();
    }

    partial void OnHostedRepositoryChanged(HostedRepositoryId? value)
    {
        OnPropertyChanged(nameof(HasHostedRepository));
        OnPropertyChanged(nameof(CanSignInToProvider));
        OnPropertyChanged(nameof(ProviderSummary));
        OnPropertyChanged(nameof(CanCreatePullRequest));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        NotifyPullRequestActionStateChanged();
    }

    partial void OnPullRequestFilterChanged(PullRequestFilterKind value)
    {
        if (ProviderCapabilities.CanListPullRequests)
            _ = RefreshPullRequestsAsync();
    }

    // ---- search -------------------------------------------------------------

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        MatchedShas = GraphSearch.Match(_commits, RefsByCommit, SearchText);
        SearchSummary = MatchedShas is null ? "" : $"{MatchedShas.Count} match(es)";
    }

    [RelayCommand]
    private void NextMatch() => StepMatch(1);

    [RelayCommand]
    private void PreviousMatch() => StepMatch(-1);

    private void StepMatch(int direction)
    {
        if (Graph is null || MatchedShas is not { Count: > 0 }) return;
        var rows = Graph.Rows;
        var start = SelectedRow is null ? (direction > 0 ? -1 : rows.Count) : rows.ToList().IndexOf(SelectedRow);
        for (var step = 1; step <= rows.Count; step++)
        {
            var i = ((start + direction * step) % rows.Count + rows.Count) % rows.Count;
            if (MatchedShas.Contains(rows[i].Commit.Sha))
            {
                SelectedRow = rows[i];
                return;
            }
        }
    }

    // ---- commit details -----------------------------------------------------

    partial void OnSelectedRowChanged(GraphRow? value)
    {
        ClearPullRequestDetail();
        CommitFiles.Clear();
        DetailHeading = "";
        _detailSha = value?.Commit.Sha;
        if (value is null) { SelectedCommitFile = null; return; }

        SelectedStash = null;
        ShowWorkingDirectory = false;
        LoadDetailFiles(value.Commit.Sha);
    }

    partial void OnSelectedStashChanged(StashInfo? value)
    {
        if (value is null) return;
        ClearPullRequestDetail();
        SelectedRow = null;
        ShowWorkingDirectory = false;
        _detailSha = value.Sha;
        DetailHeading = $"Stash: {value.Message}";
        LoadDetailFiles(value.Sha);
    }

    partial void OnSelectedPullRequestChanged(PullRequestListItem? value)
    {
        NotifyPullRequestActionStateChanged();
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        if (value is null)
        {
            ClearPullRequestDetail();
            return;
        }
        SelectedRow = null;
        SelectedStash = null;
        ShowWorkingDirectory = false;
        _detailSha = null;
        PullRequestDetail = null;
        PullRequestCommits.Clear();
        PullRequestStatusChecks.Clear();
        PullRequestThreads.Clear();
        DetailHeading = $"PR #{value.Number}: {value.Title}";
        CommitFiles.Clear();
        ActiveDiff = null;
        ActiveConflict = null;
        _ = LoadPullRequestDetailAsync(value);
    }

    public bool ShowCommitMode => !ShowWorkingDirectory && !HasPullRequestDetail;

    partial void OnPullRequestDetailChanged(PullRequestDetail? value)
    {
        OnPropertyChanged(nameof(ShowCommitMode));
        OnPropertyChanged(nameof(HasPullRequestDetail));
        OnPropertyChanged(nameof(PullRequestDetailTitle));
        OnPropertyChanged(nameof(PullRequestDetailSummary));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
    }

    private async Task RefreshPullRequestsAsync()
    {
        if (HostedRepository is null)
        {
            PullRequests.Clear();
            SelectedPullRequest = null;
            ClearPullRequestDetail();
            return;
        }

        ProviderSession = _pullRequests.GetSession(HostedRepository);
        ProviderCapabilities = _pullRequests.GetCapabilities(HostedRepository, ProviderSession);
        OnPropertyChanged(nameof(CanSignInToProvider));
        OnPropertyChanged(nameof(IsProviderAuthenticated));
        OnPropertyChanged(nameof(ProviderSummary));

        if (!ProviderCapabilities.CanListPullRequests)
        {
            PullRequests.Clear();
            SelectedPullRequest = null;
            ClearPullRequestDetail();
            return;
        }

        try
        {
            IsBusy = true;
            var items = await _pullRequests.ListAsync(HostedRepository, PullRequestFilter, CancellationToken.None);
            Replace(PullRequests, items.OrderByDescending(pr => pr.UpdatedAt));
            if (SelectedPullRequest is { } current)
                SelectedPullRequest = PullRequests.FirstOrDefault(pr => pr.Id == current.Id) ?? PullRequests.FirstOrDefault();
            else
                SelectedPullRequest = PullRequests.FirstOrDefault();
            StatusText = PullRequests.Count == 0 ? "No pull requests found." : $"Loaded {PullRequests.Count} pull request(s).";
        }
        catch (NotSupportedException ex)
        {
            PullRequests.Clear();
            SelectedPullRequest = null;
            ClearPullRequestDetail();
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPullRequestDetailAsync(PullRequestListItem selected)
    {
        if (HostedRepository is null)
        {
            ClearPullRequestDetail();
            return;
        }

        try
        {
            IsBusy = true;
            var detail = await _pullRequests.GetDetailAsync(HostedRepository, selected.Id, CancellationToken.None);
            if (SelectedPullRequest?.Id != selected.Id) return;
            PullRequestDetail = detail;
            Replace(CommitFiles, detail.Files);
            Replace(PullRequestCommits, detail.Commits);
            Replace(PullRequestStatusChecks, detail.StatusChecks);
            Replace(PullRequestThreads, detail.Threads);
            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (SelectedPullRequest?.Id == selected.Id)
            {
                ClearPullRequestDetail();
                StatusText = ex.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearPullRequestDetail()
    {
        PullRequestDetail = null;
        PullRequestCommits.Clear();
        PullRequestStatusChecks.Clear();
        PullRequestThreads.Clear();
    }

    private async Task SubmitPullRequestReviewAsync(PullRequestReviewDecision decision, string title, string label)
    {
        if (HostedRepository is null || SelectedPullRequest is null) return;

        var body = await _dialogs.PromptAsync(title, label, "");
        if (body is null) return;

        try
        {
            IsBusy = true;
            await _pullRequests.SubmitReviewAsync(
                HostedRepository,
                SelectedPullRequest.Id,
                new PullRequestReviewRequest(decision, body),
                CancellationToken.None);
            await LoadPullRequestDetailAsync(SelectedPullRequest);
            StatusText = decision switch
            {
                PullRequestReviewDecision.Approve => $"Approved PR #{SelectedPullRequest.Number}.",
                PullRequestReviewDecision.RequestChanges => $"Requested changes on PR #{SelectedPullRequest.Number}.",
                _ => $"Commented on PR #{SelectedPullRequest.Number}."
            };
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyPullRequestActionStateChanged()
    {
        OnPropertyChanged(nameof(CanCreatePullRequest));
        OnPropertyChanged(nameof(CanCheckoutSelectedPullRequest));
        OnPropertyChanged(nameof(CanReviewSelectedPullRequest));
        OnPropertyChanged(nameof(CanMergeSelectedPullRequest));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        CreatePullRequestCommand.NotifyCanExecuteChanged();
        CheckoutPullRequestCommand.NotifyCanExecuteChanged();
        ApprovePullRequestCommand.NotifyCanExecuteChanged();
        RequestPullRequestChangesCommand.NotifyCanExecuteChanged();
        CommentOnPullRequestCommand.NotifyCanExecuteChanged();
        MergePullRequestCommand.NotifyCanExecuteChanged();
        AddInlinePullRequestCommentCommand.NotifyCanExecuteChanged();
    }

    // A stash is stored as a commit, so its files and diffs reuse the commit machinery.
    private void LoadDetailFiles(string sha)
    {
        try
        {
            Replace(CommitFiles, _git.GetCommitChanges(sha));
            SelectedCommitFile = CommitFiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(ChangedFile? value)
    {
        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        if (ShowWorkingDirectory) return;
        if (value is null) { ActiveDiff = null; return; }
        if (PullRequestDetail is not null)
        {
            if (string.IsNullOrWhiteSpace(value.Patch))
            {
                ActiveDiff = null;
                DiffEditable = false;
                DiffStaged = false;
                StatusText = $"A diff is unavailable for '{value.Path}'.";
                return;
            }

            ActiveDiff = PatchParser.Parse(value.Path, value.Patch, value.Status == "A", value.Status == "D");
            DiffEditable = false;
            DiffStaged = false;
            return;
        }
        if (_detailSha is null) { ActiveDiff = null; return; }
        try
        {
            ActiveDiff = _git.GetCommitDiff(_detailSha, value.Path);
            DiffEditable = false;
            DiffStaged = false;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ---- working directory --------------------------------------------------

    partial void OnShowWorkingDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCommitMode));
        ActiveDiff = null;
        ActiveConflict = null;
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
        if (value)
        {
            RefreshStatus();
        }
        else if (SelectedCommitFile is { } f)
        {
            OnSelectedCommitFileChanged(f);
        }
    }

    partial void OnAmendChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(CommitMessage))
            CommitMessage = _git.GetHeadMessage() ?? "";
    }

    partial void OnSelectedUnstagedChanged(FileChange? value)
    {
        if (_suppressFileSelection || value is null) return;
        _suppressFileSelection = true;
        SelectedStaged = null;
        _suppressFileSelection = false;
        LoadWorkingDiff(value);
    }

    partial void OnSelectedStagedChanged(FileChange? value)
    {
        if (_suppressFileSelection || value is null) return;
        _suppressFileSelection = true;
        SelectedUnstaged = null;
        _suppressFileSelection = false;
        LoadWorkingDiff(value);
    }

    partial void OnActiveDiffChanged(FileDiff? value)
    {
        Selection.Clear();
        OnPropertyChanged(nameof(HasCurrentFile));
        OnPropertyChanged(nameof(CanCommentSelectedPullRequestInline));
    }

    partial void OnActiveConflictChanged(ConflictViewModel? value) => OnPropertyChanged(nameof(HasCurrentFile));

    private void LoadWorkingDiff(FileChange file)
    {
        if (file is { Status: "Conflicted", IsStaged: false })
        {
            ActiveDiff = null;
            DiffEditable = false;
            // Keep a half-resolved result when a refresh re-selects the same file.
            if (ActiveConflict?.Path != file.Path) _ = LoadConflictAsync(file);
            return;
        }

        ActiveConflict = null;
        try
        {
            ActiveDiff = _git.GetWorkingDiff(file.Path, file.IsStaged);
            DiffStaged = file.IsStaged;
            DiffEditable = true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void RefreshStatus(string? keepPath = null, bool keepStaged = false)
    {
        if (_git.RepositoryPath is null) return;

        var current = SelectedUnstaged ?? SelectedStaged;
        keepPath ??= current?.Path;
        if (current is not null && keepPath == current.Path) keepStaged = current.IsStaged;

        var status = _git.GetStatus();
        var newUnstaged = status.Where(s => !s.IsStaged).ToList();
        var newStaged = status.Where(s => s.IsStaged).ToList();
        UpdateSummary(status.Count);

        // Nothing structural changed (typical for an external edit to an already-listed file): keep the
        // selection and ticked lines, and only swap the diff if its content differs.
        if (newUnstaged.SequenceEqual(Unstaged) && newStaged.SequenceEqual(Staged))
        {
            ReloadDiffIfChanged();
            return;
        }

        _suppressFileSelection = true;
        Replace(Unstaged, newUnstaged);
        Replace(Staged, newStaged);
        SelectedUnstaged = null;
        SelectedStaged = null;

        FileChange? target = null;
        if (keepPath is not null)
        {
            var preferred = keepStaged ? Staged : Unstaged;
            var other = keepStaged ? Unstaged : Staged;
            target = preferred.FirstOrDefault(f => f.Path == keepPath) ?? other.FirstOrDefault(f => f.Path == keepPath);
        }
        if (target is not null)
        {
            if (target.IsStaged) SelectedStaged = target; else SelectedUnstaged = target;
        }
        _suppressFileSelection = false;

        if (target is not null && ShowWorkingDirectory) LoadWorkingDiff(target);
        else if (ShowWorkingDirectory)
        {
            ActiveDiff = null;
            ActiveConflict = null;
        }

        CommitCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSummary(int changeCount)
    {
        CommitCommand.NotifyCanExecuteChanged();
        WorkingTreeSummary = changeCount == 0 ? "Working directory clean" : $"{changeCount} change(s)";
    }

    private void ReloadDiffIfChanged()
    {
        if (!ShowWorkingDirectory || (SelectedUnstaged ?? SelectedStaged) is not { } file) return;
        if (file is { Status: "Conflicted" }) return;
        try
        {
            var diff = _git.GetWorkingDiff(file.Path, file.IsStaged);
            if (ActiveDiff is { } shown && SameDiff(shown, diff)) return;
            ActiveDiff = diff;
            DiffStaged = file.IsStaged;
            DiffEditable = true;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static bool SameDiff(FileDiff a, FileDiff b) =>
        a.Path == b.Path && a.IsBinary == b.IsBinary && a.Hunks.Count == b.Hunks.Count &&
        a.Hunks.Zip(b.Hunks).All(p => p.First.Header == p.Second.Header && p.First.Lines.SequenceEqual(p.Second.Lines));

    [ObservableProperty] private string _workingTreeSummary = "";

    [RelayCommand]
    private void StageFile(FileChange? file) => Run(() => { if (file is not null) _git.Stage(file.Path); }, refreshStatusOnly: true);

    [RelayCommand]
    private void UnstageFile(FileChange? file) => Run(() => { if (file is not null) _git.Unstage(file.Path); }, refreshStatusOnly: true);

    [RelayCommand]
    private void StageAll() => Run(() =>
    {
        foreach (var f in Unstaged.ToList()) _git.Stage(f.Path);
    }, refreshStatusOnly: true);

    [RelayCommand]
    private void UnstageAll() => Run(() =>
    {
        foreach (var f in Staged.ToList()) _git.Unstage(f.Path);
    }, refreshStatusOnly: true);

    [RelayCommand]
    private void ApplyHunk(int hunk)
    {
        if (ActiveDiff is null || hunk < 0 || hunk >= ActiveDiff.Hunks.Count) return;
        ApplyLines(PatchBuilder.HunkChanges(ActiveDiff, hunk));
    }

    [RelayCommand(CanExecute = nameof(CanStageSelected))]
    private void StageSelected() => ApplyLines(new HashSet<LineRef>(Selection.Lines));

    private bool CanStageSelected() => DiffEditable && Selection.Lines.Count > 0;

    partial void OnDiffEditableChanged(bool value) => StageSelectedCommand.NotifyCanExecuteChanged();

    partial void OnDiffStagedChanged(bool value) => OnPropertyChanged(nameof(SelectionActionText));

    private static PullRequestCommentAnchor? BuildSelectedPullRequestAnchor(FileDiff diff, IReadOnlySet<LineRef> selection)
    {
        var selectedLines = selection
            .Select(lineRef => TryGetDiffLine(diff, lineRef))
            .Where(line => line is not null && line.Kind is DiffLineKind.Added or DiffLineKind.Removed)
            .Select(line => line!)
            .ToList();
        if (selectedLines.Count == 0)
            return null;

        var hasAdded = selectedLines.Any(line => line.Kind == DiffLineKind.Added);
        var hasRemoved = selectedLines.Any(line => line.Kind == DiffLineKind.Removed);
        if (hasAdded && hasRemoved)
            return null;

        var side = hasRemoved
            ? PullRequestCommentSide.Left
            : PullRequestCommentSide.Right;
        var lineNumbers = selectedLines
            .Select(line => side == PullRequestCommentSide.Left ? line.OldNo : line.NewNo)
            .Where(lineNumber => lineNumber is not null)
            .Select(lineNumber => lineNumber!.Value)
            .Distinct()
            .OrderBy(lineNumber => lineNumber)
            .ToArray();
        if (lineNumbers.Length == 0 || lineNumbers.Zip(lineNumbers.Skip(1)).Any(pair => pair.Second != pair.First + 1))
            return null;

        return new PullRequestCommentAnchor(
            diff.Path,
            lineNumbers[0],
            lineNumbers[^1],
            side,
            side == PullRequestCommentSide.Left ? lineNumbers[0] : null,
            side == PullRequestCommentSide.Left ? lineNumbers[^1] : null);
    }

    private static DiffLine? TryGetDiffLine(FileDiff diff, LineRef lineRef)
    {
        if (lineRef.Hunk < 0 || lineRef.Hunk >= diff.Hunks.Count)
            return null;

        var hunk = diff.Hunks[lineRef.Hunk];
        if (lineRef.Line < 0 || lineRef.Line >= hunk.Lines.Count)
            return null;

        return hunk.Lines[lineRef.Line];
    }

    private void ApplyLines(IReadOnlySet<LineRef> lines)
    {
        if (ActiveDiff is null || lines.Count == 0) return;
        var diff = ActiveDiff;
        var staged = DiffStaged;
        try
        {
            if (staged) _git.UnstageLines(diff, lines); else _git.StageLines(diff, lines);
            RefreshStatus(diff.Path, staged);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        try
        {
            _git.Commit(CommitMessage.Trim(), Amend);
            CommitMessage = "";
            Amend = false;
            Refresh();
            StatusText = "Committed.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanCommit() => !string.IsNullOrWhiteSpace(CommitMessage) && (Staged.Count > 0 || Amend);

    [RelayCommand(CanExecute = nameof(CanGenerateAiCommitMessage))]
    private async Task GenerateAiCommitMessage()
    {
        try
        {
            IsGeneratingAiCommitMessage = true;
            if (!await EnsureAiProviderConfiguredAsync())
            {
                StatusText = "AI provider setup cancelled.";
                return;
            }

            StatusText = "Generating AI commit draft...";

            var draft = await _aiAssistant.GenerateCommitMessageAsync(_git, Amend, CancellationToken.None);
            if (!_trustAiPreviewForSession)
            {
                var preview = await _dialogs.ShowAiPreviewAsync(
                    "AI commit draft",
                    $"{draft.ProviderName} will receive the staged diff excerpt below. Nothing will be committed until you confirm.",
                    draft.Preview.Title,
                    draft.Preview.Body,
                    draft.FullMessage);
                if (!preview.Accepted)
                {
                    StatusText = "AI draft cancelled.";
                    return;
                }

                _trustAiPreviewForSession = preview.RememberDecision;
            }

            CommitMessage = draft.FullMessage;
            StatusText = "AI draft applied to commit message.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsGeneratingAiCommitMessage = false;
        }
    }

    [RelayCommand]
    private async Task ConfigureAi()
    {
        try
        {
            if (await ShowAiProviderSettingsAsync())
            {
                OnPropertyChanged(nameof(IsAiConfigured));
                StatusText = "AI provider settings saved.";
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task<bool> EnsureAiProviderConfiguredAsync()
    {
        if (_aiAssistant.LoadSettings().IsConfigured)
            return true;

        return await ShowAiProviderSettingsAsync();
    }

    private async Task<bool> ShowAiProviderSettingsAsync()
    {
        var current = _aiAssistant.LoadSettings();
        var result = await _dialogs.ShowAiProviderSettingsAsync(_aiAssistant.GetAvailableProviders(), current);
        if (result is null)
            return false;

        var settings = new AiProviderSettings(
            result.Provider,
            result.Endpoint,
            result.Model,
            result.SecretKey);

        if (settings.Kind != AiProviderKind.Disabled &&
            (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.Model)))
            throw new InvalidOperationException("AI endpoint and model are required.");

        _aiAssistant.SaveSettings(settings);

        if (!string.IsNullOrWhiteSpace(result.ApiKey))
        {
            var store = new GitCredentialAiKeyStore();
            await store.SaveApiKeyAsync(settings, result.ApiKey.Trim(), CancellationToken.None);
        }

        if (result.TestConnection)
            await _aiAssistant.TestConnectionAsync(settings, CancellationToken.None);

        OnPropertyChanged(nameof(IsAiConfigured));
        return true;
    }

    [RelayCommand]
    private async Task TestAiConnection()
    {
        try
        {
            await _aiAssistant.TestConnectionAsync(CancellationToken.None);
            StatusText = "AI connection test succeeded.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    // ---- branches -----------------------------------------------------------

    partial void OnSelectedBranchChanged(RefInfo? value)
    {
        OnPropertyChanged(nameof(CanCheckoutSelectedBranch));
        CheckoutCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheckoutSelectedBranch))]
    private void Checkout()
    {
        if (SelectedBranch is not { } branch) return;
        Run(() =>
        {
            if (branch.Kind == RefKind.RemoteBranch)
                _git.CheckoutRemoteBranch(branch.Name);
            else if (branch.Kind == RefKind.LocalBranch)
                _git.Checkout(branch.Name);
        });
    }

    private IReadOnlyList<ContextAction> BuildMenu(GraphRow row)
    {
        var sha = row.Commit.Sha;
        var actions = new List<ContextAction>
        {
            new("Create branch here...", () => CreateBranchAsync(sha, checkout: false)),
            new("Create branch here and check out...", () => CreateBranchAsync(sha, checkout: true)),
            new("Check out this commit (detached)", () => RunAsync(() => _git.CheckoutCommit(sha))),
        };

        if (RefsByCommit.TryGetValue(sha, out var refs))
        {
            foreach (var b in refs.Where(r => r.Kind == RefKind.LocalBranch).OrderBy(r => r.Name))
            {
                var branch = b;
                if (!branch.IsHead)
                    actions.Add(new($"Check out '{branch.Name}'", () => RunAsync(() => _git.Checkout(branch.Name))));
                actions.Add(new($"Rename '{branch.Name}'...", () => RenameBranchAsync(branch)));
                actions.Add(new($"Set upstream for '{branch.Name}'...", () => SetUpstreamAsync(branch)));
                if (!branch.IsHead)
                    actions.Add(new($"Delete '{branch.Name}'...", () => DeleteBranchAsync(branch)));
            }
        }

        actions.Add(new("Create tag here...", () => CreateTagAsync(sha)));

        if (!OperationState.InProgress) AddHistoryActions(actions, sha, refs);

        if (refs is not null)
        {
            foreach (var t in refs.Where(r => r.Kind == RefKind.Tag).OrderBy(r => r.Name))
            {
                var tag = t.Name;
                actions.Add(new($"Push tag '{tag}'", () => PushTagAsync(tag)));
                actions.Add(new($"Delete tag '{tag}'...", () => DeleteTagAsync(tag, remote: false)));
                actions.Add(new($"Delete tag '{tag}' from remote...", () => DeleteTagAsync(tag, remote: true)));
            }
        }

        actions.Add(new("Copy SHA", () => _dialogs.CopyTextAsync(sha)));
        return actions;
    }

    // ---- merge, rebase, cherry-pick, revert, reset ---------------------------

    private string CurrentBranchName => LocalBranches.FirstOrDefault(b => b.IsHead)?.Name ?? "HEAD";

    public string CurrentBranchLabel => LocalBranches.FirstOrDefault(b => b.IsHead)?.Name ?? "HEAD (detached)";
    public string CurrentBranchSync => LocalBranches.FirstOrDefault(b => b.IsHead)?.AheadBehindText ?? "";

    private void AddHistoryActions(List<ContextAction> actions, string sha, List<RefInfo>? refs)
    {
        var current = CurrentBranchName;
        var headSha = LocalBranches.FirstOrDefault(b => b.IsHead)?.TargetSha;
        var isHead = sha == headSha;

        foreach (var b in (refs ?? new()).Where(r => r.Kind != RefKind.Tag && !r.IsHead).OrderBy(r => r.Name))
        {
            var target = b.Name;
            actions.Add(new($"Merge '{target}' into '{current}'...", () => MergeAsync(target, MergeMode.Default)));
            actions.Add(new($"Merge '{target}' into '{current}' (squash)...", () => MergeAsync(target, MergeMode.Squash)));
        }

        if (!isHead)
        {
            actions.Add(new($"Rebase '{current}' onto this commit...", () => RebaseOntoAsync(sha)));
            actions.Add(new("Interactive rebase of the commits after this one...", () => InteractiveRebaseFromAsync(sha)));
            actions.Add(new("Cherry-pick this commit", () => RunOperationAsync(
                "Cherry-pick", (p, ct) => _git.CherryPickAsync(new[] { sha }, p, ct))));
        }

        actions.Add(new("Revert this commit...", () => RevertAsync(sha)));
        foreach (var mode in new[] { ResetMode.Soft, ResetMode.Mixed, ResetMode.Hard })
        {
            var m = mode;
            actions.Add(new($"Reset '{current}' to here ({m.ToString().ToLowerInvariant()})...", () => ResetAsync(sha, m)));
        }
    }

    // ---- stash, undo, history, tools ------------------------------------------

    [RelayCommand]
    private async Task Stash()
    {
        var message = await _dialogs.PromptAsync("Stash changes", "Message (optional):");
        if (message is null) return;
        var untracked = await _dialogs.ConfirmAsync("Stash changes", "Also stash untracked files?");
        await RunRemoteAsync("Stash", async (_, ct) => await _git.CreateStashAsync(message, untracked, ct), includeStatus: true);
    }

    private async Task ApplyStashAsync(StashInfo stash, bool drop)
    {
        GitOpResult? result = null;
        await RunRemoteAsync(drop ? "Pop stash" : "Apply stash",
            async (_, ct) => result = await _git.ApplyStashAsync(stash.Sha, drop, ct), includeStatus: true);
        if (result is { Success: false })
        {
            ShowWorkingDirectory = true;
            StatusText = "The stash conflicts with your changes. Resolve the files, then stage them.";
        }
    }

    [RelayCommand]
    private Task ApplySelectedStash() => SelectedStash is { } s ? ApplyStashAsync(s, drop: false) : Task.CompletedTask;

    [RelayCommand]
    private Task PopSelectedStash() => SelectedStash is { } s ? ApplyStashAsync(s, drop: true) : Task.CompletedTask;

    [RelayCommand]
    private async Task DropSelectedStash()
    {
        if (SelectedStash is not { } s) return;
        if (!await _dialogs.ConfirmAsync("Drop stash", $"Delete '{s.Message}'? Its changes cannot be recovered afterwards.")) return;
        await RunRemoteAsync("Drop stash", (_, ct) => _git.DropStashAsync(s.Sha, ct), includeStatus: false);
    }

    [RelayCommand]
    private async Task Undo()
    {
        var plan = _git.GetUndoPlan();
        if (plan is null)
        {
            StatusText = "There is nothing to undo.";
            return;
        }
        if (!await _dialogs.ConfirmAsync("Undo last operation",
                $"Undo '{plan.Description}'?\n\n{plan.Action}\n\nUndoing again redoes it.")) return;
        await RunRemoteAsync("Undo", (_, ct) => _git.UndoAsync(ct), includeStatus: true);
    }

    [RelayCommand]
    private Task ShowHistory() => OpenHistoryAsync(startOnBlame: false);

    [RelayCommand]
    private Task ShowBlame() => OpenHistoryAsync(startOnBlame: true);

    private async Task OpenHistoryAsync(bool startOnBlame)
    {
        if (CurrentFilePath is not { } path) return;
        FileHistoryViewModel history;
        try
        {
            history = new FileHistoryViewModel(_git, path, GoToCommit);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }
        await _dialogs.ShowFileHistoryAsync(history, startOnBlame);
    }

    private void GoToCommit(string sha)
    {
        var row = Graph?.Rows.FirstOrDefault(r => r.Commit.Sha == sha);
        if (row is null)
        {
            StatusText = "That commit is not in the loaded history.";
            return;
        }
        ShowWorkingDirectory = false;
        SelectedRow = row;
    }

    [RelayCommand]
    private async Task OpenTools()
    {
        var tools = new RepoToolsViewModel(_git, _dialogs, path => OpenRepositoryRequested?.Invoke(path));
        await _dialogs.ShowRepoToolsAsync(tools);
        TryRefresh(includeStatus: true);
    }

    // ---- conflicts and interactive rebase ------------------------------------

    private (string Ours, string Theirs) ConflictLabels() => OperationState.Kind switch
    {
        RepoOperation.Rebase => ("Ours \u2014 the branch being rebased onto", "Theirs \u2014 your commit being replayed"),
        RepoOperation.Merge => ("Ours \u2014 the current branch", "Theirs \u2014 the incoming branch"),
        _ => ("Ours \u2014 the current branch", "Theirs \u2014 the commit being applied")
    };

    private async Task LoadConflictAsync(FileChange file)
    {
        try
        {
            var data = await _git.GetConflictFileAsync(file.Path, CancellationToken.None);
            // The user may have picked another file while this one was loading.
            if ((SelectedUnstaged ?? SelectedStaged)?.Path != file.Path) return;

            var (ours, theirs) = ConflictLabels();
            ActiveConflict = new ConflictViewModel(
                data, ours, theirs, SaveResolutionAsync, TakeSideAsync,
                message => _dialogs.ConfirmAsync("Conflict markers remain", message));
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private Task SaveResolutionAsync(string path, string content) =>
        ResolveAsync(path, () => _git.ResolveFileAsync(path, content, CancellationToken.None));

    private Task TakeSideAsync(ConflictSide side) =>
        ActiveConflict is { } c
            ? ResolveAsync(c.Path, () => _git.ResolveWithSideAsync(c.Path, side, CancellationToken.None))
            : Task.CompletedTask;

    private async Task ResolveAsync(string path, Func<Task> resolve)
    {
        try
        {
            await resolve();
            ActiveConflict = null;
            ActiveDiff = null;
            SelectedUnstaged = null;
            RefreshStatus();
            OperationState = _git.GetOperationState();

            var next = Unstaged.FirstOrDefault(f => f.Status == "Conflicted");
            if (next is not null) SelectedUnstaged = next;
            StatusText = next is null
                ? $"Resolved {path}. No conflicts left" + (HasOperation ? " \u2014 press Continue." : ".")
                : $"Resolved {path}.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private async Task InteractiveRebaseFromAsync(string sha)
    {
        IReadOnlyList<RebaseEntry> entries;
        try
        {
            entries = _git.GetRebaseEntries(sha);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        var request = await _dialogs.ShowInteractiveRebaseAsync(entries, $"{CurrentBranchName} onto {sha[..7]}");
        if (request is null) return;
        await RunOperationAsync("Interactive rebase", (p, ct) => _git.InteractiveRebaseAsync(sha, request, p, ct));
    }

    private async Task MergeAsync(string target, MergeMode mode)
    {
        var current = CurrentBranchName;
        MergePreview preview;
        try
        {
            preview = await _git.PreviewMergeAsync(target, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        if (preview.IsUpToDate)
        {
            StatusText = $"'{current}' already contains '{target}'.";
            return;
        }

        var summary = !preview.CanPreview ? preview.Message ?? ""
            : preview.IsFastForward ? "This can be fast-forwarded."
            : preview.IsClean ? "No conflicts expected."
            : $"{preview.ConflictedFiles.Count} file(s) would conflict:\n" + ListFiles(preview.ConflictedFiles);
        if (mode == MergeMode.Squash) summary += "\nThe changes are staged, not committed.";

        if (!await _dialogs.ConfirmAsync("Merge", $"Merge '{target}' into '{current}'?\n\n{summary}")) return;
        await RunOperationAsync($"Merge {target}", (p, ct) => _git.MergeAsync(target, mode, p, ct));
    }

    private static string ListFiles(IReadOnlyList<string> files)
    {
        const int max = 10;
        var lines = files.Take(max).Select(f => $"  {f}");
        var more = files.Count > max ? $"\n  ...and {files.Count - max} more" : "";
        return string.Join("\n", lines) + more;
    }

    private async Task RebaseOntoAsync(string sha)
    {
        var current = CurrentBranchName;
        if (!await _dialogs.ConfirmAsync("Rebase",
                $"Rebase '{current}' onto {sha[..7]}?\n\nCommits on '{current}' are rewritten.")) return;
        await RunOperationAsync("Rebase", (p, ct) => _git.RebaseAsync(sha, p, ct));
    }

    private async Task RevertAsync(string sha)
    {
        if (!await _dialogs.ConfirmAsync("Revert", $"Create a new commit that undoes {sha[..7]}?")) return;
        await RunOperationAsync("Revert", (p, ct) => _git.RevertAsync(sha, p, ct));
    }

    private async Task ResetAsync(string sha, ResetMode mode)
    {
        var current = CurrentBranchName;
        if (mode == ResetMode.Hard)
        {
            var dirty = Staged.Count + Unstaged.Count;
            var lost = dirty == 0 ? "There are no uncommitted changes." : $"{dirty} uncommitted change(s) will be discarded.";
            if (!await _dialogs.ConfirmAsync("Hard reset",
                    $"Reset '{current}' to {sha[..7]}?\n\n{lost} Later commits stay reachable through the reflog.")) return;
        }
        await RunOperationAsync($"Reset ({mode.ToString().ToLowerInvariant()})", (p, ct) => _git.ResetAsync(sha, mode, p, ct));
    }

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task ContinueOperation() => RunOperationAsync("Continue", (p, ct) => _git.ContinueOperationAsync(p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task SkipOperation() => RunOperationAsync("Skip", (p, ct) => _git.SkipOperationAsync(p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private async Task AbortOperation()
    {
        if (!await _dialogs.ConfirmAsync("Abort", $"{OperationState.Describe()}. Abort and return to the state before it started?")) return;
        await RunOperationAsync("Abort", (p, ct) => _git.AbortOperationAsync(p, ct));
    }

    /// <summary>Runs a history-changing operation; a stop for conflicts switches to the working directory so they can be resolved.</summary>
    private async Task RunOperationAsync(string name, Func<IProgress<GitProgress>, CancellationToken, Task<GitOpResult>> operation)
    {
        GitOpResult? result = null;
        await RunRemoteAsync(name, async (p, ct) => result = await operation(p, ct), includeStatus: true);
        if (result is null) return;

        if (result.Paused)
        {
            ShowWorkingDirectory = true;
            StatusText = $"{name} stopped. Resolve conflicts, stage the files, then Continue \u2014 or Abort.";
        }
        else if (result.Success)
        {
            StatusText = $"{name} complete.";
        }
    }

    private async Task CreateBranchAsync(string sha, bool checkout)
    {
        var name = (await _dialogs.PromptAsync("Create branch", "Branch name:"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync(() => _git.CreateBranch(name, sha, checkout));
    }

    private async Task RenameBranchAsync(RefInfo branch)
    {
        var name = (await _dialogs.PromptAsync("Rename branch", $"New name for '{branch.Name}':", branch.Name))?.Trim();
        if (string.IsNullOrEmpty(name) || name == branch.Name) return;
        await RunAsync(() => _git.RenameBranch(branch.Name, name));
    }

    private async Task DeleteBranchAsync(RefInfo branch)
    {
        if (!await _dialogs.ConfirmAsync("Delete branch", $"Delete branch '{branch.Name}'? Unmerged commits may become unreachable."))
            return;
        await RunAsync(() => _git.DeleteBranch(branch.Name));
    }

    // ---- remotes ------------------------------------------------------------

    private bool CanRemote() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task Fetch() =>
        RunRemoteAsync("Fetch", (p, ct) => _git.FetchAsync(_options.PruneOnFetch, p, ct), includeStatus: false);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullMerge() => PullAsync(PullMode.Merge);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullRebase() => PullAsync(PullMode.Rebase);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task PullFastForward() => PullAsync(PullMode.FastForwardOnly);

    private Task PullAsync(PullMode mode) =>
        RunOperationAsync($"Pull ({mode})", (p, ct) => _git.PullAsync(mode, p, ct));

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task Push() => RunRemoteAsync("Push", (p, ct) => _git.PushAsync(p, ct), includeStatus: false);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private async Task ForcePush()
    {
        if (!await _dialogs.ConfirmAsync("Force push",
                "Force push (with lease) rewrites the remote branch. It is refused if someone else pushed in the meantime. Continue?"))
            return;
        await RunRemoteAsync("Force push", (p, ct) => _git.PushAsync(p, ct, forceWithLease: true), includeStatus: false);
    }

    [RelayCommand]
    private void CancelOperation() => _cts?.Cancel();

    private async Task RunRemoteAsync(
        string name, Func<IProgress<GitProgress>, CancellationToken, Task> operation, bool includeStatus)
    {
        if (IsBusy) return;
        BeginOperation(name);
        var ct = _cts!.Token;
        var progress = new Progress<GitProgress>(p =>
        {
            OperationText = $"{name}: {p.Phase}";
            if (p.Percent is { } pct)
            {
                ProgressIndeterminate = false;
                ProgressPercent = pct;
            }
        });

        try
        {
            await AuthRetry.RunAsync(_dialogs, () => _git.GetRemoteUrl(), () => operation(progress, ct), ct, _git.RepositoryPath);
            AutoFetchPaused = false;
            RefreshCore(keepSelection: true, includeStatus);
            StatusText = $"{name} complete.";
        }
        catch (OperationCanceledException)
        {
            TryRefresh(includeStatus);
            StatusText = $"{name} cancelled.";
        }
        catch (Exception ex)
        {
            TryRefresh(includeStatus);
            StatusText = ex is GitAuthException && GitCredentials.IsSshUrl(_git.GetRemoteUrl())
                ? $"{ex.Message} Load your key into ssh-agent (ssh-add) or configure it in ~/.ssh/config."
                : ex.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Fetches quietly for the timer: no dialogs, no sign-in windows, selection and working-tree state untouched.</summary>
    public async Task AutoFetchAsync()
    {
        if (IsBusy || AutoFetchPaused || _git.RepositoryPath is null || _git.GetRemoteNames().Count == 0) return;

        BeginOperation("Auto-fetch");
        try
        {
            await _git.FetchAsync(_options.PruneOnFetch, null, _cts!.Token, background: true);
            RefreshCore(keepSelection: true, includeStatus: false);
            StatusText = $"Auto-fetched at {DateTime.Now:t}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Auto-fetch cancelled.";
        }
        catch (GitAuthException)
        {
            AutoFetchPaused = true;
            StatusText = "Auto-fetch paused: sign-in required. Run Fetch to authenticate.";
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-fetch failed: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private void BeginOperation(string name)
    {
        // Remote operations rewrite refs and files and then refresh explicitly.
        _watcher?.Pause();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        OperationText = name;
        ProgressIndeterminate = true;
        ProgressPercent = 0;
    }

    private void EndOperation()
    {
        _cts?.Dispose();
        _cts = null;
        IsBusy = false;
        _watcher?.Resume();
    }

    private void TryRefresh(bool includeStatus)
    {
        try { RefreshCore(keepSelection: true, includeStatus); }
        catch (Exception) { /* keep the original error as the status */ }
    }

    private async Task SetUpstreamAsync(RefInfo branch)
    {
        var remote = _git.GetRemoteNames().FirstOrDefault() ?? "origin";
        var initial = branch.Upstream ?? $"{remote}/{branch.Name}";
        var upstream = (await _dialogs.PromptAsync("Set upstream", $"Upstream for '{branch.Name}' (remote/branch):", initial))?.Trim();
        if (string.IsNullOrEmpty(upstream)) return;
        await RunAsync(() => _git.SetUpstream(branch.Name, upstream));
    }

    // ---- tags ---------------------------------------------------------------

    private async Task CreateTagAsync(string sha)
    {
        var name = (await _dialogs.PromptAsync("Create tag", "Tag name:"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var message = await _dialogs.PromptAsync("Create tag", "Message (leave empty for a lightweight tag):");
        if (message is null) return;
        await RunAsync(() => _git.CreateTag(name, sha, message));
    }

    private Task PushTagAsync(string name) =>
        RunRemoteAsync($"Push tag {name}", (p, ct) => _git.PushTagAsync(name, p, ct), includeStatus: false);

    private async Task DeleteTagAsync(string name, bool remote)
    {
        var where = remote ? "from the remote" : "locally";
        if (!await _dialogs.ConfirmAsync("Delete tag", $"Delete tag '{name}' {where}?")) return;
        if (remote)
            await RunRemoteAsync($"Delete remote tag {name}", (p, ct) => _git.DeleteRemoteTagAsync(name, p, ct), includeStatus: false);
        else
            await RunAsync(() => _git.DeleteTag(name));
    }

    [RelayCommand]
    private Task PushSelectedTag() => SelectedTag is { } t ? PushTagAsync(t.Name) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteSelectedTag() => SelectedTag is { } t ? DeleteTagAsync(t.Name, remote: false) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteSelectedTagFromRemote() => SelectedTag is { } t ? DeleteTagAsync(t.Name, remote: true) : Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------

    private Task RunAsync(Action action)
    {
        Run(action);
        return Task.CompletedTask;
    }

    private void Run(Action action, bool refreshStatusOnly = false)
    {
        try
        {
            action();
            if (refreshStatusOnly) RefreshStatus(); else Refresh();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _cts?.Cancel();
        _git.Dispose();
    }
}
