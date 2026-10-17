using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface IGitService : IDisposable
{
    string? RepositoryPath { get; }
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
}
