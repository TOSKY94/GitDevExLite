using GitDevExLite.Core.Models;
using LibGit2Sharp;

namespace GitDevExLite.Core.Services;

public sealed class GitService : IGitService
{
    private Repository? _repo;

    public string? RepositoryPath => _repo?.Info.WorkingDirectory;

    private Repository Repo => _repo ?? throw new InvalidOperationException("No repository is open.");

    public void Open(string path)
    {
        var discovered = Repository.Discover(path)
            ?? throw new ArgumentException($"'{path}' is not inside a git repository.");
        _repo?.Dispose();
        _repo = new Repository(discovered);
    }

    public IReadOnlyList<RefInfo> GetRefs()
    {
        var result = new List<RefInfo>();
        foreach (var b in Repo.Branches)
        {
            if (b.Tip is null) continue;
            result.Add(new RefInfo(b.FriendlyName,
                b.IsRemote ? RefKind.RemoteBranch : RefKind.LocalBranch,
                b.Tip.Sha, b.IsCurrentRepositoryHead));
        }
        foreach (var t in Repo.Tags)
        {
            if (t.PeeledTarget is Commit c)
                result.Add(new RefInfo(t.FriendlyName, RefKind.Tag, c.Sha, false));
        }
        return result;
    }

    public IReadOnlyList<StashInfo> GetStashes() =>
        Repo.Stashes.Select((s, i) => new StashInfo(i, s.Message, s.WorkTree.Sha)).ToList();

    public IReadOnlyList<CommitInfo> GetCommits(int maxCount = 2000)
    {
        var tips = new List<object>();
        foreach (var r in Repo.Refs)
        {
            if (r.CanonicalName.StartsWith("refs/heads/") ||
                r.CanonicalName.StartsWith("refs/remotes/") ||
                r.CanonicalName.StartsWith("refs/tags/"))
                tips.Add(r);
        }
        if (Repo.Info.IsHeadDetached && Repo.Head.Tip is not null) tips.Add(Repo.Head.Tip);
        if (tips.Count == 0) return Array.Empty<CommitInfo>();

        var filter = new CommitFilter
        {
            IncludeReachableFrom = tips,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
        };

        return Repo.Commits.QueryBy(filter)
            .Take(maxCount)
            .Select(c => new CommitInfo(
                c.Sha, c.MessageShort, c.Message,
                c.Author.Name, c.Author.Email, c.Author.When,
                c.Parents.Select(p => p.Sha).ToList()))
            .ToList();
    }

    public IReadOnlyList<FileChange> GetStatus()
    {
        var list = new List<FileChange>();
        foreach (var e in Repo.RetrieveStatus(new StatusOptions()))
        {
            var s = e.State;
            if (s == FileStatus.Ignored || s == FileStatus.Unaltered) continue;
            var staged = s.HasFlag(FileStatus.NewInIndex) || s.HasFlag(FileStatus.ModifiedInIndex)
                      || s.HasFlag(FileStatus.DeletedFromIndex) || s.HasFlag(FileStatus.RenamedInIndex);
            list.Add(new FileChange(e.FilePath, staged, s.ToString()));
        }
        return list;
    }

    public void Stage(string path) => Commands.Stage(Repo, path);

    public void Unstage(string path) => Commands.Unstage(Repo, path);

    public void Commit(string message)
    {
        var sig = Repo.Config.BuildSignature(DateTimeOffset.Now)
            ?? throw new InvalidOperationException("Set user.name and user.email in your git config.");
        Repo.Commit(message, sig, sig);
    }

    public void Checkout(string branchName)
    {
        var branch = Repo.Branches[branchName]
            ?? throw new ArgumentException($"Branch '{branchName}' not found.");
        Commands.Checkout(Repo, branch);
    }

    public void Dispose() => _repo?.Dispose();
}
