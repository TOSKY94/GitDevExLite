using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class RepoWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();
    private readonly RepoWatcher _watcher;

    public RepoWatcherTests()
    {
        Directory.CreateDirectory(_dir);
        Repository.Init(_dir);
        using (var repo = new Repository(_dir))
        {
            repo.Config.Set("user.name", "Test");
            repo.Config.Set("user.email", "test@example.com");
        }
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "1\n");
        _git.Open(_dir);
        _git.Stage("a.txt");
        _git.Commit("first");
        _watcher = new RepoWatcher(_dir, _git.GitDirectory!, TimeSpan.FromMilliseconds(100));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _git.Dispose();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    private Task<RepoChange> NextChange()
    {
        var tcs = new TaskCompletionSource<RepoChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watcher.Changed += change => tcs.TrySetResult(change);
        return tcs.Task;
    }

    private static async Task<bool> Completes(Task task, int ms) =>
        await Task.WhenAny(task, Task.Delay(ms)) == task;

    [Fact]
    public async Task Editing_a_file_raises_a_working_tree_change()
    {
        var next = NextChange();
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "2\n");

        Assert.True(await Completes(next, 5000));
        Assert.True((await next).HasFlag(RepoChange.WorkingTree));
    }

    [Fact]
    public async Task A_burst_of_edits_is_coalesced_into_one_event()
    {
        var count = 0;
        _watcher.Changed += _ => Interlocked.Increment(ref count);
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(_dir, $"f{i}.txt"), "x");

        await Task.Delay(1000);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Creating_a_branch_raises_a_repo_state_change()
    {
        var next = NextChange();
        _git.CreateBranch("topic", _git.GetCommits()[0].Sha, checkout: false);

        Assert.True(await Completes(next, 5000));
        Assert.True((await next).HasFlag(RepoChange.RepoState));
    }

    [Fact]
    public async Task Ignored_build_folders_do_not_raise_events()
    {
        var next = NextChange();
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "out.dll"), "x");

        Assert.False(await Completes(next, 800));
    }

    [Fact]
    public async Task Paused_watcher_drops_events_until_resumed()
    {
        var next = NextChange();
        _watcher.Pause();
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "paused\n");
        Assert.False(await Completes(next, 800));

        _watcher.Resume();
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "resumed\n");
        Assert.True(await Completes(next, 5000));
    }

    [Theory]
    [InlineData("src/x.cs", RepoChange.WorkingTree)]
    [InlineData("bin/Debug/x.dll", RepoChange.None)]
    [InlineData("node_modules/p/i.js", RepoChange.None)]
    [InlineData(".git/HEAD", RepoChange.RepoState)]
    [InlineData(".git/refs/heads/main", RepoChange.RepoState)]
    [InlineData(".git/index", RepoChange.WorkingTree)]
    [InlineData(".git/index.lock", RepoChange.None)]
    [InlineData(".git/objects/ab/cdef", RepoChange.None)]
    public void Paths_are_classified(string relative, RepoChange expected) =>
        Assert.Equal(expected, _watcher.Classify(Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar))));
}
