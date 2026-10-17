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
    void Commit(string message);
    void Checkout(string branchName);
}
