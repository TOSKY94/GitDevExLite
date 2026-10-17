using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Hosting;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

public sealed record PatchRow(string Text, int? NewLine, string Kind)
{
    public Avalonia.Media.IBrush Foreground => Kind switch
    {
        "add" => Avalonia.Media.Brushes.LightGreen,
        "del" => Avalonia.Media.Brushes.IndianRed,
        "hunk" => Avalonia.Media.Brushes.SkyBlue,
        "meta" => Avalonia.Media.Brushes.Gray,
        _ => Avalonia.Media.Brushes.Gainsboro
    };
}

public sealed record CommentRow(string Header, string Body, string ThreadId, bool IsReply)
{
    public Avalonia.Thickness Indent => new(IsReply ? 24 : 0, 2, 0, 2);
}

public sealed record CommitRow(string ShortSha, string Message, string Author);

public sealed record CheckRow(string Name, string State, string? Url);

/// <summary>Pull requests of the repository's GitHub or Azure DevOps remote.</summary>
public sealed partial class PullRequestsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IDialogService _dialogs;
    private readonly Action<string> _checkoutLocal;
    private IPullRequestProvider? _provider;
    private RemoteRepo? _repo;
    private int _loadVersion;

    public PullRequestsViewModel(IGitService git, IDialogService dialogs, Action<string> checkoutLocal)
    {
        _git = git;
        _dialogs = dialogs;
        _checkoutLocal = checkoutLocal;
    }

    public static IReadOnlyList<string> Filters { get; } = new[] { "All", "Mine", "Review requested" };
    public static IReadOnlyList<string> MergeMethods { get; } = new[] { "Merge commit", "Squash", "Rebase" };

    public ObservableCollection<PullRequest> Items { get; } = new();
    public ObservableCollection<CommitRow> Commits { get; } = new();
    public ObservableCollection<PrFile> Files { get; } = new();
    public ObservableCollection<PatchRow> Patch { get; } = new();
    public ObservableCollection<CommentRow> Comments { get; } = new();
    public ObservableCollection<CheckRow> Checks { get; } = new();

    [ObservableProperty] private string _repoName = "";
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private PullRequest? _selected;
    [ObservableProperty] private PrFile? _selectedFile;
    [ObservableProperty] private PatchRow? _selectedPatchRow;
    [ObservableProperty] private CommentRow? _selectedComment;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private string _commentText = "";
    [ObservableProperty] private int _mergeMethodIndex = 1;
    [ObservableProperty] private string _mergeMessage = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSupported = true;

    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newBody = "";
    [ObservableProperty] private string _newSource = "";
    [ObservableProperty] private string _newTarget = "main";
    [ObservableProperty] private bool _newDraft;

    public async Task LoadAsync()
    {
        _repo = RemoteParser.Parse(_git.GetRemoteUrl());
        if (_repo is null || _repo.Kind is not (HostKind.GitHub or HostKind.AzureDevOps))
        {
            IsSupported = false;
            Status = _repo is null
                ? "This repository has no recognised remote."
                : $"Pull requests for {_repo.Kind} are not supported yet. GitHub and Azure DevOps are.";
            return;
        }

        RepoName = $"{_repo.Kind switch { HostKind.GitHub => "GitHub", _ => "Azure DevOps" }}: {_repo.Display}";
        var refs = _git.GetRefs();
        NewSource = refs.FirstOrDefault(r => r.Kind == RefKind.LocalBranch && r.IsHead)?.Name ?? "";
        var remoteNames = refs.Where(r => r.Kind == RefKind.RemoteBranch).Select(r => r.Name).ToList();
        NewTarget = remoteNames.Any(n => n.EndsWith("/main")) ? "main" : remoteNames.Any(n => n.EndsWith("/master")) ? "master" : "main";
        var (title, body) = PullRequestProviders.DraftFor(NewSource, _git.GetHeadMessage() is { Length: > 0 } m ? new[] { m } : Array.Empty<string>());
        NewTitle = title;
        NewBody = body;

        await RefreshAsync();
    }

    partial void OnFilterIndexChanged(int value) => _ = RefreshAsync();

    partial void OnSelectedChanged(PullRequest? value) => _ = LoadDetailAsync(value);

    partial void OnSelectedFileChanged(PrFile? value)
    {
        Patch.Clear();
        if (value?.Patch is not { Length: > 0 } text)
        {
            if (value is not null) Patch.Add(new PatchRow("(no textual diff available)", null, "meta"));
            return;
        }
        try
        {
            foreach (var h in PatchParser.Parse(value.Path, text, false, false).Hunks)
            {
                Patch.Add(new PatchRow(h.Header, null, "hunk"));
                foreach (var l in h.Lines)
                {
                    var prefix = l.Kind switch { DiffLineKind.Added => "+", DiffLineKind.Removed => "-", _ => " " };
                    var kind = l.Kind switch { DiffLineKind.Added => "add", DiffLineKind.Removed => "del", _ => "ctx" };
                    Patch.Add(new PatchRow($"{l.OldNo,5} {l.NewNo,5} {prefix}{l.Text}", l.NewNo, kind));
                }
            }
        }
        catch (Exception ex)
        {
            Patch.Add(new PatchRow(ex.Message, null, "meta"));
        }
    }

    private async Task<IPullRequestProvider?> GetProviderAsync(bool prompt)
    {
        if (_provider is not null) return _provider;
        if (_repo is null) return null;

        var token = await PullRequestProviders.TryGetTokenAsync(_repo, CancellationToken.None);
        if (token is null && prompt)
        {
            token = await PromptForTokenAsync();
        }
        if (token is null) return null;
        return _provider = PullRequestProviders.Create(_repo, token);
    }

    private async Task<string?> PromptForTokenAsync()
    {
        var host = _repo!.Kind == HostKind.AzureDevOps ? "dev.azure.com" : _repo.Host;
        var url = $"https://{host}/";
        var request = new CredentialRequest(
            url, $"Sign in to {host} to work with pull requests. A personal access token with pull request scope is needed.",
            false, await GitCredentials.IsGcmAvailableAsync());
        var choice = await _dialogs.PromptCredentialsAsync(request);
        if (choice is null) return null;

        if (choice.UseGcm)
        {
            await GitCredentials.SignInAsync(url, CancellationToken.None);
            return await PullRequestProviders.TryGetTokenAsync(_repo, CancellationToken.None);
        }
        if (string.IsNullOrWhiteSpace(choice.Token)) return null;
        try
        {
            await GitCredentials.StoreTokenAsync(url, string.IsNullOrWhiteSpace(choice.Username) ? "PAT" : choice.Username!, choice.Token!, CancellationToken.None);
        }
        catch (Exception)
        {
            // Storing is best effort; the token is still used for this session.
        }
        return choice.Token;
    }

    /// <summary>Runs a provider call; on a rejected token asks for new credentials once and retries.</summary>
    private async Task<T?> CallAsync<T>(Func<IPullRequestProvider, Task<T>> call)
    {
        var provider = await GetProviderAsync(prompt: true);
        if (provider is null)
        {
            Status = "Not signed in.";
            return default;
        }
        try
        {
            return await call(provider);
        }
        catch (HostingAuthException ex)
        {
            _provider = null;
            Status = ex.Message;
            var token = await PromptForTokenAsync();
            if (token is null) return default;
            _provider = PullRequestProviders.Create(_repo!, token);
            try
            {
                return await call(_provider);
            }
            catch (Exception retry)
            {
                _provider = null;
                Status = retry.Message;
                return default;
            }
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return default;
        }
    }

    private async Task<bool> RunAsync(string doing, Func<IPullRequestProvider, Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        Status = doing + "...";
        try
        {
            var ok = await CallAsync(async p => { await action(p); return true; });
            if (ok) Status = doing + " done.";
            return ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_repo is null || !IsSupported) return;
        var version = ++_loadVersion;
        IsBusy = true;
        Status = "Loading pull requests...";
        try
        {
            var filter = (PrFilter)FilterIndex;
            var list = await CallAsync(p => p.ListAsync(filter, CancellationToken.None));
            if (version != _loadVersion || list is null) return;
            Items.Clear();
            foreach (var pr in list) Items.Add(pr);
            Status = $"{list.Count} open pull request(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDetailAsync(PullRequest? pr)
    {
        Commits.Clear(); Files.Clear(); Patch.Clear(); Comments.Clear(); Checks.Clear();
        Description = ReviewSummary = "";
        if (pr is null) return;

        var detail = await CallAsync(p => p.GetAsync(pr.Number, CancellationToken.None));
        if (detail is null || Selected?.Number != pr.Number) return;

        Description = string.IsNullOrWhiteSpace(detail.Pr.Body) ? "(no description)" : detail.Pr.Body;
        ReviewSummary = detail.ReviewSummary;
        foreach (var c in detail.Commits)
            Commits.Add(new CommitRow(c.Sha.Length > 8 ? c.Sha[..8] : c.Sha, c.Message.Split('\n')[0], c.Author));
        foreach (var f in detail.Files) Files.Add(f);
        foreach (var c in detail.Comments)
        {
            var where = c.Path is null ? "" : $"  \u2014 {c.Path}{(c.Line is { } l ? ":" + l : "")}";
            Comments.Add(new CommentRow($"{c.Author}  {c.Created:yyyy-MM-dd HH:mm}{where}", c.Body, c.ThreadId, c.IsReply));
        }
        foreach (var c in detail.Checks) Checks.Add(new CheckRow(c.Name, c.State.ToString(), c.Url));
        if (detail.Checks.Count == 0) Checks.Add(new CheckRow("No checks reported", "", null));
        SelectedFile = Files.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Checkout()
    {
        if (Selected is not { } pr || IsBusy) return;
        IsBusy = true;
        Status = $"Fetching PR #{pr.Number}...";
        try
        {
            await _git.FetchRefAsync(pr.CheckoutRefspec, null, CancellationToken.None);
            _checkoutLocal(pr.LocalBranch);
            Status = $"Checked out {pr.LocalBranch}.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task Approve() => Review(ReviewVerdict.Approve, "Approve");

    [RelayCommand]
    private Task RequestChanges() => Review(ReviewVerdict.RequestChanges, "Request changes");

    [RelayCommand]
    private Task AddComment() => Review(ReviewVerdict.Comment, "Comment");

    private async Task Review(ReviewVerdict verdict, string label)
    {
        if (Selected is not { } pr) return;
        var text = CommentText.Trim();
        if (verdict != ReviewVerdict.Approve && text.Length == 0)
        {
            Status = "Write a comment first.";
            return;
        }
        var ok = verdict == ReviewVerdict.Comment
            ? await RunAsync(label, p => p.CommentAsync(pr.Number, text, CancellationToken.None))
            : await RunAsync(label, p => p.ReviewAsync(pr.Number, verdict, text.Length == 0 ? null : text, CancellationToken.None));
        if (!ok) return;
        CommentText = "";
        await LoadDetailAsync(pr);
    }

    [RelayCommand]
    private async Task CommentOnLine()
    {
        if (Selected is not { } pr || SelectedFile is not { } file) return;
        if (SelectedPatchRow?.NewLine is not { } line)
        {
            Status = "Select an added or unchanged line in the diff first.";
            return;
        }
        var text = CommentText.Trim();
        if (text.Length == 0)
        {
            Status = "Write a comment first.";
            return;
        }
        if (!await RunAsync("Inline comment", p => p.AddInlineCommentAsync(pr.Number, new InlineComment(file.Path, line, text), CancellationToken.None))) return;
        CommentText = "";
        await LoadDetailAsync(pr);
    }

    [RelayCommand]
    private async Task Reply()
    {
        if (Selected is not { } pr || SelectedComment is not { } c) return;
        var text = CommentText.Trim();
        if (text.Length == 0)
        {
            Status = "Write a reply first.";
            return;
        }
        if (!await RunAsync("Reply", p => p.ReplyAsync(pr.Number, c.ThreadId, text, CancellationToken.None))) return;
        CommentText = "";
        await LoadDetailAsync(pr);
    }

    [RelayCommand]
    private async Task Merge()
    {
        if (Selected is not { } pr) return;
        var method = (PrMergeMethod)MergeMethodIndex;
        if (!await _dialogs.ConfirmAsync("Merge pull request", $"Merge #{pr.Number} '{pr.Title}' into {pr.TargetBranch} ({MergeMethods[MergeMethodIndex]})?"))
            return;
        var message = MergeMessage;
        if (await RunAsync("Merge", p => p.MergeAsync(pr.Number, method, message, CancellationToken.None)))
            await RefreshAsync();
    }

    [RelayCommand]
    private async Task CreatePr()
    {
        if (string.IsNullOrWhiteSpace(NewTitle) || string.IsNullOrWhiteSpace(NewSource) || string.IsNullOrWhiteSpace(NewTarget))
        {
            Status = "Title, source branch and target branch are required.";
            return;
        }
        if (NewSource == NewTarget)
        {
            Status = "Source and target branch must differ.";
            return;
        }
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = $"Pushing {NewSource}...";
            try
            {
                await _git.PushAsync(null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Status = "Push failed: " + ex.Message;
                return;
            }

            Status = "Creating pull request...";
            var request = new CreatePrRequest(NewTitle.Trim(), NewBody, NewSource, NewTarget.Trim(), NewDraft);
            var created = await CallAsync(p => p.CreateAsync(request, CancellationToken.None));
            if (created is null) return;
            Status = $"Created #{created.Number}.";
            await _dialogs.CopyTextAsync(created.Url);
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private Task CopyLink() => Selected is { } pr ? _dialogs.CopyTextAsync(pr.Url) : Task.CompletedTask;
}
