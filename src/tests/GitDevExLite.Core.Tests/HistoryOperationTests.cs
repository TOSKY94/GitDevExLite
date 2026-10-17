using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;
using ResetMode = GitDevExLite.Core.Services.ResetMode;

namespace GitDevExLite.Core.Tests;

public sealed class HistoryOperationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public HistoryOperationTests()
    {
        Directory.CreateDirectory(_dir);
        Repository.Init(_dir);
        using (var repo = new Repository(_dir))
        {
            repo.Config.Set("user.name", "Test");
            repo.Config.Set("user.email", "test@example.com");
        }
        _git.Open(_dir);
    }

    public void Dispose()
    {
        _git.Dispose();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    private string Main => _main ??= _git.GetRefs().First(r => r is { IsHead: true, Kind: RefKind.LocalBranch }).Name;

    private string? _main;

    private string Path_(string file) => Path.Combine(_dir, file);

    // Windows checkouts may apply autocrlf.
    private string Read(string file) => File.ReadAllText(Path_(file)).Replace("\r\n", "\n");

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(Path_(file), content);
        _git.Stage(file);
        _git.Commit(message);
        return _git.GetCommits()[0].Sha;
    }

    /// <summary>Base commit on main, then main and "feature" each change f.txt's first line differently (a conflict) when <paramref name="conflict"/>.</summary>
    private (string Base, string MainTip, string FeatureTip) Diverge(bool conflict)
    {
        var baseSha = Commit("f.txt", "one\ntwo\nthree\n", "base");
        var main = Main;
        _git.CreateBranch("feature", baseSha, checkout: true);
        var featureTip = conflict
            ? Commit("f.txt", "feature\ntwo\nthree\n", "feature change")
            : Commit("g.txt", "from feature\n", "feature change");
        _git.Checkout(main);
        var mainTip = conflict
            ? Commit("f.txt", "main\ntwo\nthree\n", "main change")
            : Commit("h.txt", "from main\n", "main change");
        return (baseSha, mainTip, featureTip);
    }

    private static int Parents(CommitInfo c) => c.ParentShas.Count;

    // ---- state ----------------------------------------------------------------

    [Fact]
    public void Clean_repo_has_no_operation_in_progress()
    {
        Commit("f.txt", "x\n", "base");
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
    }

    [Theory]
    [InlineData("git version 2.43.0.windows.1", 2, 43)]
    [InlineData("git version 2.56.0", 2, 56)]
    public void Git_version_is_parsed(string text, int major, int minor) =>
        Assert.Equal((major, minor), GitVersion.Parse(text));

    // ---- merge preview ---------------------------------------------------------

    [Fact]
    public async Task Preview_reports_a_clean_merge_without_touching_the_repo()
    {
        var (_, mainTip, _) = Diverge(conflict: false);

        var preview = await _git.PreviewMergeAsync("feature", Ct);

        Assert.True(preview.CanPreview);
        Assert.True(preview.IsClean);
        Assert.False(preview.IsFastForward);
        Assert.Equal(mainTip, _git.GetCommits().First(c => c.Summary == "main change").Sha);
        Assert.Empty(_git.GetStatus());
    }

    [Fact]
    public async Task Preview_lists_conflicting_files_and_leaves_no_merge_state()
    {
        Diverge(conflict: true);

        var preview = await _git.PreviewMergeAsync("feature", Ct);

        Assert.False(preview.IsClean);
        Assert.Equal(new[] { "f.txt" }, preview.ConflictedFiles);
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.Empty(_git.GetStatus());
        Assert.Equal("main\ntwo\nthree\n", Read("f.txt"));
    }

    [Fact]
    public async Task Preview_detects_fast_forward_and_up_to_date()
    {
        var baseSha = Commit("f.txt", "1\n", "base");
        var main = Main;
        _git.CreateBranch("ahead", baseSha, checkout: true);
        Commit("f.txt", "2\n", "ahead commit");
        _git.Checkout(main);

        Assert.True((await _git.PreviewMergeAsync("ahead", Ct)).IsFastForward);
        Assert.True((await _git.PreviewMergeAsync("HEAD", Ct)).IsUpToDate);
    }

    // ---- merge -----------------------------------------------------------------

    [Fact]
    public async Task Clean_merge_creates_a_merge_commit()
    {
        Diverge(conflict: false);

        var result = await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        Assert.True(result.Success);
        Assert.Equal(2, Parents(_git.GetCommits()[0]));
        Assert.True(File.Exists(Path_("g.txt")));
    }

    [Fact]
    public async Task Fast_forward_only_merge_fails_when_diverged()
    {
        Diverge(conflict: false);

        await Assert.ThrowsAsync<GitCommandException>(() => _git.MergeAsync("feature", MergeMode.FastForwardOnly, null, Ct));
    }

    [Fact]
    public async Task Squash_merge_stages_changes_without_committing()
    {
        Diverge(conflict: false);
        var before = _git.GetCommits().Count;

        var result = await _git.MergeAsync("feature", MergeMode.Squash, null, Ct);

        Assert.True(result.Success);
        Assert.Equal(before, _git.GetCommits().Count);
        Assert.Contains(_git.GetStatus(), s => s is { Path: "g.txt", IsStaged: true });
    }

    [Fact]
    public async Task Conflicting_merge_pauses_and_can_be_aborted()
    {
        Diverge(conflict: true);

        var result = await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        Assert.False(result.Success);
        Assert.True(result.Paused);
        Assert.Equal(RepoOperation.Merge, _git.GetOperationState().Kind);
        Assert.Contains(_git.GetStatus(), s => s is { Path: "f.txt", Status: "Conflicted" });

        await _git.AbortOperationAsync(null, Ct);

        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.Empty(_git.GetStatus());
    }

    [Fact]
    public async Task Conflicting_merge_completes_after_resolution_and_continue()
    {
        Diverge(conflict: true);
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        File.WriteAllText(Path_("f.txt"), "resolved\ntwo\nthree\n");
        _git.Stage("f.txt");
        var result = await _git.ContinueOperationAsync(null, Ct);

        Assert.True(result.Success);
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.Equal(2, Parents(_git.GetCommits()[0]));
    }

    [Fact]
    public async Task Merge_cannot_be_skipped()
    {
        Diverge(conflict: true);
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _git.SkipOperationAsync(null, Ct));
    }

    // ---- rebase ----------------------------------------------------------------

    [Fact]
    public async Task Rebase_replays_commits_linearly()
    {
        var (_, mainTip, _) = Diverge(conflict: false);
        _git.Checkout("feature");

        var result = await _git.RebaseAsync(Main, null, Ct);

        Assert.True(result.Success);
        var commits = _git.GetCommits();
        Assert.All(commits, c => Assert.True(Parents(c) <= 1));
        Assert.Equal(mainTip, commits[1].Sha);
    }

    [Fact]
    public async Task Conflicting_rebase_reports_progress_and_can_be_aborted()
    {
        Diverge(conflict: true);
        _git.Checkout("feature");

        var result = await _git.RebaseAsync(Main, null, Ct);

        Assert.True(result.Paused);
        var state = _git.GetOperationState();
        Assert.Equal(RepoOperation.Rebase, state.Kind);
        Assert.Equal(1, state.Step);
        Assert.Equal(1, state.Total);
        Assert.Equal("Rebase in progress (1/1)", state.Describe());

        await _git.AbortOperationAsync(null, Ct);
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.Equal("feature", _git.GetRefs().First(r => r.IsHead).Name);
    }

    [Fact]
    public async Task Conflicting_rebase_commit_can_be_skipped()
    {
        Diverge(conflict: true);
        _git.Checkout("feature");
        await _git.RebaseAsync(Main, null, Ct);

        var result = await _git.SkipOperationAsync(null, Ct);

        Assert.True(result.Success);
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.DoesNotContain(_git.GetCommits(), c => c.Summary == "feature change");
    }

    // ---- cherry-pick -----------------------------------------------------------

    [Fact]
    public async Task Cherry_pick_applies_a_commit_onto_the_current_branch()
    {
        var (_, _, featureTip) = Diverge(conflict: false);

        var result = await _git.CherryPickAsync(new[] { featureTip }, null, Ct);

        Assert.True(result.Success);
        Assert.Equal("feature change", _git.GetCommits()[0].Summary);
        Assert.True(File.Exists(Path_("g.txt")));
    }

    [Fact]
    public async Task Conflicting_cherry_pick_pauses_and_can_be_aborted()
    {
        var (_, _, featureTip) = Diverge(conflict: true);

        var result = await _git.CherryPickAsync(new[] { featureTip }, null, Ct);

        Assert.True(result.Paused);
        Assert.Equal(RepoOperation.CherryPick, _git.GetOperationState().Kind);

        await _git.AbortOperationAsync(null, Ct);
        Assert.Equal(RepoOperation.None, _git.GetOperationState().Kind);
        Assert.Equal("main\ntwo\nthree\n", Read("f.txt"));
    }

    [Fact]
    public async Task Cherry_pick_of_a_merge_commit_uses_the_first_parent()
    {
        Diverge(conflict: false);
        await _git.MergeAsync("feature", MergeMode.NoFastForward, null, Ct);
        var merge = _git.GetCommits()[0].Sha;
        _git.CreateBranch("target", _git.GetCommits().Last().Sha, checkout: true);

        var result = await _git.CherryPickAsync(new[] { merge }, null, Ct);

        Assert.True(result.Success);
    }

    // ---- revert ----------------------------------------------------------------

    [Fact]
    public async Task Revert_undoes_a_commit_with_a_new_commit()
    {
        Commit("f.txt", "one\n", "base");
        var change = Commit("f.txt", "two\n", "change");

        var result = await _git.RevertAsync(change, null, Ct);

        Assert.True(result.Success);
        Assert.Equal("one\n", Read("f.txt"));
        Assert.StartsWith("Revert", _git.GetCommits()[0].Summary);
    }

    [Fact]
    public async Task Conflicting_revert_pauses()
    {
        var first = Commit("f.txt", "one\n", "base");
        var change = Commit("f.txt", "two\n", "change");
        Commit("f.txt", "three\n", "later");

        var result = await _git.RevertAsync(change, null, Ct);

        Assert.True(result.Paused);
        Assert.Equal(RepoOperation.Revert, _git.GetOperationState().Kind);
        await _git.AbortOperationAsync(null, Ct);
        Assert.Equal("three\n", Read("f.txt"));
        Assert.NotNull(first);
    }

    // ---- reset -----------------------------------------------------------------

    private string ThreeCommits(out string first)
    {
        first = Commit("f.txt", "1\n", "one");
        Commit("f.txt", "2\n", "two");
        return Commit("f.txt", "3\n", "three");
    }

    [Fact]
    public async Task Soft_reset_moves_head_and_keeps_changes_staged()
    {
        ThreeCommits(out var first);

        await _git.ResetAsync(first, ResetMode.Soft, null, Ct);

        Assert.Equal(first, _git.GetCommits()[0].Sha);
        Assert.Contains(_git.GetStatus(), s => s is { Path: "f.txt", IsStaged: true });
        Assert.Equal("3\n", Read("f.txt"));
    }

    [Fact]
    public async Task Mixed_reset_keeps_changes_unstaged()
    {
        ThreeCommits(out var first);

        await _git.ResetAsync(first, ResetMode.Mixed, null, Ct);

        Assert.Equal(first, _git.GetCommits()[0].Sha);
        Assert.Contains(_git.GetStatus(), s => s is { Path: "f.txt", IsStaged: false });
        Assert.DoesNotContain(_git.GetStatus(), s => s.IsStaged);
        Assert.Equal("3\n", Read("f.txt"));
    }

    [Fact]
    public async Task Hard_reset_discards_changes()
    {
        ThreeCommits(out var first);

        await _git.ResetAsync(first, ResetMode.Hard, null, Ct);

        Assert.Equal(first, _git.GetCommits()[0].Sha);
        Assert.Empty(_git.GetStatus());
        Assert.Equal("1\n", Read("f.txt"));
    }

    [Fact]
    public async Task Option_like_revisions_are_rejected()
    {
        Commit("f.txt", "1\n", "one");

        await Assert.ThrowsAsync<ArgumentException>(() => _git.ResetAsync("--hard", ResetMode.Hard, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _git.MergeAsync("-x", MergeMode.Default, null, Ct));
    }
}
