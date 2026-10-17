namespace GitDevExLite.Core.Hosting;

public enum HostKind { Unknown, GitHub, AzureDevOps, GitLab, Bitbucket }

public enum PrFilter { All, Mine, ReviewRequested }

public enum PrMergeMethod { Merge, Squash, Rebase }

public enum ReviewVerdict { Comment, Approve, RequestChanges }

public enum CheckState { Pending, Success, Failure, Neutral }

/// <summary>Owner is the GitHub owner or Azure DevOps organization; Project is only used by Azure DevOps.</summary>
public sealed record RemoteRepo(HostKind Kind, string Host, string Owner, string Project, string Name, string Url)
{
    public string Display => Kind == HostKind.AzureDevOps ? $"{Owner}/{Project}/{Name}" : $"{Owner}/{Name}";
}

public sealed record PullRequest(
    int Number, string Title, string Body, string Author, string SourceBranch, string TargetBranch,
    string State, bool IsDraft, string Url, DateTimeOffset Created, string HeadSha,
    IReadOnlyList<string> Reviewers, string CheckoutRefspec)
{
    public string LocalBranch => $"pr-{Number}";
}

public sealed record PrCommit(string Sha, string Message, string Author, DateTimeOffset? Date);

/// <summary>Patch is a unified diff starting at the first hunk header, or null when unavailable (binary or too large).</summary>
public sealed record PrFile(string Path, string Status, int Additions, int Deletions, string? Patch);

/// <summary>Comments of one thread share a ThreadId. Path and Line are set for inline comments.</summary>
public sealed record PrComment(
    string Id, string ThreadId, string Author, string Body, DateTimeOffset? Created, string? Path, int? Line, bool IsReply);

public sealed record PrCheck(string Name, CheckState State, string? Url);

public sealed record PullRequestDetail(
    PullRequest Pr, IReadOnlyList<PrCommit> Commits, IReadOnlyList<PrFile> Files,
    IReadOnlyList<PrComment> Comments, IReadOnlyList<PrCheck> Checks, string ReviewSummary);

public sealed record CreatePrRequest(string Title, string Body, string SourceBranch, string TargetBranch, bool Draft);

public sealed record InlineComment(string Path, int Line, string Body);

public class HostingException : Exception
{
    public HostingException(string message, int statusCode = 0, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;

    public int StatusCode { get; }
}

/// <summary>The token is missing, expired or lacks permission.</summary>
public sealed class HostingAuthException : HostingException
{
    public HostingAuthException(string message, int statusCode) : base(message, statusCode) { }
}

public interface IPullRequestProvider
{
    string Name { get; }
    RemoteRepo Repo { get; }
    Task<IReadOnlyList<PullRequest>> ListAsync(PrFilter filter, CancellationToken ct);
    Task<PullRequestDetail> GetAsync(int number, CancellationToken ct);
    Task<PullRequest> CreateAsync(CreatePrRequest request, CancellationToken ct);
    Task ReviewAsync(int number, ReviewVerdict verdict, string? body, CancellationToken ct);
    Task CommentAsync(int number, string body, CancellationToken ct);
    Task AddInlineCommentAsync(int number, InlineComment comment, CancellationToken ct);
    Task ReplyAsync(int number, string threadId, string body, CancellationToken ct);
    Task MergeAsync(int number, PrMergeMethod method, string? message, CancellationToken ct);
}
