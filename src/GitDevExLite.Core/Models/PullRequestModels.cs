namespace GitDevExLite.Core.Models;

public enum HostingProviderKind
{
    Unknown,
    GitHub,
    AzureDevOps,
}

public enum PullRequestFilterKind
{
    All,
    Mine,
    ReviewRequested,
}

public enum PullRequestReviewDecision
{
    Comment,
    Approve,
    RequestChanges,
}

public enum PullRequestMergeStrategy
{
    MergeCommit,
    Squash,
    Rebase,
}

public enum PullRequestCommentSide
{
    Right,
    Left,
}

public sealed record HostedRepositoryId(
    HostingProviderKind Provider,
    string Host,
    string Owner,
    string Name,
    string? Project = null,
    string? RemoteName = null)
{
    public string DisplayName => Provider switch
    {
        HostingProviderKind.AzureDevOps when !string.IsNullOrWhiteSpace(Project) => $"{Project}/{Name}",
        _ when !string.IsNullOrWhiteSpace(Owner) => $"{Owner}/{Name}",
        _ => Name
    };
}

public sealed record ProviderAccount(
    string Id,
    string DisplayName,
    string? UserName,
    string? Email);

public sealed record ProviderSession(
    HostingProviderKind Provider,
    string Host,
    ProviderAccount Account,
    bool IsAuthenticated,
    DateTimeOffset? ExpiresAt = null,
    string? SessionLabel = null);

public sealed record ProviderCapabilities(
    bool CanListPullRequests,
    bool CanCreatePullRequest,
    bool CanCheckoutPullRequest,
    bool CanReview,
    bool CanMerge,
    bool CanCommentInline,
    IReadOnlyList<PullRequestMergeStrategy> SupportedMergeStrategies)
{
    public static ProviderCapabilities None { get; } = new(
        false, false, false, false, false, false, Array.Empty<PullRequestMergeStrategy>());
}

public sealed record PullRequestListItem(
    string Id,
    int Number,
    string Title,
    string SourceBranch,
    string TargetBranch,
    string Author,
    string Status,
    bool IsDraft,
    bool IsMine,
    bool IsReviewRequested,
    DateTimeOffset UpdatedAt,
    string? Url = null);

public sealed record PullRequestCommit(
    string Sha,
    string Summary,
    string Author,
    DateTimeOffset When);

public sealed record PullRequestStatusCheck(
    string Name,
    string Status,
    string? Description = null,
    string? Url = null);

public sealed record PullRequestCommentAnchor(
    string Path,
    int? Line,
    int? EndLine = null,
    PullRequestCommentSide Side = PullRequestCommentSide.Right,
    int? OriginalLine = null,
    int? OriginalEndLine = null);

public sealed record PullRequestComment(
    string Id,
    string Author,
    string Body,
    DateTimeOffset CreatedAt,
    bool IsPending,
    PullRequestCommentAnchor? Anchor = null);

public sealed record PullRequestThread(
    string Id,
    PullRequestCommentAnchor? Anchor,
    bool IsResolved,
    IReadOnlyList<PullRequestComment> Comments);

public sealed record PullRequestDetail(
    PullRequestListItem Summary,
    string Description,
    IReadOnlyList<PullRequestCommit> Commits,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<PullRequestThread> Threads,
    IReadOnlyList<PullRequestStatusCheck> StatusChecks,
    ProviderCapabilities Capabilities);

public sealed record CreatePullRequestDraft(
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    bool IsDraft = false);

public sealed record CreatePullRequestRequest(
    string Title,
    string Description,
    string SourceBranch,
    string TargetBranch,
    bool IsDraft = false);

public sealed record PullRequestReviewRequest(
    PullRequestReviewDecision Decision,
    string Body,
    IReadOnlyList<PullRequestCommentDraft>? InlineComments = null);

public sealed record PullRequestCommentDraft(
    PullRequestCommentAnchor Anchor,
    string Body);

public sealed record PullRequestMergeRequest(
    PullRequestMergeStrategy Strategy,
    string? CommitTitle = null,
    string? CommitMessage = null,
    bool DeleteSourceBranch = false);

public sealed record PullRequestCheckoutRequest(
    string Id,
    string SourceBranch,
    string? LocalBranchName = null,
    string? RefSpec = null);
