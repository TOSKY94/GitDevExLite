using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface IPullRequestService
{
    HostedRepositoryId? ResolveRepository(IGitService git);
    ProviderSession? GetSession(HostedRepositoryId repository);
    ProviderCapabilities GetCapabilities(HostedRepositoryId repository, ProviderSession? session);
    Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct);
    Task SignOutAsync(HostedRepositoryId repository, CancellationToken ct);
    Task<IReadOnlyList<PullRequestListItem>> ListAsync(
        HostedRepositoryId repository,
        PullRequestFilterKind filter,
        CancellationToken ct);
    Task<PullRequestDetail> GetDetailAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        CancellationToken ct);
    Task<CreatePullRequestDraft> CreateDraftAsync(
        HostedRepositoryId repository,
        string sourceBranch,
        string targetBranch,
        CancellationToken ct);
    Task<PullRequestListItem> CreateAsync(
        HostedRepositoryId repository,
        CreatePullRequestRequest request,
        CancellationToken ct);
    Task SubmitReviewAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestReviewRequest request,
        CancellationToken ct);
    Task<PullRequestThread> AddInlineCommentAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestCommentDraft draft,
        CancellationToken ct);
    Task MergeAsync(
        HostedRepositoryId repository,
        string pullRequestId,
        PullRequestMergeRequest request,
        CancellationToken ct);
    Task CheckoutAsync(
        HostedRepositoryId repository,
        PullRequestCheckoutRequest request,
        IGitService git,
        IProgress<GitProgress>? progress,
        CancellationToken ct);
}
