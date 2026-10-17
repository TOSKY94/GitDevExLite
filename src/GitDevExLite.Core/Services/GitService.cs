using GitDevExLite.Core.Diff;
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

            if (s.HasFlag(FileStatus.NewInIndex)) list.Add(new FileChange(e.FilePath, true, "Added"));
            else if (s.HasFlag(FileStatus.DeletedFromIndex)) list.Add(new FileChange(e.FilePath, true, "Deleted"));
            else if (s.HasFlag(FileStatus.RenamedInIndex)) list.Add(new FileChange(e.FilePath, true, "Renamed"));
            else if (s.HasFlag(FileStatus.ModifiedInIndex) || s.HasFlag(FileStatus.TypeChangeInIndex))
                list.Add(new FileChange(e.FilePath, true, "Modified"));

            if (s.HasFlag(FileStatus.Conflicted)) list.Add(new FileChange(e.FilePath, false, "Conflicted"));
            else if (s.HasFlag(FileStatus.NewInWorkdir)) list.Add(new FileChange(e.FilePath, false, "Untracked"));
            else if (s.HasFlag(FileStatus.DeletedFromWorkdir)) list.Add(new FileChange(e.FilePath, false, "Deleted"));
            else if (s.HasFlag(FileStatus.ModifiedInWorkdir) || s.HasFlag(FileStatus.TypeChangeInWorkdir)
                  || s.HasFlag(FileStatus.RenamedInWorkdir))
                list.Add(new FileChange(e.FilePath, false, "Modified"));
        }
        return list;
    }

    public void Stage(string path) => Commands.Stage(Repo, path);

    public void Unstage(string path) => Commands.Unstage(Repo, path);

    public void Commit(string message, bool amend = false)
    {
        var sig = Repo.Config.BuildSignature(DateTimeOffset.Now)
            ?? throw new InvalidOperationException("Set user.name and user.email in your git config.");
        Repo.Commit(message, sig, sig, new CommitOptions { AmendPreviousCommit = amend });
    }

    public string? GetHeadMessage() => Repo.Head.Tip?.Message.TrimEnd();

    public void Checkout(string branchName)
    {
        var branch = Repo.Branches[branchName]
            ?? throw new ArgumentException($"Branch '{branchName}' not found.");
        Commands.Checkout(Repo, branch);
    }

    public IReadOnlyList<ChangedFile> GetCommitChanges(string sha)
    {
        var commit = Repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.");
        var changes = Repo.Diff.Compare<TreeChanges>(
            commit.Parents.FirstOrDefault()?.Tree, commit.Tree, DiffOptions);
        return changes
            .Select(c => new ChangedFile(c.Path, c.Status == ChangeKind.Renamed ? c.OldPath : null, c.Status.ToString()))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public FileDiff GetCommitDiff(string sha, string path)
    {
        var commit = Repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.");
        var patch = Repo.Diff.Compare<Patch>(
            commit.Parents.FirstOrDefault()?.Tree, commit.Tree, new[] { path }, null, DiffOptions);
        return ToFileDiff(path, patch);
    }

    public FileDiff GetWorkingDiff(string path, bool staged)
    {
        var patch = staged
            ? Repo.Diff.Compare<Patch>(Repo.Head.Tip?.Tree, DiffTargets.Index, new[] { path }, null, DiffOptions)
            : Repo.Diff.Compare<Patch>(new[] { path }, true, null, DiffOptions);
        return ToFileDiff(path, patch);
    }

    public void StageLines(FileDiff diff, IReadOnlySet<LineRef> lines) => ApplySelection(diff, lines, reverse: false);

    public void UnstageLines(FileDiff diff, IReadOnlySet<LineRef> lines) => ApplySelection(diff, lines, reverse: true);

    public void CreateBranch(string name, string sha, bool checkout)
    {
        var commit = Repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.");
        var branch = Repo.CreateBranch(name, commit);
        if (checkout) Commands.Checkout(Repo, branch);
    }

    public void RenameBranch(string oldName, string newName)
    {
        var branch = Repo.Branches[oldName] ?? throw new ArgumentException($"Branch '{oldName}' not found.");
        Repo.Branches.Rename(branch, newName);
    }

    public void DeleteBranch(string name)
    {
        var branch = Repo.Branches[name] ?? throw new ArgumentException($"Branch '{name}' not found.");
        if (branch.IsCurrentRepositoryHead)
            throw new InvalidOperationException("Cannot delete the checked-out branch.");
        Repo.Branches.Remove(branch);
    }

    public void CheckoutCommit(string sha)
    {
        var commit = Repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.");
        Commands.Checkout(Repo, commit);
    }

    private static readonly CompareOptions DiffOptions = new()
    {
        ContextLines = 3,
        Similarity = SimilarityOptions.Renames
    };

    private static FileDiff ToFileDiff(string path, Patch patch)
    {
        var entry = patch.FirstOrDefault();
        if (entry is null) return FileDiff.Empty(path);
        return PatchParser.Parse(path, entry.Patch,
            entry.Status == ChangeKind.Added, entry.Status == ChangeKind.Deleted);
    }

    private void ApplySelection(FileDiff diff, IReadOnlySet<LineRef> lines, bool reverse)
    {
        var patch = PatchBuilder.Build(diff, lines, reverse)
            ?? throw new InvalidOperationException("Nothing selected.");
        GitCli.ApplyToIndex(Repo.Info.WorkingDirectory, patch, reverse);
        // libgit2 caches the index in memory, so reopen to see the CLI's write.
        var gitDir = Repo.Info.Path;
        _repo!.Dispose();
        _repo = new Repository(gitDir);
    }

    public void Dispose() => _repo?.Dispose();
}
