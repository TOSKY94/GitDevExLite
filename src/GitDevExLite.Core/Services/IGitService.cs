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

    IReadOnlyList<ChangedFile> GetCommitChanges(string sha);
    FileDiff GetCommitDiff(string sha, string path);
    FileDiff GetWorkingDiff(string path, bool staged);
    void StageLines(FileDiff diff, IReadOnlySet<LineRef> lines);
    void UnstageLines(FileDiff diff, IReadOnlySet<LineRef> lines);

    void CreateBranch(string name, string sha, bool checkout);
    void RenameBranch(string oldName, string newName);
    void DeleteBranch(string name);
    void CheckoutCommit(string sha);

    IReadOnlyList<string> GetRemoteNames();
    string? GetRemoteUrl(string? remote = null);
    Task FetchAsync(bool prune, IProgress<GitProgress>? progress, CancellationToken ct, bool background = false);
    Task PullAsync(PullMode mode, IProgress<GitProgress>? progress, CancellationToken ct);
    Task PushAsync(IProgress<GitProgress>? progress, CancellationToken ct);
    void SetUpstream(string branch, string upstream);

    void CreateTag(string name, string sha, string? message);
    void DeleteTag(string name);
    Task PushTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct);
    Task DeleteRemoteTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct);
}
