using System.Text.RegularExpressions;
using GitDevExLite.Core.Models;
using LibGit2Sharp;

namespace GitDevExLite.Core.Services;

public sealed partial class GitService
{
    [GeneratedRegex(@"^checkout: moving from (?<from>\S+) to (?<to>\S+)$")]
    private static partial Regex CheckoutEntry();

    private static CommitInfo ToInfo(Commit c) => new(
        c.Sha, c.MessageShort, c.Message, c.Author.Name, c.Author.Email, c.Author.When,
        c.Parents.Select(p => p.Sha).ToList());

    // ---- stash -----------------------------------------------------------------

    private string StashRef(string sha)
    {
        var index = Repo.Stashes.Select((s, i) => (s, i)).FirstOrDefault(p => p.s.WorkTree.Sha == sha);
        if (index.s is null) throw new ArgumentException("That stash no longer exists.");
        return $"stash@{{{index.i}}}";
    }

    public async Task<GitOpResult> CreateStashAsync(string? message, bool includeUntracked, CancellationToken ct)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(new[] { "-m", message.Trim() });

        var result = await RunGitAsync(args, ct);
        if (result.ExitCode != 0)
            throw new GitCommandException(FirstNonEmpty(result.StdErr, result.StdOut, "git stash failed."), result.ExitCode);
        if (result.StdOut.Contains("No local changes to save", StringComparison.Ordinal))
            throw new InvalidOperationException("There are no local changes to stash.");
        return new GitOpResult(true, false, result.Combined);
    }

    public async Task<GitOpResult> ApplyStashAsync(string sha, bool drop, CancellationToken ct)
    {
        var result = await RunGitAsync(new[] { "stash", drop ? "pop" : "apply", StashRef(sha) }, ct);
        if (result.ExitCode == 0) return new GitOpResult(true, false, result.Combined);

        // Conflicts leave the stash in place and the files marked; report them rather than fail.
        if (GetStatus().Any(s => s.Status == "Conflicted")) return new GitOpResult(false, false, result.Combined);
        throw new GitCommandException(FirstNonEmpty(result.Combined, "git stash failed."), result.ExitCode);
    }

    public async Task DropStashAsync(string sha, CancellationToken ct)
    {
        var result = await RunGitAsync(new[] { "stash", "drop", StashRef(sha) }, ct);
        if (result.ExitCode != 0) throw new GitCommandException(FirstNonEmpty(result.Combined, "git stash drop failed."), result.ExitCode);
    }

    // ---- history and blame -----------------------------------------------------

    public IReadOnlyList<FileHistoryEntry> GetFileHistory(string path, int maxCount = 500)
    {
        RequireRepoPath(path);
        var current = path.Replace('\\', '/');
        var results = new List<FileHistoryEntry>();
        if (Repo.Head.Tip is null) return results;

        var walk = Repo.Commits.QueryBy(new LibGit2Sharp.CommitFilter
        {
            IncludeReachableFrom = Repo.Head.Tip,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
        });

        foreach (var commit in walk)
        {
            if (results.Count >= maxCount) break;
            if (commit.Tree[current] is not { } here) continue;

            var parent = commit.Parents.FirstOrDefault();
            var before = parent?.Tree[current];
            if (before is not null)
            {
                if (here.Target.Id != before.Target.Id) results.Add(new(ToInfo(commit), current));
                continue;
            }

            // The file appears here: either it was created, or it was renamed from another path.
            if (parent is not null)
            {
                var changes = Repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree, DiffOptions);
                var moved = changes.FirstOrDefault(c => c.Status == ChangeKind.Renamed && c.Path == current);
                results.Add(new(ToInfo(commit), current));
                if (moved is not null) current = moved.OldPath;
                continue;
            }
            results.Add(new(ToInfo(commit), current));
        }
        return results;
    }

    public IReadOnlyList<BlameLine> GetBlame(string path, string? rev = null)
    {
        RequireRepoPath(path);
        var commit = rev is null
            ? Repo.Head.Tip ?? throw new InvalidOperationException("The repository has no commits.")
            : Repo.Lookup<Commit>(ResolveCommit(rev));
        var normalized = path.Replace('\\', '/');
        if (commit.Tree[normalized]?.Target is not Blob blob)
            throw new ArgumentException($"'{path}' does not exist in that commit.");
        if (blob.IsBinary) throw new InvalidOperationException("Blame is not available for binary files.");

        var lines = blob.GetContentText().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var result = new List<BlameLine>(lines.Count);
        foreach (var hunk in Repo.Blame(normalized, new BlameOptions { StartingAt = commit }))
        {
            for (var k = 0; k < hunk.LineCount; k++)
            {
                var n = hunk.FinalStartLineNumber + k;
                if (n < 0 || n >= lines.Count) continue;
                result.Add(new BlameLine(n + 1, hunk.FinalCommit.Sha, hunk.FinalSignature.Name,
                    hunk.FinalSignature.When, hunk.FinalCommit.MessageShort, lines[n]));
            }
        }
        return result.OrderBy(l => l.LineNumber).ToList();
    }

    // ---- reflog and undo -------------------------------------------------------

    public IReadOnlyList<ReflogItem> GetReflog(int maxCount = 200) =>
        Repo.Refs.Log(Repo.Refs.Head)
            .Take(maxCount)
            .Select((e, i) => new ReflogItem(i, e.To.Sha, e.From.Sha, e.Message, e.Committer.When))
            .ToList();

    public UndoPlan? GetUndoPlan()
    {
        var log = GetReflog(100);
        if (log.Count == 0) return null;
        var latest = log[0];

        if (CheckoutEntry().Match(latest.Message) is { Success: true } m)
        {
            var from = m.Groups["from"].Value;
            return new UndoPlan(latest.Message, $"Switch back to '{from}'.", null, from);
        }

        var entry = latest;
        // A rebase writes one entry per replayed commit; undoing it means returning to where it began.
        if (latest.Message.StartsWith("rebase", StringComparison.Ordinal))
            entry = log.FirstOrDefault(e => e.Message.StartsWith("rebase (start)", StringComparison.Ordinal)) ?? latest;

        if (entry.PreviousSha.All(c => c == '0')) return null;
        var branch = Repo.Info.IsHeadDetached ? "HEAD" : Repo.Head.FriendlyName;
        return new UndoPlan(latest.Message,
            $"Move '{branch}' back to {entry.PreviousSha[..7]}, discarding later commits and uncommitted changes.",
            entry.PreviousSha, null);
    }

    public async Task UndoAsync(CancellationToken ct)
    {
        var plan = GetUndoPlan() ?? throw new InvalidOperationException("There is nothing to undo.");
        if (GetOperationState().InProgress)
            throw new InvalidOperationException("Finish or abort the operation in progress first.");
        if (GetStatus().Any(s => s.Status != "Untracked"))
            throw new InvalidOperationException("Commit or stash your changes first; undoing would discard them.");

        if (plan.CheckoutRef is { } target)
        {
            RequireSafeName(target);
            await RunOperationAsync(new[] { "checkout", target }, null, ct);
            return;
        }
        await ResetAsync(plan.TargetSha!, ResetMode.Hard, null, ct);
    }

    // ---- submodules ------------------------------------------------------------

    public IReadOnlyList<SubmoduleInfo> GetSubmodules() =>
        Repo.Submodules.Select(s =>
        {
            var status = s.RetrieveStatus();
            const SubmoduleStatus changed = SubmoduleStatus.WorkDirModified | SubmoduleStatus.IndexModified
                | SubmoduleStatus.WorkDirFilesModified | SubmoduleStatus.WorkDirAdded | SubmoduleStatus.WorkDirDeleted;
            return new SubmoduleInfo(s.Name, s.Path, s.Url,
                !status.HasFlag(SubmoduleStatus.WorkDirUninitialized),
                (status & changed) != 0,
                s.IndexCommitId?.Sha, s.WorkDirCommitId?.Sha);
        }).OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();

    public Task UpdateSubmodulesAsync(string? path, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "submodule", "update", "--init", "--recursive" };
        if (path is not null)
        {
            RequireRepoPath(path);
            args.AddRange(new[] { "--", path });
        }
        return RunRemoteAsync(args, progress, ct);
    }

    // ---- worktrees -------------------------------------------------------------

    public async Task<IReadOnlyList<WorktreeInfo>> GetWorktreesAsync(CancellationToken ct)
    {
        var output = await GitProcess.RunAsync(Repo.Info.WorkingDirectory, new[] { "worktree", "list", "--porcelain" }, null, ct);
        return ParseWorktrees(output);
    }

    public static IReadOnlyList<WorktreeInfo> ParseWorktrees(string porcelain)
    {
        var list = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null;
        bool detached = false, locked = false, prunable = false;

        void Flush()
        {
            if (path is not null)
                list.Add(new WorktreeInfo(path, head, branch, list.Count == 0, detached, locked, prunable));
            path = head = branch = null;
            detached = locked = prunable = false;
        }

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "detached") detached = true;
            else if (line.StartsWith("locked", StringComparison.Ordinal)) locked = true;
            else if (line.StartsWith("prunable", StringComparison.Ordinal)) prunable = true;
        }
        Flush();
        return list;
    }

    public async Task AddWorktreeAsync(string path, string? newBranch, string? commitish, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a folder for the worktree.");
        var args = new List<string> { "worktree", "add" };
        if (!string.IsNullOrWhiteSpace(newBranch))
        {
            RequireSafeName(newBranch);
            args.AddRange(new[] { "-b", newBranch });
        }
        args.AddRange(new[] { "--", Path.GetFullPath(path) });
        if (!string.IsNullOrWhiteSpace(commitish))
        {
            RequireSafeName(commitish);
            args.Add(commitish);
        }

        var result = await RunGitAsync(args, ct);
        if (result.ExitCode != 0) throw new GitCommandException(FirstNonEmpty(result.StdErr, result.StdOut, "git worktree add failed."), result.ExitCode);
    }

    public async Task RemoveWorktreeAsync(string path, bool force, CancellationToken ct)
    {
        var args = new List<string> { "worktree", "remove" };
        if (force) args.Add("--force");
        args.AddRange(new[] { "--", Path.GetFullPath(path) });

        var result = await RunGitAsync(args, ct);
        if (result.ExitCode != 0) throw new GitCommandException(FirstNonEmpty(result.StdErr, result.StdOut, "git worktree remove failed."), result.ExitCode);
    }

    public async Task PruneWorktreesAsync(CancellationToken ct) =>
        await GitProcess.RunAsync(Repo.Info.WorkingDirectory, new[] { "worktree", "prune" }, null, ct);

    // ---- helpers ---------------------------------------------------------------

    private async Task<GitResult> RunGitAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var dir = Repo.Info.WorkingDirectory;
        try
        {
            return await GitProcess.ExecuteAsync(dir, args, null, ct);
        }
        finally
        {
            if (_repo is not null) Reopen();
        }
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}
