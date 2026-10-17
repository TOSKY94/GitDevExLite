namespace GitDevExLite.Core.Services;

[Flags]
public enum RepoChange
{
    None = 0,
    /// <summary>Files or the index changed.</summary>
    WorkingTree = 1,
    /// <summary>HEAD or refs changed (commit, checkout, branch, tag, fetch).</summary>
    RepoState = 2
}

/// <summary>Watches a repository for edits made outside the app and raises one debounced, coalesced event.</summary>
public sealed class RepoWatcher : IDisposable
{
    private static readonly string[] IgnoredDirs = { "bin", "obj", "node_modules", ".vs", ".git" };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _workDir;
    private readonly string _gitDir;
    private readonly TimeSpan _delay;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _timer;
    private readonly object _gate = new();
    private RepoChange _pending;
    private int _paused;
    private bool _disposed;

    public event Action<RepoChange>? Changed;

    public RepoWatcher(string workingDirectory, string gitDirectory, TimeSpan? delay = null)
    {
        _workDir = Path.GetFullPath(workingDirectory);
        _gitDir = Path.GetFullPath(gitDirectory);
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        Watch(_workDir);
        // A linked worktree or submodule keeps its git directory elsewhere.
        if (!IsUnder(_gitDir, _workDir) && Directory.Exists(_gitDir)) Watch(_gitDir);
    }

    private void Watch(string root)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += (_, e) => Raise(Classify(e.FullPath));
        watcher.Created += (_, e) => Raise(Classify(e.FullPath));
        watcher.Deleted += (_, e) => Raise(Classify(e.FullPath));
        watcher.Renamed += (_, e) => Raise(Classify(e.OldFullPath) | Classify(e.FullPath));
        // Buffer overflow: events were lost, so assume everything changed.
        watcher.Error += (_, _) => Raise(RepoChange.WorkingTree | RepoChange.RepoState);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    public RepoChange Classify(string fullPath)
    {
        if (IsUnder(fullPath, _gitDir))
        {
            var rel = Path.GetRelativePath(_gitDir, fullPath).Replace('\\', '/');
            if (rel == "index") return RepoChange.WorkingTree;
            if (rel is "HEAD" or "packed-refs" or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD"
                || rel.StartsWith("refs/", StringComparison.Ordinal))
                return RepoChange.RepoState;
            return RepoChange.None;
        }

        if (!IsUnder(fullPath, _workDir)) return RepoChange.None;
        var segments = Path.GetRelativePath(_workDir, fullPath).Split('\\', '/');
        return segments.Any(s => IgnoredDirs.Contains(s, StringComparer.OrdinalIgnoreCase))
            ? RepoChange.None
            : RepoChange.WorkingTree;
    }

    /// <summary>Drops events until <see cref="Resume"/>; use around operations that refresh explicitly.</summary>
    public void Pause()
    {
        Interlocked.Increment(ref _paused);
        lock (_gate) _pending = RepoChange.None;
    }

    public void Resume()
    {
        Interlocked.Decrement(ref _paused);
        lock (_gate) _pending = RepoChange.None;
    }

    private void Raise(RepoChange change)
    {
        if (change == RepoChange.None || Volatile.Read(ref _paused) > 0 || _disposed) return;
        lock (_gate)
        {
            _pending |= change;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        RepoChange change;
        lock (_gate)
        {
            change = _pending;
            _pending = RepoChange.None;
        }
        if (change != RepoChange.None && Volatile.Read(ref _paused) == 0 && !_disposed)
            Changed?.Invoke(change);
    }

    private static bool IsUnder(string path, string dir)
    {
        var d = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(d, PathComparison) ||
               path.StartsWith(d + Path.DirectorySeparatorChar, PathComparison) ||
               path.StartsWith(d + Path.AltDirectorySeparatorChar, PathComparison);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
