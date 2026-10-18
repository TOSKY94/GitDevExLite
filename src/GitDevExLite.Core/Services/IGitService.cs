using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface IGitService : IDisposable
{
    string? RepositoryPath { get; }
    string? GitDirectory { get; }
    void Open(string path);
    IReadOnlyList<RefInfo> GetRefs();
    IReadOnlyList<StashInfo> GetStashes();
    IReadOnlyList<CommitInfo> GetCommits(int maxCount = 2000);
    IReadOnlyList<FileChange> GetStatus();
    void Stage(string path);
    void Unstage(string path);
    void Commit(string message, bool amend = false);
    string? GetHeadMessage();
    void Checkout(string branchName);
    void CheckoutRemoteBranch(string remoteBranchName);

    IReadOnlyList<ChangedFile> GetCommitChanges(string sha);
    FileDiff GetCommitDiff(string sha, string path);
    FileDiff GetWorkingDiff(string path, bool staged);
    string? GetFileContent(string path, bool staged);
    void StageLines(FileDiff diff, IReadOnlySet<LineRef> lines);
    void UnstageLines(FileDiff diff, IReadOnlySet<LineRef> lines);

    void CreateBranch(string name, string sha, bool checkout);
    void RenameBranch(string oldName, string newName);
    void DeleteBranch(string name);
    void CheckoutCommit(string sha);

    IReadOnlyList<string> GetRemoteNames();
    string? GetRemoteUrl(string? remote = null);
    Task FetchAsync(bool prune, IProgress<GitProgress>? progress, CancellationToken ct, bool background = false);
    Task<GitOpResult> PullAsync(PullMode mode, IProgress<GitProgress>? progress, CancellationToken ct);
    Task PushAsync(IProgress<GitProgress>? progress, CancellationToken ct, bool forceWithLease = false);
    Task FetchRefAsync(string refspec, IProgress<GitProgress>? progress, CancellationToken ct);
    void SetUpstream(string branch, string upstream);

    void CreateTag(string name, string sha, string? message);
    void DeleteTag(string name);
    Task PushTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct);
    Task DeleteRemoteTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct);

    RepoOperationState GetOperationState();
    Task<MergePreview> PreviewMergeAsync(string target, CancellationToken ct);
    Task<GitOpResult> MergeAsync(string target, MergeMode mode, IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> RebaseAsync(string onto, IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> CherryPickAsync(IReadOnlyList<string> shas, IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> RevertAsync(string sha, IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> ResetAsync(string sha, ResetMode mode, IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> ContinueOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> SkipOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct);
    Task<GitOpResult> AbortOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct);

    Task<ConflictFile> GetConflictFileAsync(string path, CancellationToken ct);
    Task ResolveFileAsync(string path, string content, CancellationToken ct);
    Task ResolveWithSideAsync(string path, ConflictSide side, CancellationToken ct);

    IReadOnlyList<RebaseEntry> GetRebaseEntries(string baseRev);
    Task<GitOpResult> InteractiveRebaseAsync(string baseRev, RebaseRequest request, IProgress<GitProgress>? progress, CancellationToken ct);

    Task<GitOpResult> CreateStashAsync(string? message, bool includeUntracked, CancellationToken ct);
    Task<GitOpResult> ApplyStashAsync(string sha, bool drop, CancellationToken ct);
    Task DropStashAsync(string sha, CancellationToken ct);

    IReadOnlyList<FileHistoryEntry> GetFileHistory(string path, int maxCount = 500);
    IReadOnlyList<BlameLine> GetBlame(string path, string? rev = null);

    IReadOnlyList<ReflogItem> GetReflog(int maxCount = 200);
    UndoPlan? GetUndoPlan();
    Task UndoAsync(CancellationToken ct);

    IReadOnlyList<SubmoduleInfo> GetSubmodules();
    Task UpdateSubmodulesAsync(string? path, IProgress<GitProgress>? progress, CancellationToken ct);

    Task<IReadOnlyList<WorktreeInfo>> GetWorktreesAsync(CancellationToken ct);
    Task AddWorktreeAsync(string path, string? newBranch, string? commitish, CancellationToken ct);
    Task RemoveWorktreeAsync(string path, bool force, CancellationToken ct);
    Task PruneWorktreesAsync(CancellationToken ct);
}
