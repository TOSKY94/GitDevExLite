using System.Diagnostics;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;
using ResetMode = GitDevExLite.Core.Services.ResetMode;

namespace GitDevExLite.Core.Tests;

public sealed class PowerToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly GitService _git = new();
    private readonly List<IDisposable> _extra = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private string? _main;

    public PowerToolsTests()
    {
        _dir = Path.Combine(_root, "main");
        Directory.CreateDirectory(_dir);
        Init(_dir);
        _git.Open(_dir);
    }

    public void Dispose()
    {
        _git.Dispose();
        foreach (var d in _extra) d.Dispose();
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private static void Init(string dir)
    {
        Repository.Init(dir);
        using var repo = new Repository(dir);
        repo.Config.Set("user.name", "Test");
        repo.Config.Set("user.email", "test@example.com");
    }

    private string Main => _main ??= _git.GetRefs().First(r => r is { IsHead: true, Kind: RefKind.LocalBranch }).Name;
    private string P(string file) => Path.Combine(_dir, file);
    private string Read(string file) => File.ReadAllText(P(file)).Replace("\r\n", "\n");

    private string Commit(string file, string content, string message, GitService? git = null, string? dir = null)
    {
        git ??= _git;
        File.WriteAllText(Path.Combine(dir ?? _dir, file), content);
        git.Stage(file);
        git.Commit(message);
        return git.GetCommits()[0].Sha;
    }

    private static void RunGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {err}");
    }

    // ---- stash -----------------------------------------------------------------

    [Fact]
    public async Task Stash_saves_local_changes_and_shows_its_diff()
    {
        Commit("f.txt", "one\n", "base");
        File.WriteAllText(P("f.txt"), "two\n");

        await _git.CreateStashAsync("work in progress", false, Ct);

        Assert.Empty(_git.GetStatus());
        Assert.Equal("one\n", Read("f.txt"));
        var stash = Assert.Single(_git.GetStashes());
        Assert.Contains("work in progress", stash.Message);

        Assert.Equal("f.txt", Assert.Single(_git.GetCommitChanges(stash.Sha)).Path);
        var diff = _git.GetCommitDiff(stash.Sha, "f.txt");
        Assert.Contains(diff.Hunks.SelectMany(h => h.Lines), l => l.Kind == DiffLineKind.Added && l.Text == "two");
    }

    [Fact]
    public async Task Apply_keeps_the_stash_and_pop_removes_it()
    {
        Commit("f.txt", "one\n", "base");
        File.WriteAllText(P("f.txt"), "two\n");
        await _git.CreateStashAsync(null, false, Ct);
        var sha = _git.GetStashes()[0].Sha;

        var applied = await _git.ApplyStashAsync(sha, drop: false, Ct);

        Assert.True(applied.Success);
        Assert.Equal("two\n", Read("f.txt"));
        Assert.Single(_git.GetStashes());

        await _git.CreateStashAsync("again", false, Ct);
        await _git.ApplyStashAsync(_git.GetStashes()[0].Sha, drop: true, Ct);
        Assert.Single(_git.GetStashes());
    }

    [Fact]
    public async Task A_stash_can_be_dropped_by_identity_even_when_indexes_shift()
    {
        Commit("f.txt", "one\n", "base");
        File.WriteAllText(P("f.txt"), "a\n");
        await _git.CreateStashAsync("first", false, Ct);
        var first = _git.GetStashes()[0].Sha;
        File.WriteAllText(P("f.txt"), "b\n");
        await _git.CreateStashAsync("second", false, Ct);

        await _git.DropStashAsync(first, Ct);

        var left = Assert.Single(_git.GetStashes());
        Assert.Contains("second", left.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => _git.DropStashAsync(first, Ct));
    }

    [Fact]
    public async Task Untracked_files_are_stashed_only_when_asked()
    {
        Commit("f.txt", "one\n", "base");
        File.WriteAllText(P("new.txt"), "untracked\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _git.CreateStashAsync(null, false, Ct));
        await _git.CreateStashAsync(null, true, Ct);

        Assert.False(File.Exists(P("new.txt")));
    }

    [Fact]
    public async Task Applying_a_conflicting_stash_reports_conflicts_and_keeps_the_stash()
    {
        Commit("f.txt", "one\n", "base");
        File.WriteAllText(P("f.txt"), "from stash\n");
        await _git.CreateStashAsync(null, false, Ct);
        Commit("f.txt", "from commit\n", "diverge");

        var result = await _git.ApplyStashAsync(_git.GetStashes()[0].Sha, drop: true, Ct);

        Assert.False(result.Success);
        Assert.Contains(_git.GetStatus(), s => s.Status == "Conflicted");
        Assert.Single(_git.GetStashes());
    }

    // ---- file history and blame ------------------------------------------------

    [Fact]
    public void File_history_lists_only_commits_touching_the_file_newest_first()
    {
        Commit("f.txt", "1\n", "f one");
        Commit("g.txt", "g\n", "g only");
        Commit("f.txt", "2\n", "f two");

        var history = _git.GetFileHistory("f.txt");

        Assert.Equal(new[] { "f two", "f one" }, history.Select(h => h.Commit.Summary));
    }

    [Fact]
    public void File_history_follows_a_rename()
    {
        Commit("old.txt", "content that is long enough to be detected as a rename\nline two\nline three\n", "create");
        File.Move(P("old.txt"), P("new.txt"));
        _git.Stage("old.txt");
        _git.Stage("new.txt");
        _git.Commit("rename");

        var history = _git.GetFileHistory("new.txt");

        Assert.Equal(new[] { "rename", "create" }, history.Select(h => h.Commit.Summary));
        Assert.Equal("old.txt", history[1].Path);
    }

    [Fact]
    public void Blame_attributes_each_line_to_the_commit_that_wrote_it()
    {
        var c1 = Commit("f.txt", "a\n", "one");
        var c2 = Commit("f.txt", "a\nb\n", "two");
        var c3 = Commit("f.txt", "a\nb\nc\n", "three");

        var blame = _git.GetBlame("f.txt");

        Assert.Equal(new[] { c1, c2, c3 }, blame.Select(b => b.Sha));
        Assert.Equal(new[] { "a", "b", "c" }, blame.Select(b => b.Text));
        Assert.Equal(new[] { 1, 2, 3 }, blame.Select(b => b.LineNumber));
        Assert.Equal("Test", blame[0].Author);
        Assert.Equal(2, _git.GetBlame("f.txt", c2).Count);
    }

    [Fact]
    public void Blame_rejects_missing_and_binary_files()
    {
        Commit("f.txt", "a\n", "one");
        File.WriteAllBytes(P("bin.dat"), new byte[] { 1, 0, 2, 0, 3 });
        _git.Stage("bin.dat");
        _git.Commit("binary");

        Assert.Throws<ArgumentException>(() => _git.GetBlame("missing.txt"));
        Assert.Throws<InvalidOperationException>(() => _git.GetBlame("bin.dat"));
        Assert.Throws<ArgumentException>(() => _git.GetBlame("../f.txt"));
    }

    // ---- reflog and undo -------------------------------------------------------

    [Fact]
    public void Reflog_lists_head_movements_newest_first()
    {
        Commit("f.txt", "1\n", "one");
        Commit("f.txt", "2\n", "two");

        var log = _git.GetReflog();

        Assert.StartsWith("commit", log[0].Message);
        Assert.Contains("two", log[0].Message);
        Assert.Equal(_git.GetCommits()[0].Sha, log[0].Sha);
        Assert.Equal(_git.GetCommits()[1].Sha, log[0].PreviousSha);
    }

    [Fact]
    public async Task Undo_moves_back_over_the_last_commit_and_a_second_undo_redoes_it()
    {
        Commit("f.txt", "1\n", "one");
        Commit("f.txt", "2\n", "two");
        var tip = _git.GetCommits()[0].Sha;

        await _git.UndoAsync(Ct);
        Assert.Equal("one", _git.GetCommits()[0].Summary);
        Assert.Equal("1\n", Read("f.txt"));

        await _git.UndoAsync(Ct);
        Assert.Equal(tip, _git.GetCommits()[0].Sha);
    }

    [Fact]
    public void Nothing_can_be_undone_after_the_initial_commit()
    {
        Commit("f.txt", "1\n", "one");

        Assert.Null(_git.GetUndoPlan());
    }

    [Fact]
    public async Task Undo_refuses_to_discard_uncommitted_changes()
    {
        Commit("f.txt", "1\n", "one");
        Commit("f.txt", "2\n", "two");
        File.WriteAllText(P("f.txt"), "dirty\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _git.UndoAsync(Ct));
        Assert.Equal("dirty\n", Read("f.txt"));
    }

    [Fact]
    public async Task Undoing_a_checkout_switches_back_instead_of_resetting()
    {
        var baseSha = Commit("f.txt", "1\n", "one");
        var main = Main;
        _git.CreateBranch("topic", baseSha, checkout: false);
        _git.Checkout("topic");

        var plan = _git.GetUndoPlan();
        Assert.Equal(main, plan!.CheckoutRef);

        await _git.UndoAsync(Ct);

        Assert.Equal(main, _git.GetRefs().First(r => r.IsHead).Name);
    }

    [Fact]
    public async Task Undoing_a_rebase_returns_to_the_pre_rebase_tip()
    {
        var baseSha = Commit("f.txt", "1\n", "base");
        var main = Main;
        _git.CreateBranch("feature", baseSha, checkout: true);
        Commit("g.txt", "g\n", "feature one");
        var featureTip = Commit("h.txt", "h\n", "feature two");
        _git.Checkout(main);
        Commit("m.txt", "m\n", "main work");
        _git.Checkout("feature");
        await _git.RebaseAsync(main, null, Ct);
        Assert.NotEqual(featureTip, Tip("feature"));

        await _git.UndoAsync(Ct);

        Assert.Equal(featureTip, Tip("feature"));
    }

    private string Tip(string branch) => _git.GetRefs().First(r => r.Name == branch).TargetSha;

    // ---- worktrees -------------------------------------------------------------

    [Fact]
    public void Worktree_porcelain_output_is_parsed()
    {
        const string text =
            "worktree C:/repo\nHEAD abc1234def\nbranch refs/heads/main\n\n" +
            "worktree C:/repo-wt\nHEAD 1111111\ndetached\nlocked reason\n\n" +
            "worktree C:/gone\nHEAD 2222222\nbranch refs/heads/old\nprunable gitdir file points to non-existent location\n";

        var list = GitService.ParseWorktrees(text);

        Assert.Equal(3, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal("main", list[0].Branch);
        Assert.True(list[1].IsDetached && list[1].IsLocked);
        Assert.True(list[2].IsPrunable);
        Assert.Equal("(detached 1111111)", list[1].Display);
    }

    [Fact]
    public async Task Worktrees_can_be_added_listed_and_removed()
    {
        Commit("f.txt", "1\n", "one");
        var wt = Path.Combine(_root, "wt-feature");

        await _git.AddWorktreeAsync(wt, "wt-branch", null, Ct);

        var list = await _git.GetWorktreesAsync(Ct);
        Assert.Equal(2, list.Count);
        Assert.Equal("wt-branch", list[1].Branch);
        Assert.True(File.Exists(Path.Combine(wt, "f.txt")));

        await _git.RemoveWorktreeAsync(wt, force: false, Ct);

        Assert.Single(await _git.GetWorktreesAsync(Ct));
        Assert.False(Directory.Exists(wt));
    }

    [Fact]
    public async Task An_existing_branch_can_be_checked_out_in_a_new_worktree()
    {
        var sha = Commit("f.txt", "1\n", "one");
        _git.CreateBranch("topic", sha, checkout: false);
        var wt = Path.Combine(_root, "wt-topic");

        await _git.AddWorktreeAsync(wt, null, "topic", Ct);

        Assert.Equal("topic", (await _git.GetWorktreesAsync(Ct))[1].Branch);
    }

    [Theory]
    [InlineData("-b")]
    [InlineData("has space")]
    public async Task Option_like_worktree_arguments_are_rejected(string name)
    {
        Commit("f.txt", "1\n", "one");

        await Assert.ThrowsAsync<ArgumentException>(() => _git.AddWorktreeAsync(Path.Combine(_root, "w"), name, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _git.AddWorktreeAsync(Path.Combine(_root, "w"), null, name, Ct));
    }

    // ---- submodules ------------------------------------------------------------

    [Fact]
    public async Task Submodules_are_listed_and_a_fresh_clone_can_initialise_them()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        Init(subDir);
        using (var sub = new GitService())
        {
            sub.Open(subDir);
            Commit("lib.txt", "library\n", "lib", sub, subDir);
        }

        Commit("f.txt", "1\n", "super base");
        RunGit(_dir, "-c", "protocol.file.allow=always", "submodule", "add", subDir.Replace('\\', '/'), "mod");
        RunGit(_dir, "commit", "-m", "add submodule");
        _git.Open(_dir);

        var mine = Assert.Single(_git.GetSubmodules());
        Assert.Equal("mod", mine.Path);
        Assert.True(mine.Initialized);

        var cloneDir = Path.Combine(_root, "clone");
        await GitClone.CloneAsync(_dir, cloneDir, null, Ct);
        using var clone = new GitService();
        clone.Open(cloneDir);

        Assert.False(Assert.Single(clone.GetSubmodules()).Initialized);

        // Git blocks local-path submodule clones unless the file transport is allowed explicitly.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
        try
        {
            await clone.UpdateSubmodulesAsync(null, null, Ct);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
        }

        Assert.True(Assert.Single(clone.GetSubmodules()).Initialized);
        Assert.True(File.Exists(Path.Combine(cloneDir, "mod", "lib.txt")));
    }
}
