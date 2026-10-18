using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed class PullRequestService : IPullRequestService
{
    private readonly IProviderSessionStore _sessions;
    private readonly IPullRequestAuthService _auth;
    private readonly HttpClient _githubHttp;
    private readonly HttpClient _azureDevOpsHttp;
    private readonly Func<string, string, CancellationToken, Task<string?>> _tokenProvider;
    private readonly Func<string, string, string?, CancellationToken, Task<string?>> _azureDevOpsTokenProvider;

    public PullRequestService(
        IProviderSessionStore? sessions = null,
        IPullRequestAuthService? auth = null,
        HttpClient? http = null,
        HttpClient? azureDevOpsHttp = null,
        Func<string, string, CancellationToken, Task<string?>>? tokenProvider = null)
    {
        _sessions = sessions ?? new InMemoryProviderSessionStore();
        _githubHttp = http ?? GitHubApiClient.CreateClient();
        _azureDevOpsHttp = azureDevOpsHttp ?? AzureDevOpsApiClient.CreateClient();
        _tokenProvider = tokenProvider ?? ProviderCredentialStore.TryGetTokenAsync;
        _azureDevOpsTokenProvider = tokenProvider is null
            ? ProviderCredentialStore.TryGetTokenAsync
            : (protocol, host, _, cancellationToken) => tokenProvider(protocol, host, cancellationToken);
        _auth = auth ?? new CompositePullRequestAuthService(
            new GitHubPullRequestAuthService(_sessions, _githubHttp, _tokenProvider),
            new AzureDevOpsPullRequestAuthService(_sessions, _azureDevOpsHttp, tokenProviderWithUsername: _azureDevOpsTokenProvider));
    }

    public HostedRepositoryId? ResolveRepository(IGitService git)
    {
        foreach (var remoteName in git.GetRemoteNames())
        {
            var url = git.GetRemoteUrl(remoteName);
            var resolved = PullRequestRemoteResolver.Resolve(remoteName, url);
            if (resolved is not null) return resolved;
        }

        return PullRequestRemoteResolver.Resolve("origin", git.GetRemoteUrl());
    }

    public ProviderSession? GetSession(HostedRepositoryId repository) =>
        _sessions.Get(repository.Provider, repository.Host);

    public ProviderCapabilities GetCapabilities(HostedRepositoryId repository, ProviderSession? session)
    {
        if (repository.Provider is not (HostingProviderKind.GitHub or HostingProviderKind.AzureDevOps))
            return ProviderCapabilities.None;

        var authenticated = session?.IsAuthenticated == true;

        return new ProviderCapabilities(
            CanListPullRequests: authenticated,
            CanCreatePullRequest: authenticated,
            CanCheckoutPullRequest: true,
            CanReview: authenticated,
            CanMerge: authenticated,
            CanCommentInline: repository.Provider is HostingProviderKind.GitHub or HostingProviderKind.AzureDevOps && authenticated,
            SupportedMergeStrategies: new[]
            {
                PullRequestMergeStrategy.MergeCommit,
                PullRequestMergeStrategy.Squash,
                PullRequestMergeStrategy.Rebase,
            });
    }

    public Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct) =>
        _auth.SignInAsync(repository, ct);

    public Task SignOutAsync(HostedRepositoryId repository, CancellationToken ct) =>
        _auth.SignOutAsync(repository, ct);

    public async Task<IReadOnlyList<PullRequestListItem>> ListAsync(
        HostedRepositoryId repository,
        PullRequestFilterKind filter,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
            return await ListAzureDevOpsAsync(repository, session, filter, ct);

        var pulls = await SendAsync<List<GitHubPullRequestDto>>(
            repository,
            session,
            HttpMethod.Get,
            $"repos/{repository.Owner}/{repository.Name}/pulls?state=open&per_page=50",
            ct) ?? new List<GitHubPullRequestDto>();

        IEnumerable<GitHubPullRequestDto> filtered = pulls;
        filtered = filter switch
        {
            PullRequestFilterKind.Mine => filtered.Where(pr => string.Equals(pr.User?.Login, session.Account.UserName, StringComparison.OrdinalIgnoreCase)),
            PullRequestFilterKind.ReviewRequested => filtered.Where(pr => pr.RequestedReviewers?.Any(r => string.Equals(r.Login, session.Account.UserName, StringComparison.OrdinalIgnoreCase)) == true),
            _ => filtered,
        };

        return filtered
            .Select(pr => ToListItem(pr, session))
            .OrderByDescending(pr => pr.UpdatedAt)
            .ToArray();
    }

    public async Task<PullRequestDetail> GetDetailAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
            return await GetAzureDevOpsDetailAsync(repository, session, pullRequestId, ct);

        var pr = await SendAsync<GitHubPullRequestDto>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId), ct)
            ?? throw new InvalidOperationException($"Pull request '{pullRequestId}' was not found.");
        var commits = await SendAsync<List<GitHubCommitDto>>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId, "commits?per_page=100"), ct)
            ?? new List<GitHubCommitDto>();
        var files = await SendAsync<List<GitHubFileDto>>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId, "files?per_page=100"), ct)
            ?? new List<GitHubFileDto>();
        var reviews = await SendAsync<List<GitHubReviewDto>>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId, "reviews?per_page=100"), ct)
            ?? new List<GitHubReviewDto>();
        var comments = await SendAsync<List<GitHubReviewCommentDto>>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId, "comments?per_page=100"), ct)
            ?? new List<GitHubReviewCommentDto>();
        var statusChecks = await GetStatusChecksAsync(repository, session, pr.Head?.Sha, ct);

        return new PullRequestDetail(
            ToListItem(pr, session),
            pr.Body ?? string.Empty,
            commits.Select(ToCommit).ToArray(),
            files.Select(ToChangedFile).ToArray(),
            BuildThreads(reviews, comments),
            statusChecks,
            GetCapabilities(repository, session));
    }

    public async Task<CreatePullRequestDraft> CreateDraftAsync(
        HostedRepositoryId repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
            return await CreateAzureDevOpsDraftAsync(repository, session, sourceBranch, targetBranch, ct);

        var compare = await SendAsync<GitHubCompareDto>(
            repository,
            session,
            HttpMethod.Get,
            $"repos/{repository.Owner}/{repository.Name}/compare/{Uri.EscapeDataString(targetBranch)}...{Uri.EscapeDataString(sourceBranch)}",
            ct,
            acceptNotFound: true);

        var title = compare?.Commits?.FirstOrDefault()?.Commit?.Message?.Split('\n')[0].Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = sourceBranch.Contains('/') ? sourceBranch[(sourceBranch.LastIndexOf('/') + 1)..] : sourceBranch;
            title = title.Replace('-', ' ').Replace('_', ' ').Trim();
            if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title[1..];
        }

        var description = compare?.Commits is { Count: > 0 }
            ? string.Join("\n", compare.Commits.Take(8).Select(c => $"- {c.Commit?.Message?.Split('\n')[0].Trim()}"))
            : $"Merge {sourceBranch} into {targetBranch}";

        return new CreatePullRequestDraft(title ?? sourceBranch, description, sourceBranch, targetBranch);
    }

    public async Task<PullRequestListItem> CreateAsync(
        HostedRepositoryId repository,
        CreatePullRequestRequest request,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
            return await CreateAzureDevOpsAsync(repository, session, request, ct);

        var pr = await SendAsync<GitHubPullRequestDto>(
            repository,
            session,
            HttpMethod.Post,
            $"repos/{repository.Owner}/{repository.Name}/pulls",
            ct,
            new Dictionary<string, object?>
            {
                ["title"] = request.Title,
                ["body"] = request.Description,
                ["head"] = request.SourceBranch,
                ["base"] = request.TargetBranch,
                ["draft"] = request.IsDraft,
            }) ?? throw new InvalidOperationException("GitHub did not return the created pull request.");

        return ToListItem(pr, session);
    }

    public async Task SubmitReviewAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestReviewRequest request,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
        {
            await SubmitAzureDevOpsReviewAsync(repository, session, pullRequestId, request, ct);
            return;
        }

        await SendAsync<object>(
            repository,
            session,
            HttpMethod.Post,
            PullPath(repository, pullRequestId, "reviews"),
            ct,
            new Dictionary<string, object?>
            {
                ["body"] = request.Body,
                ["event"] = request.Decision switch
                {
                    PullRequestReviewDecision.Approve => "APPROVE",
                    PullRequestReviewDecision.RequestChanges => "REQUEST_CHANGES",
                    _ => "COMMENT"
                }
            });
    }

    public async Task<PullRequestThread> AddInlineCommentAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestCommentDraft draft,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
            return await AddAzureDevOpsInlineCommentAsync(repository, session, pullRequestId, draft, ct);

        var detail = await SendAsync<GitHubPullRequestDto>(repository, session, HttpMethod.Get, PullPath(repository, pullRequestId), ct)
            ?? throw new InvalidOperationException($"Pull request '{pullRequestId}' was not found.");

        var payload = new Dictionary<string, object?>
        {
            ["body"] = draft.Body,
            ["commit_id"] = detail.Head?.Sha,
            ["path"] = draft.Anchor.Path,
            ["side"] = draft.Anchor.Side == PullRequestCommentSide.Left ? "LEFT" : "RIGHT",
        };
        if (draft.Anchor.Line is { } line)
            payload["line"] = line;

        var created = await SendAsync<GitHubReviewCommentDto>(repository, session, HttpMethod.Post, PullPath(repository, pullRequestId, "comments"), ct, payload)
            ?? throw new InvalidOperationException("GitHub did not return the created inline comment.");

        return new PullRequestThread(
            $"thread-{created.Id}",
            ToAnchor(created),
            false,
            new[]
            {
                new PullRequestComment(created.Id.ToString(), created.User?.Login ?? session.Account.DisplayName, created.Body ?? string.Empty, created.CreatedAt, false, ToAnchor(created))
            });
    }

    public async Task MergeAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestMergeRequest request,
        CancellationToken ct)
    {
        EnsureSupported(repository);
        var session = RequireAuthenticatedSession(repository);
        if (repository.Provider == HostingProviderKind.AzureDevOps)
        {
            await MergeAzureDevOpsAsync(repository, session, pullRequestId, request, ct);
            return;
        }

        await SendAsync<object>(
            repository,
            session,
            HttpMethod.Put,
            PullPath(repository, pullRequestId, "merge"),
            ct,
            new Dictionary<string, object?>
            {
                ["merge_method"] = request.Strategy switch
                {
                    PullRequestMergeStrategy.Squash => "squash",
                    PullRequestMergeStrategy.Rebase => "rebase",
                    _ => "merge"
                },
                ["commit_title"] = request.CommitTitle,
                ["commit_message"] = request.CommitMessage,
            });
    }

    public async Task CheckoutAsync(
        HostedRepositoryId repository,
        PullRequestCheckoutRequest request,
        IGitService git,
        IProgress<GitProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureSupported(repository);
        RequireAuthenticatedSession(repository);

        var branchName = request.LocalBranchName;
        if (string.IsNullOrWhiteSpace(branchName))
            branchName = request.SourceBranch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
                ? request.SourceBranch["refs/heads/".Length..]
                : request.SourceBranch;

        if (string.IsNullOrWhiteSpace(branchName))
            throw new InvalidOperationException("A local branch name is required to check out the pull request.");

        await git.FetchAsync(prune: false, progress, ct);
        git.Checkout(branchName);
    }

    private ProviderSession RequireAuthenticatedSession(HostedRepositoryId repository)
    {
        var session = GetSession(repository);
        if (session?.IsAuthenticated != true)
            throw new InvalidOperationException($"Sign in to {repository.Provider} before using pull request features.");

        return session;
    }

    private void EnsureSupported(HostedRepositoryId repository)
    {
        if (repository.Provider is not (HostingProviderKind.GitHub or HostingProviderKind.AzureDevOps))
            throw new NotSupportedException($"{repository.Provider} pull request support is not implemented yet.");
    }

    private async Task<IReadOnlyList<PullRequestListItem>> ListAzureDevOpsAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        PullRequestFilterKind filter,
        CancellationToken ct)
    {
        var criteria = filter switch
        {
            PullRequestFilterKind.Mine => $"searchCriteria.creatorId={Uri.EscapeDataString(session.Account.Id)}&",
            PullRequestFilterKind.ReviewRequested => $"searchCriteria.reviewerId={Uri.EscapeDataString(session.Account.Id)}&",
            _ => string.Empty,
        };

        var path = AzureDevOpsPullRequestPath(repository, $"pullrequests?{criteria}searchCriteria.status=active&api-version=7.1");
        var response = await SendAzureDevOpsAsync<AzureDevOpsPullRequestListDto>(repository, session, HttpMethod.Get, path, ct)
            ?? new AzureDevOpsPullRequestListDto(Array.Empty<AzureDevOpsPullRequestDto>());
        return response.Value.Select(pr => ToAzureDevOpsListItem(pr, session)).OrderByDescending(pr => pr.UpdatedAt).ToArray();
    }

    private async Task<PullRequestDetail> GetAzureDevOpsDetailAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string pullRequestId,
        CancellationToken ct)
    {
        var detail = await SendAzureDevOpsAsync<AzureDevOpsPullRequestDto>(repository, session, HttpMethod.Get, AzureDevOpsPullRequestPath(repository, $"pullRequests/{pullRequestId}?api-version=7.1"), ct)
            ?? throw new InvalidOperationException($"Pull request '{pullRequestId}' was not found.");
        var commits = await SendAzureDevOpsAsync<AzureDevOpsPullRequestCommitListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/commits?api-version=7.1"), ct)
            ?? new AzureDevOpsPullRequestCommitListDto(Array.Empty<AzureDevOpsPullRequestCommitDto>());
        var threads = await SendAzureDevOpsAsync<AzureDevOpsThreadListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/threads?api-version=7.1"), ct)
            ?? new AzureDevOpsThreadListDto(Array.Empty<AzureDevOpsThreadDto>());
        var iterations = await SendAzureDevOpsAsync<AzureDevOpsIterationListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/iterations?api-version=7.1"), ct)
            ?? new AzureDevOpsIterationListDto(Array.Empty<AzureDevOpsIterationDto>());
        var latestIterationId = iterations.Value.OrderByDescending(i => i.Id).FirstOrDefault()?.Id;
        var files = latestIterationId is null
            ? Array.Empty<ChangedFile>()
            : (await SendAzureDevOpsAsync<AzureDevOpsChangeListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/iterations/{latestIterationId}/changes?api-version=7.1"), ct)
                ?? new AzureDevOpsChangeListDto(Array.Empty<AzureDevOpsChangeDto>())).ChangeEntries
                ?.Select(ToAzureDevOpsChangedFile).ToArray() ?? Array.Empty<ChangedFile>();
        var statuses = await SendAzureDevOpsAsync<AzureDevOpsStatusListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/statuses?api-version=7.1"), ct)
            ?? new AzureDevOpsStatusListDto(Array.Empty<AzureDevOpsStatusDto>());

        return new PullRequestDetail(
            ToAzureDevOpsListItem(detail, session),
            detail.Description ?? string.Empty,
            commits.Value.Select(ToAzureDevOpsCommit).ToArray(),
            files,
            threads.Value.Select(ToAzureDevOpsThread).Where(t => t.Comments.Count > 0).ToArray(),
            statuses.Value.Select(ToAzureDevOpsStatusCheck).ToArray(),
            GetCapabilities(repository, session));
    }

    private async Task<CreatePullRequestDraft> CreateAzureDevOpsDraftAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string sourceBranch,
        string targetBranch,
        CancellationToken ct)
    {
        var commits = await SendAzureDevOpsAsync<AzureDevOpsCommitListDto>(repository, session, HttpMethod.Get, AzureDevOpsRepositoryPath(repository, $"commits?searchCriteria.$top=8&searchCriteria.itemVersion.version={Uri.EscapeDataString(sourceBranch)}&api-version=7.1"), ct, acceptNotFound: true)
            ?? new AzureDevOpsCommitListDto(Array.Empty<AzureDevOpsCommitDto>());
        var title = commits.Value.FirstOrDefault()?.Comment?.Split('\n')[0].Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = sourceBranch.Contains('/') ? sourceBranch[(sourceBranch.LastIndexOf('/') + 1)..] : sourceBranch;
            title = title.Replace('-', ' ').Replace('_', ' ').Trim();
            if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title[1..];
        }

        var description = commits.Value.Count > 0
            ? string.Join("\n", commits.Value.Take(8).Select(c => $"- {c.Comment?.Split('\n')[0].Trim()}"))
            : $"Merge {sourceBranch} into {targetBranch}";
        return new CreatePullRequestDraft(title ?? sourceBranch, description, sourceBranch, targetBranch);
    }

    private async Task<PullRequestListItem> CreateAzureDevOpsAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        CreatePullRequestRequest request,
        CancellationToken ct)
    {
        var pr = await SendAzureDevOpsAsync<AzureDevOpsPullRequestDto>(
            repository,
            session,
            HttpMethod.Post,
            AzureDevOpsPullRequestPath(repository, "pullrequests?api-version=7.1"),
            ct,
            new Dictionary<string, object?>
            {
                ["sourceRefName"] = ToRefName(request.SourceBranch),
                ["targetRefName"] = ToRefName(request.TargetBranch),
                ["title"] = request.Title,
                ["description"] = request.Description,
                ["isDraft"] = request.IsDraft,
            }) ?? throw new InvalidOperationException("Azure DevOps did not return the created pull request.");

        return ToAzureDevOpsListItem(pr, session);
    }

    private async Task SubmitAzureDevOpsReviewAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string pullRequestId,
        PullRequestReviewRequest request,
        CancellationToken ct)
    {
        var vote = request.Decision switch
        {
            PullRequestReviewDecision.Approve => 10,
            PullRequestReviewDecision.RequestChanges => -10,
            _ => 0,
        };

        await SendAzureDevOpsAsync<object>(
            repository,
            session,
            HttpMethod.Put,
            AzureDevOpsPullRequestPath(repository, $"pullrequests/{pullRequestId}/reviewers/{Uri.EscapeDataString(session.Account.Id)}?api-version=7.1"),
            ct,
            new Dictionary<string, object?>
            {
                ["id"] = session.Account.Id,
                ["vote"] = vote,
            });

        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            await SendAzureDevOpsAsync<object>(
                repository,
                session,
                HttpMethod.Post,
                AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/threads?api-version=7.1"),
                ct,
                new Dictionary<string, object?>
                {
                    ["comments"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["parentCommentId"] = 0,
                            ["content"] = request.Body,
                            ["commentType"] = "text"
                        }
                    },
                    ["status"] = "active"
                });
        }
    }

    private async Task MergeAzureDevOpsAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string pullRequestId,
        PullRequestMergeRequest request,
        CancellationToken ct)
    {
        var strategy = request.Strategy switch
        {
            PullRequestMergeStrategy.Squash => "squash",
            PullRequestMergeStrategy.Rebase => "rebase",
            _ => "noFastForward"
        };

        await SendAzureDevOpsAsync<object>(
            repository,
            session,
            HttpMethod.Patch,
            AzureDevOpsPullRequestPath(repository, $"pullrequests/{pullRequestId}?api-version=7.1"),
            ct,
            new Dictionary<string, object?>
            {
                ["status"] = "completed",
                ["completionOptions"] = new Dictionary<string, object?>
                {
                    ["mergeStrategy"] = strategy,
                    ["deleteSourceBranch"] = request.DeleteSourceBranch,
                    ["mergeCommitMessage"] = request.CommitMessage ?? request.CommitTitle,
                }
            });
    }

    private async Task<PullRequestThread> AddAzureDevOpsInlineCommentAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string pullRequestId,
        PullRequestCommentDraft draft,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(draft.Anchor.Path))
            throw new InvalidOperationException("Azure DevOps inline comments require a file path.");
        if (draft.Anchor.Line is null)
            throw new InvalidOperationException("Azure DevOps inline comments require a target line.");

        var payload = new Dictionary<string, object?>
        {
            ["comments"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["parentCommentId"] = 0,
                    ["content"] = draft.Body,
                    ["commentType"] = "text"
                }
            },
            ["status"] = "active",
            ["threadContext"] = new Dictionary<string, object?>
            {
                ["filePath"] = NormalizeAzureDevOpsPath(draft.Anchor.Path),
                ["rightFileStart"] = BuildAzureDevOpsCommentPosition(draft.Anchor.Line),
                ["rightFileEnd"] = BuildAzureDevOpsCommentPosition(draft.Anchor.EndLine ?? draft.Anchor.Line),
            }
        };

        var created = await SendAzureDevOpsAsync<AzureDevOpsThreadDto>(
            repository,
            session,
            HttpMethod.Post,
            AzureDevOpsRepositoryPath(repository, $"pullRequests/{pullRequestId}/threads?api-version=7.1"),
            ct,
            payload) ?? throw new InvalidOperationException("Azure DevOps did not return the created inline comment thread.");

        return ToAzureDevOpsThread(created);
    }

    private async Task<T?> SendAzureDevOpsAsync<T>(
        HostedRepositoryId repository,
        ProviderSession session,
        HttpMethod method,
        string path,
        CancellationToken ct,
        object? body = null,
        bool acceptNotFound = false)
    {
        var token = await _azureDevOpsTokenProvider("https", repository.Host, repository.Owner, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Azure DevOps credential was found for this host. Sign in again.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = CreateAzureDevOpsAuth(token);
        if (body is not null)
            request.Content = new JsonStringContent(JsonSerializer.Serialize(body));

        using var response = await _azureDevOpsHttp.SendAsync(request, ct);
        if (acceptNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Azure DevOps API request failed: {(int)response.StatusCode} {response.ReasonPhrase}{FormatErrorBody(errorBody)}");
        }

        if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0)
            return default;

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private static AuthenticationHeaderValue CreateAzureDevOpsAuth(string token)
    {
        var bytes = Encoding.ASCII.GetBytes($":{token}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    private static string AzureDevOpsPullRequestPath(HostedRepositoryId repository, string suffix) =>
        $"{repository.Owner}/{repository.Project}/_apis/git/repositories/{repository.Name}/{suffix}";

    private static string AzureDevOpsRepositoryPath(HostedRepositoryId repository, string suffix) =>
        $"{repository.Owner}/{repository.Project}/_apis/git/repositories/{repository.Name}/{suffix}";

    private static string ToRefName(string branch) => branch.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ? branch : $"refs/heads/{branch}";

    private static PullRequestListItem ToAzureDevOpsListItem(AzureDevOpsPullRequestDto pr, ProviderSession session) => new(
        pr.PullRequestId.ToString(),
        pr.PullRequestId,
        pr.Title ?? $"PR #{pr.PullRequestId}",
        TrimRefName(pr.SourceRefName),
        TrimRefName(pr.TargetRefName),
        pr.CreatedBy?.DisplayName ?? pr.CreatedBy?.UniqueName ?? "unknown",
        NormalizeAzureDevOpsStatus(pr.Status, pr.IsDraft),
        pr.IsDraft,
        string.Equals(pr.CreatedBy?.Id, session.Account.Id, StringComparison.OrdinalIgnoreCase),
        pr.Reviewers?.Any(r => string.Equals(r.Id, session.Account.Id, StringComparison.OrdinalIgnoreCase) && r.Vote == 0) == true,
        pr.CreationDate,
        pr.Url);

    private static string NormalizeAzureDevOpsStatus(string? status, bool isDraft)
    {
        if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            return isDraft ? "Draft" : "Open";
        return status ?? "Unknown";
    }

    private static string TrimRefName(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return string.Empty;
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) ? branch["refs/heads/".Length..] : branch;
    }

    private static string NormalizeAzureDevOpsPath(string path) =>
        path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path.Replace('\\', '/')}";

    private static Dictionary<string, object?> BuildAzureDevOpsCommentPosition(int? line) => new()
    {
        ["line"] = line,
        ["offset"] = 1,
    };

    private static PullRequestCommit ToAzureDevOpsCommit(AzureDevOpsPullRequestCommitDto commit) => new(
        commit.CommitId ?? string.Empty,
        commit.Comment?.Split('\n')[0].Trim() ?? commit.CommitId ?? string.Empty,
        commit.Author?.Name ?? "unknown",
        commit.Author?.Date ?? DateTimeOffset.UtcNow);

    private static ChangedFile ToAzureDevOpsChangedFile(AzureDevOpsChangeDto change) =>
        new(change.Item?.Path ?? string.Empty, null, change.ChangeType?.ToLowerInvariant() switch
        {
            "add" or "branch" => "A",
            "delete" => "D",
            "rename" => "R",
            _ => "M"
        });

    private static PullRequestThread ToAzureDevOpsThread(AzureDevOpsThreadDto thread) => new(
        thread.Id.ToString(),
        ToAzureDevOpsAnchor(thread.ThreadContext),
        string.Equals(thread.Status, "fixed", StringComparison.OrdinalIgnoreCase),
        thread.Comments?
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .Select(c => new PullRequestComment(
                c.Id.ToString(),
                c.Author?.DisplayName ?? c.Author?.UniqueName ?? "unknown",
                c.Content ?? string.Empty,
                c.PublishedDate ?? DateTimeOffset.UtcNow,
                false,
                ToAzureDevOpsAnchor(thread.ThreadContext)))
            .ToArray() ?? Array.Empty<PullRequestComment>());

    private static PullRequestCommentAnchor? ToAzureDevOpsAnchor(AzureDevOpsThreadContextDto? context)
    {
        var filePath = context?.FilePath;
        var right = context?.RightFileStart;
        if (string.IsNullOrWhiteSpace(filePath) || right?.Line is null)
            return null;

        return new PullRequestCommentAnchor(filePath, right.Line, right.Line, PullRequestCommentSide.Right, right.Line, right.Line);
    }

    private static PullRequestStatusCheck ToAzureDevOpsStatusCheck(AzureDevOpsStatusDto status) =>
        new(status.Context?.Name ?? status.Description ?? "status", NormalizeAzureDevOpsCheckStatus(status.State), status.Description, status.TargetUrl);

    private static string NormalizeAzureDevOpsCheckStatus(string? state) => state?.ToLowerInvariant() switch
    {
        "succeeded" or "success" => "Succeeded",
        "failed" or "error" => "Failed",
        "pending" or "notset" => "Pending",
        _ => "Unknown"
    };

    private static string PullPath(HostedRepositoryId repository, string pullRequestId, string? suffix = null)
    {
        var path = $"repos/{repository.Owner}/{repository.Name}/pulls/{pullRequestId}";
        return string.IsNullOrWhiteSpace(suffix) ? path : $"{path}/{suffix}";
    }

    private async Task<T?> SendAsync<T>(
        HostedRepositoryId repository,
        ProviderSession session,
        HttpMethod method,
        string path,
        CancellationToken ct,
        object? body = null,
        bool acceptNotFound = false)
    {
        var token = await ProviderCredentialStore.TryGetTokenAsync("https", repository.Host, session.Account.UserName, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No GitHub credential was found for this host. Sign in again.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitDevExLite");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new JsonStringContent(JsonSerializer.Serialize(body));
        }

        using var response = await _githubHttp.SendAsync(request, ct);
        if (acceptNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"GitHub API request failed: {(int)response.StatusCode} {response.ReasonPhrase}{FormatErrorBody(errorBody)}");
        }

        if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0)
            return default;

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private async Task<IReadOnlyList<PullRequestStatusCheck>> GetStatusChecksAsync(
        HostedRepositoryId repository,
        ProviderSession session,
        string? sha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha)) return Array.Empty<PullRequestStatusCheck>();

        var combined = await SendAsync<GitHubCombinedStatusDto>(repository, session, HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/commits/{sha}/status", ct, acceptNotFound: true);
        if (combined?.Statuses is { Count: > 0 })
        {
            return combined.Statuses.Select(s => new PullRequestStatusCheck(s.Context ?? "status", NormalizeStatus(s.State), s.Description, s.TargetUrl)).ToArray();
        }

        var checks = await SendAsync<GitHubChecksDto>(repository, session, HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/commits/{sha}/check-runs", ct, acceptNotFound: true);
        return checks?.CheckRuns?.Select(c => new PullRequestStatusCheck(c.Name, NormalizeCheckStatus(c.Status, c.Conclusion), c.Output?.Title, c.HtmlUrl)).ToArray()
            ?? Array.Empty<PullRequestStatusCheck>();
    }

    private static string NormalizeStatus(string? state) => state?.ToLowerInvariant() switch
    {
        "success" => "Succeeded",
        "failure" => "Failed",
        "error" => "Failed",
        "pending" => "Pending",
        _ => "Unknown"
    };

    private static string NormalizeCheckStatus(string? status, string? conclusion)
    {
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return conclusion?.ToLowerInvariant() switch
            {
                "success" => "Succeeded",
                "failure" => "Failed",
                "cancelled" => "Cancelled",
                _ => "Completed"
            };
        }

        return string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase) ? "Running" : "Pending";
    }

    private static string FormatErrorBody(string errorBody)
    {
        if (string.IsNullOrWhiteSpace(errorBody)) return string.Empty;
        var compact = errorBody.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return compact.Length == 0 ? string.Empty : $": {compact}";
    }

    private static PullRequestListItem ToListItem(GitHubPullRequestDto pr, ProviderSession session) => new(
        pr.Number.ToString(),
        pr.Number,
        pr.Title ?? $"PR #{pr.Number}",
        pr.Head?.Ref ?? string.Empty,
        pr.Base?.Ref ?? string.Empty,
        pr.User?.Login ?? "unknown",
        pr.StateLabel,
        pr.Draft,
        string.Equals(pr.User?.Login, session.Account.UserName, StringComparison.OrdinalIgnoreCase),
        pr.RequestedReviewers?.Any(r => string.Equals(r.Login, session.Account.UserName, StringComparison.OrdinalIgnoreCase)) == true,
        pr.UpdatedAt,
        pr.HtmlUrl);

    private static PullRequestCommit ToCommit(GitHubCommitDto commit) => new(
        commit.Sha,
        commit.Commit?.Message?.Split('\n')[0].Trim() ?? commit.Sha,
        commit.Commit?.Author?.Name ?? commit.Author?.Login ?? "unknown",
        commit.Commit?.Author?.Date ?? DateTimeOffset.UtcNow);

    private static ChangedFile ToChangedFile(GitHubFileDto file) =>
        new(file.Filename, file.PreviousFilename, file.Status?.ToLowerInvariant() switch
        {
            "added" => "A",
            "removed" => "D",
            "renamed" => "R",
            _ => "M"
        }, file.Patch);

    private static IReadOnlyList<PullRequestThread> BuildThreads(IReadOnlyList<GitHubReviewDto> reviews, IReadOnlyList<GitHubReviewCommentDto> comments)
    {
        var reviewThreads = reviews
            .Where(r => !string.IsNullOrWhiteSpace(r.Body))
            .Select(r => new PullRequestThread(
                $"review-{r.Id}",
                null,
                false,
                new[] { new PullRequestComment(r.Id.ToString(), r.User?.Login ?? "unknown", r.Body ?? string.Empty, r.SubmittedAt ?? r.CreatedAt, false) }));

        var commentThreads = comments.GroupBy(c => c.InReplyToId ?? c.Id).Select(group =>
        {
            var root = group.OrderBy(c => c.CreatedAt).First();
            return new PullRequestThread(
                $"thread-{group.Key}",
                ToAnchor(root),
                false,
                group.OrderBy(c => c.CreatedAt)
                    .Select(c => new PullRequestComment(c.Id.ToString(), c.User?.Login ?? "unknown", c.Body ?? string.Empty, c.CreatedAt, false, ToAnchor(c)))
                    .ToArray());
        });

        return reviewThreads.Concat(commentThreads).ToArray();
    }

    private static PullRequestCommentAnchor? ToAnchor(GitHubReviewCommentDto comment)
    {
        if (string.IsNullOrWhiteSpace(comment.Path)) return null;
        return new PullRequestCommentAnchor(
            comment.Path,
            comment.Line,
            comment.StartLine,
            comment.Side?.Equals("LEFT", StringComparison.OrdinalIgnoreCase) == true ? PullRequestCommentSide.Left : PullRequestCommentSide.Right,
            comment.OriginalLine,
            comment.StartLine);
    }

    private sealed record GitHubPullRequestDto(int Number, string? Title, string? Body, string State, bool Draft, DateTimeOffset UpdatedAt, string? HtmlUrl, GitHubUserDto? User, GitHubBranchDto? Head, GitHubBranchDto? Base, IReadOnlyList<GitHubUserDto>? RequestedReviewers)
    {
        public string StateLabel => string.Equals(State, "open", StringComparison.OrdinalIgnoreCase)
            ? (Draft ? "Draft" : "Open")
            : State;
    }

    private sealed record GitHubBranchDto(string? Ref, string? Sha);
    private sealed record GitHubUserDto(string Login);
    private sealed record GitHubCommitDto(string Sha, GitHubCommitInnerDto? Commit, GitHubUserDto? Author);
    private sealed record GitHubCommitInnerDto(string? Message, GitHubCommitAuthorDto? Author);
    private sealed record GitHubCommitAuthorDto(string? Name, DateTimeOffset? Date);
    private sealed record GitHubFileDto(string Filename, string? PreviousFilename, string? Status, string? Patch);
    private sealed record GitHubReviewDto(long Id, string? Body, GitHubUserDto? User, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt);
    private sealed record GitHubReviewCommentDto(long Id, string? Body, string? Path, int? Line, int? OriginalLine, int? StartLine, string? Side, long? InReplyToId, GitHubUserDto? User, DateTimeOffset CreatedAt);
    private sealed record GitHubCompareDto(IReadOnlyList<GitHubCompareCommitDto>? Commits);
    private sealed record GitHubCompareCommitDto(GitHubCommitInnerDto? Commit);
    private sealed record GitHubCombinedStatusDto(IReadOnlyList<GitHubStatusDto>? Statuses);
    private sealed record GitHubStatusDto(string? Context, string? State, string? Description, string? TargetUrl);
    private sealed record GitHubChecksDto(IReadOnlyList<GitHubCheckRunDto>? CheckRuns);
    private sealed record GitHubCheckRunDto(string Name, string? Status, string? Conclusion, string? HtmlUrl, GitHubCheckOutputDto? Output);
    private sealed record GitHubCheckOutputDto(string? Title);

    private sealed record AzureDevOpsPullRequestListDto(IReadOnlyList<AzureDevOpsPullRequestDto> Value);
    private sealed record AzureDevOpsPullRequestDto(int PullRequestId, string? Title, string? Description, string? Status, bool IsDraft, DateTimeOffset CreationDate, string? Url, string? SourceRefName, string? TargetRefName, AzureDevOpsIdentityDto? CreatedBy, IReadOnlyList<AzureDevOpsReviewerDto>? Reviewers);
    private sealed record AzureDevOpsIdentityDto(string? Id, string? DisplayName, string? UniqueName);
    private sealed record AzureDevOpsReviewerDto(string? Id, int Vote);
    private sealed record AzureDevOpsPullRequestCommitListDto(IReadOnlyList<AzureDevOpsPullRequestCommitDto> Value);
    private sealed record AzureDevOpsPullRequestCommitDto(string? CommitId, string? Comment, AzureDevOpsGitAuthorDto? Author);
    private sealed record AzureDevOpsGitAuthorDto(string? Name, DateTimeOffset? Date);
    private sealed record AzureDevOpsThreadListDto(IReadOnlyList<AzureDevOpsThreadDto> Value);
    private sealed record AzureDevOpsThreadDto(int Id, string? Status, AzureDevOpsThreadContextDto? ThreadContext, IReadOnlyList<AzureDevOpsCommentDto>? Comments);
    private sealed record AzureDevOpsThreadContextDto(string? FilePath, AzureDevOpsThreadPositionDto? RightFileStart);
    private sealed record AzureDevOpsThreadPositionDto(int? Line);
    private sealed record AzureDevOpsCommentDto(int Id, string? Content, DateTimeOffset? PublishedDate, AzureDevOpsIdentityDto? Author);
    private sealed record AzureDevOpsIterationListDto(IReadOnlyList<AzureDevOpsIterationDto> Value);
    private sealed record AzureDevOpsIterationDto(int Id);
    private sealed record AzureDevOpsChangeListDto(IReadOnlyList<AzureDevOpsChangeDto>? ChangeEntries);
    private sealed record AzureDevOpsChangeDto(string? ChangeType, AzureDevOpsGitItemDto? Item);
    private sealed record AzureDevOpsGitItemDto(string? Path);
    private sealed record AzureDevOpsStatusListDto(IReadOnlyList<AzureDevOpsStatusDto> Value);
    private sealed record AzureDevOpsStatusDto(string? State, string? Description, string? TargetUrl, AzureDevOpsStatusContextDto? Context);
    private sealed record AzureDevOpsStatusContextDto(string? Name);
    private sealed record AzureDevOpsCommitListDto(IReadOnlyList<AzureDevOpsCommitDto> Value);
    private sealed record AzureDevOpsCommitDto(string? Comment);

    private sealed class CompositePullRequestAuthService : IPullRequestAuthService
    {
        private readonly IReadOnlyDictionary<HostingProviderKind, IPullRequestAuthService> _services;

        public CompositePullRequestAuthService(params IPullRequestAuthService[] services)
        {
            _services = services.ToDictionary(
                service => service switch
                {
                    GitHubPullRequestAuthService => HostingProviderKind.GitHub,
                    AzureDevOpsPullRequestAuthService => HostingProviderKind.AzureDevOps,
                    _ => HostingProviderKind.Unknown
                },
                service => service);
        }

        public Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct) => Resolve(repository).SignInAsync(repository, ct);

        public Task SignOutAsync(HostedRepositoryId repository, CancellationToken ct) => Resolve(repository).SignOutAsync(repository, ct);

        private IPullRequestAuthService Resolve(HostedRepositoryId repository)
        {
            if (_services.TryGetValue(repository.Provider, out var service))
                return service;

            throw new NotSupportedException($"{repository.Provider} sign-in is not implemented yet.");
        }
    }

    private sealed class JsonStringContent : StringContent
    {
        public JsonStringContent(string body) : base(body, Encoding.UTF8, "application/json")
        {
            Body = body;
        }

        public string Body { get; }
    }
}
