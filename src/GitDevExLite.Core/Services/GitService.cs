using System.Text;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Models;
using LibGit2Sharp;

namespace GitDevExLite.Core.Services;

public sealed partial class GitService : IGitService
{
    private Repository? _repo;

    public string? RepositoryPath => _repo?.Info.WorkingDirectory;

    public string? GitDirectory => _repo?.Info.Path;

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
            var tracking = !b.IsRemote && b.IsTracking ? b.TrackingDetails : null;
            result.Add(new RefInfo(b.FriendlyName,
                b.IsRemote ? RefKind.RemoteBranch : RefKind.LocalBranch,
                b.Tip.Sha, b.IsCurrentRepositoryHead,
                tracking?.AheadBy, tracking?.BehindBy,
                tracking is null ? null : b.TrackedBranch?.FriendlyName));
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
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Enter a commit message.");
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

    public string? GetFileContent(string path, bool staged)
    {
        if (staged)
        {
            var entry = Repo.Index[path];
            if (entry is null) return null;
            var blob = Repo.Lookup<Blob>(entry.Id);
            if (blob is null || blob.IsBinary) return null;

            using var content = blob.GetContentStream();
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        var fullPath = Path.Combine(Repo.Info.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath)) return null;

        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Any(b => b == 0)) return null;
        return File.ReadAllText(fullPath);
    }

    public void StageLines(FileDiff diff, IReadOnlySet<LineRef> lines) => ApplySelection(diff, lines, reverse: false);

    public void UnstageLines(FileDiff diff, IReadOnlySet<LineRef> lines) => ApplySelection(diff, lines, reverse: true);

    public void CreateBranch(string name, string sha, bool checkout)
    {
        RequireSafeName(name);
        var commit = Repo.Lookup<Commit>(sha) ?? throw new ArgumentException($"Commit '{sha}' not found.");
        var branch = Repo.CreateBranch(name, commit);
        if (checkout) Commands.Checkout(Repo, branch);
    }

    public void RenameBranch(string oldName, string newName)
    {
        RequireSafeName(newName);
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
        Reopen();
    }

    private void Reopen()
    {
        var gitDir = Repo.Info.Path;
        _repo!.Dispose();
        _repo = new Repository(gitDir);
    }

    public IReadOnlyList<string> GetRemoteNames() => Repo.Network.Remotes.Select(r => r.Name).ToList();

    public string? GetRemoteUrl(string? remote = null)
    {
        remote ??= DefaultRemote();
        return remote is null ? null : Repo.Network.Remotes[remote]?.Url;
    }

    private string? DefaultRemote()
    {
        var names = GetRemoteNames();
        if (names.Count == 0) return null;
        var head = Repo.Head;
        if (head.IsTracking && head.RemoteName is { Length: > 0 } r && names.Contains(r)) return r;
        return names.Contains("origin") ? "origin" : names[0];
    }

    private static Dictionary<string, string> Env(bool background) =>
        // Background work must never pop up a sign-in window.
        background
            ? new() { ["GCM_INTERACTIVE"] = "never", ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes" }
            : new();

    private static void RequireSafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-') || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"'{name}' is not a valid name.");
    }

    private async Task RunRemoteAsync(IEnumerable<string> args, IProgress<GitProgress>? progress, CancellationToken ct, bool background = false)
    {
        var dir = Repo.Info.WorkingDirectory;
        try
        {
            await GitProcess.RunAsync(dir, args, progress, ct, Env(background));
        }
        finally
        {
            // Refs and the index were changed by another process.
            if (_repo is not null) Reopen();
        }
    }

    public Task FetchAsync(bool prune, IProgress<GitProgress>? progress, CancellationToken ct, bool background = false)
    {
        if (GetRemoteNames().Count == 0) throw new InvalidOperationException("This repository has no remotes.");
        var args = new List<string> { "fetch", "--all", "--progress" };
        if (prune) args.Add("--prune");
        return RunRemoteAsync(args, progress, ct, background);
    }

    public Task<GitOpResult> PullAsync(PullMode mode, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        if (Repo.Info.IsHeadDetached) throw new InvalidOperationException("HEAD is detached; check out a branch first.");
        if (!Repo.Head.IsTracking)
            throw new InvalidOperationException("This branch has no upstream. Push it or set an upstream first.");

        var args = new List<string> { "pull", "--progress" };
        args.AddRange(mode switch
        {
            PullMode.Rebase => new[] { "--rebase" },
            PullMode.FastForwardOnly => new[] { "--ff-only" },
            _ => new[] { "--no-rebase", "--no-edit" }
        });
        // A pull that stops on conflicts leaves a merge or rebase to resolve, so it reports a paused result.
        return RunOperationAsync(args, progress, ct, Env(false));
    }

    public Task PushAsync(IProgress<GitProgress>? progress, CancellationToken ct, bool forceWithLease = false)
    {
        if (Repo.Info.IsHeadDetached) throw new InvalidOperationException("HEAD is detached; check out a branch first.");
        var head = Repo.Head;
        var args = new List<string> { "push", "--progress" };
        if (forceWithLease) args.Add("--force-with-lease");
        if (!head.IsTracking)
        {
            var remote = DefaultRemote() ?? throw new InvalidOperationException("This repository has no remotes.");
            RequireSafeName(head.FriendlyName);
            args.AddRange(new[] { "--set-upstream", remote, head.FriendlyName });
        }
        return RunRemoteAsync(args, progress, ct);
    }

    public Task FetchRefAsync(string refspec, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        RequireSafeName(refspec);
        var remote = DefaultRemote() ?? throw new InvalidOperationException("This repository has no remotes.");
        return RunRemoteAsync(new[] { "fetch", "--progress", remote, refspec }, progress, ct);
    }

    public void SetUpstream(string branch, string upstream)
    {
        var local = Repo.Branches[branch] ?? throw new ArgumentException($"Branch '{branch}' not found.");
        var remote = Repo.Branches[upstream] ?? throw new ArgumentException($"Remote branch '{upstream}' not found.");
        if (!remote.IsRemote) throw new ArgumentException($"'{upstream}' is not a remote branch.");
        Repo.Branches.Update(local, b => b.TrackedBranch = remote.CanonicalName);
    }

    public void CreateTag(string name, string sha, string? message)
    {
        RequireSafeName(name);
        if (string.IsNullOrWhiteSpace(message))
        {
            Repo.ApplyTag(name, sha);
            return;
        }
        var sig = Repo.Config.BuildSignature(DateTimeOffset.Now)
            ?? throw new InvalidOperationException("Set user.name and user.email in your git config.");
        Repo.ApplyTag(name, sha, sig, message.Trim());
    }

    public void DeleteTag(string name) => Repo.Tags.Remove(name);

    public Task PushTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        RequireSafeName(name);
        var remote = DefaultRemote() ?? throw new InvalidOperationException("This repository has no remotes.");
        return RunRemoteAsync(new[] { "push", "--progress", remote, $"refs/tags/{name}" }, progress, ct);
    }

    public Task DeleteRemoteTagAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        RequireSafeName(name);
        var remote = DefaultRemote() ?? throw new InvalidOperationException("This repository has no remotes.");
        return RunRemoteAsync(new[] { "push", "--progress", remote, "--delete", $"refs/tags/{name}" }, progress, ct);
    }

    public void Dispose() => _repo?.Dispose();

    // ---- merge, rebase, cherry-pick, revert, reset ---------------------------

    public RepoOperationState GetOperationState() => RepoOperationState.Read(GitDirectory);

    private string ResolveCommit(string rev)
    {
        RequireSafeName(rev);
        return (Repo.Lookup<Commit>(rev) ?? throw new ArgumentException($"'{rev}' does not name a commit.")).Sha;
    }

    private static async Task<bool> IsAncestorAsync(string dir, string ancestor, string descendant, CancellationToken ct)
    {
        var r = await GitProcess.ExecuteAsync(dir, new[] { "merge-base", "--is-ancestor", ancestor, descendant }, null, ct);
        return r.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new GitCommandException(r.StdErr, r.ExitCode)
        };
    }

    public async Task<MergePreview> PreviewMergeAsync(string target, CancellationToken ct)
    {
        var sha = ResolveCommit(target);
        var head = Repo.Head.Tip ?? throw new InvalidOperationException("The repository has no commits to merge into.");
        var dir = Repo.Info.WorkingDirectory;
        var none = Array.Empty<string>();

        if (await IsAncestorAsync(dir, sha, head.Sha, ct)) return new(true, true, false, none, null);
        if (await IsAncestorAsync(dir, head.Sha, sha, ct)) return new(true, false, true, none, null);
        if (!await GitVersion.AtLeastAsync(2, 38))
            return new(false, false, false, none, "Conflict preview needs git 2.38 or newer.");

        // Simulates the merge in the object database only; the working tree and index are untouched.
        var r = await GitProcess.ExecuteAsync(dir,
            new[] { "merge-tree", "--write-tree", "--name-only", "--no-messages", head.Sha, sha }, null, ct);
        if (r.ExitCode == 0) return new(true, false, false, none, null);
        if (r.ExitCode != 1) throw new GitCommandException(string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr, r.ExitCode);

        var files = r.StdOut.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).TakeWhile(l => l.Length > 0).Distinct().ToList();
        return new(true, false, false, files, null);
    }

    public Task<GitOpResult> MergeAsync(string target, MergeMode mode, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        ResolveCommit(target);
        var args = new List<string> { "merge", "--no-edit" };
        switch (mode)
        {
            case MergeMode.NoFastForward: args.Add("--no-ff"); break;
            case MergeMode.FastForwardOnly: args.Add("--ff-only"); break;
            case MergeMode.Squash: args.Add("--squash"); break;
        }
        args.Add(target);
        return RunOperationAsync(args, progress, ct);
    }

    public Task<GitOpResult> RebaseAsync(string onto, IProgress<GitProgress>? progress, CancellationToken ct) =>
        RunOperationAsync(new[] { "rebase", ResolveCommit(onto) }, progress, ct);

    public Task<GitOpResult> CherryPickAsync(IReadOnlyList<string> shas, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        if (shas.Count == 0) throw new ArgumentException("Select at least one commit.");
        var args = new List<string> { "cherry-pick" };
        AddMainline(args, shas);
        args.AddRange(shas.Select(ResolveCommit));
        return RunOperationAsync(args, progress, ct);
    }

    public Task<GitOpResult> RevertAsync(string sha, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        var args = new List<string> { "revert", "--no-edit" };
        AddMainline(args, new[] { sha });
        args.Add(ResolveCommit(sha));
        return RunOperationAsync(args, progress, ct);
    }

    // Merge commits need a parent to diff against; the first parent is what "undo this merge" normally means.
    private void AddMainline(List<string> args, IReadOnlyList<string> shas)
    {
        var merges = shas.Count(s => Repo.Lookup<Commit>(s)?.Parents.Count() > 1);
        if (merges == 0) return;
        if (merges != shas.Count) throw new ArgumentException("Merge commits cannot be combined with ordinary commits.");
        args.AddRange(new[] { "-m", "1" });
    }

    public Task<GitOpResult> ResetAsync(string sha, ResetMode mode, IProgress<GitProgress>? progress, CancellationToken ct) =>
        RunOperationAsync(new[] { "reset", $"--{mode.ToString().ToLowerInvariant()}", ResolveCommit(sha) }, progress, ct);

    public Task<GitOpResult> ContinueOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct) =>
        RunOperationAsync(new[] { OperationVerb(GetOperationState()), "--continue" }, progress, ct);

    public Task<GitOpResult> SkipOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct)
    {
        var state = GetOperationState();
        if (!state.CanSkip) throw new InvalidOperationException("This operation cannot be skipped.");
        return RunOperationAsync(new[] { OperationVerb(state), "--skip" }, progress, ct);
    }

    public Task<GitOpResult> AbortOperationAsync(IProgress<GitProgress>? progress, CancellationToken ct) =>
        RunOperationAsync(new[] { OperationVerb(GetOperationState()), "--abort" }, progress, ct);

    private static string OperationVerb(RepoOperationState state) => state.Kind switch
    {
        RepoOperation.Merge => "merge",
        RepoOperation.Rebase => "rebase",
        RepoOperation.CherryPick => "cherry-pick",
        RepoOperation.Revert => "revert",
        _ => throw new InvalidOperationException("No merge, rebase, cherry-pick or revert is in progress.")
    };

    /// <summary>Runs a history-changing command. A stop for conflicts or an edit is a result, not an error.</summary>
    private async Task<GitOpResult> RunOperationAsync(
        IEnumerable<string> args, IProgress<GitProgress>? progress, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var dir = Repo.Info.WorkingDirectory;
        GitResult result;
        try
        {
            result = await GitProcess.ExecuteAsync(dir, args, progress, ct, env);
        }
        finally
        {
            // Refs, index and files were rewritten by another process.
            if (_repo is not null) Reopen();
        }

        var output = result.Combined;
        var paused = GetOperationState().InProgress;
        if (!paused) DeleteRebaseScratch();
        if (result.ExitCode == 0) return new(!paused, paused, output);
        if (paused) return new(false, true, output);

        var message = string.IsNullOrWhiteSpace(output) ? $"git exited with code {result.ExitCode}." : output;
        throw GitAuthFailure.IsAuthFailure(result.StdErr)
            ? new GitAuthException(message, result.ExitCode)
            : new GitCommandException(message, result.ExitCode);
    }

    // ---- conflicts -------------------------------------------------------------

    private string RequireRepoPath(string path)
    {
        var segments = path.Split('/', '\\');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || segments.Contains(".."))
            throw new ArgumentException($"'{path}' is not a path inside the repository.");
        return Path.GetFullPath(Path.Combine(Repo.Info.WorkingDirectory, path));
    }

    private static async Task<string?> ReadStageAsync(string dir, int stage, string path, CancellationToken ct)
    {
        var r = await GitProcess.ExecuteAsync(dir, new[] { "show", $":{stage}:{path}" }, null, ct);
        return r.ExitCode == 0 ? r.StdOut : null;
    }

    public async Task<ConflictFile> GetConflictFileAsync(string path, CancellationToken ct)
    {
        var full = RequireRepoPath(path);
        var dir = Repo.Info.WorkingDirectory;
        var baseText = await ReadStageAsync(dir, 1, path, ct);
        var ours = await ReadStageAsync(dir, 2, path, ct);
        var theirs = await ReadStageAsync(dir, 3, path, ct);

        var bytes = File.Exists(full) ? await File.ReadAllBytesAsync(full, ct) : Array.Empty<byte>();
        var binary = Array.IndexOf(bytes, (byte)0) >= 0 || new[] { baseText, ours, theirs }.Any(t => t?.Contains('\0') == true);
        return new ConflictFile(path, baseText, ours, theirs, binary ? "" : Encoding.UTF8.GetString(bytes), binary);
    }

    public async Task ResolveFileAsync(string path, string content, CancellationToken ct)
    {
        var full = RequireRepoPath(path);
        var crlf = File.Exists(full) && File.ReadAllText(full).Contains("\r\n");
        var text = content.Replace("\r\n", "\n");
        if (crlf) text = text.Replace("\n", "\r\n");
        await File.WriteAllTextAsync(full, text, new UTF8Encoding(false), ct);
        await RunRemoteAsync(new[] { "add", "--", path }, null, ct);
    }

    public async Task ResolveWithSideAsync(string path, ConflictSide side, CancellationToken ct)
    {
        RequireRepoPath(path);
        var dir = Repo.Info.WorkingDirectory;
        var stage = side == ConflictSide.Ours ? "2" : "3";

        var listing = await GitProcess.RunAsync(dir, new[] { "ls-files", "-u", "--", path }, null, ct);
        var exists = listing.Split('\n').Any(l => l.Split('\t')[0].Split(' ') is { Length: 3 } p && p[2] == stage);

        // A side that deleted the file wins by removing it.
        if (!exists)
        {
            await RunRemoteAsync(new[] { "rm", "--quiet", "--", path }, null, ct);
            return;
        }
        await GitProcess.RunAsync(dir, new[] { "checkout", side == ConflictSide.Ours ? "--ours" : "--theirs", "--", path }, null, ct);
        await RunRemoteAsync(new[] { "add", "--", path }, null, ct);
    }

    // ---- interactive rebase ----------------------------------------------------

    private string RebaseScratch => Path.Combine(Repo.Info.Path, "gdx-rebase");

    private void DeleteRebaseScratch()
    {
        try
        {
            if (_repo is not null && Directory.Exists(RebaseScratch)) Directory.Delete(RebaseScratch, recursive: true);
        }
        catch (IOException)
        {
            // Left over scratch files are harmless and replaced by the next rebase.
        }
    }

    public IReadOnlyList<RebaseEntry> GetRebaseEntries(string baseRev)
    {
        var baseSha = ResolveCommit(baseRev);
        var head = Repo.Head.Tip ?? throw new InvalidOperationException("The repository has no commits.");
        if (Repo.ObjectDatabase.FindMergeBase(Repo.Lookup<Commit>(baseSha), head)?.Sha != baseSha)
            throw new InvalidOperationException("That commit is not an ancestor of the current branch.");

        var commits = Repo.Commits.QueryBy(new LibGit2Sharp.CommitFilter
        {
            IncludeReachableFrom = head,
            ExcludeReachableFrom = baseSha,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        }).ToList();

        if (commits.Count == 0) throw new InvalidOperationException("There are no commits after that one.");
        if (commits.Any(c => c.Parents.Count() > 1))
            throw new InvalidOperationException("The range contains a merge commit, which interactive rebase would flatten.");

        return commits.Select(c => new RebaseEntry(RebaseAction.Pick, c.Sha, c.MessageShort, c.Message)).ToList();
    }

    public async Task<GitOpResult> InteractiveRebaseAsync(
        string baseRev, RebaseRequest request, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        if (RebasePlanner.Validate(request.Entries) is { } error) throw new ArgumentException(error);

        var baseSha = ResolveCommit(baseRev);
        var expected = GetRebaseEntries(baseSha).Select(e => e.Sha).ToHashSet();
        if (!expected.SetEquals(request.Entries.Select(e => e.Sha)))
            throw new InvalidOperationException("The history changed since the list was prepared. Reopen the rebase.");

        var plan = RebasePlanner.Build(request.Entries, request.Messages);
        var dir = RebaseScratch;
        DeleteRebaseScratch();
        Directory.CreateDirectory(dir);

        var todo = plan.Todo;
        for (var i = 0; i < plan.Messages.Count; i++)
        {
            var file = Path.Combine(dir, $"msg{i}.txt");
            await File.WriteAllTextAsync(file, plan.Messages[i], new UTF8Encoding(false), ct);
            todo = todo.Replace($"{{{{MSG:{i}}}}}", ShellQuote(file));
        }
        var todoFile = Path.Combine(dir, "todo.txt");
        await File.WriteAllTextAsync(todoFile, todo, new UTF8Encoding(false), ct);

        // Git runs this through sh with the todo path appended, so it simply swaps in our prepared list.
        var env = new Dictionary<string, string> { ["GIT_SEQUENCE_EDITOR"] = $"cp -f {ShellQuote(todoFile)}" };
        try
        {
            return await RunOperationAsync(new[] { "rebase", "-i", baseSha }, progress, ct, env);
        }
        catch
        {
            DeleteRebaseScratch();
            throw;
        }
    }

    private static string ShellQuote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";
}
