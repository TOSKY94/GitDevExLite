using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class ConflictParserTests
{
    private const string One =
        "top\n<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs 1\n>>>>>>> feature\nmiddle\n" +
        "<<<<<<< HEAD\nA\n=======\nB\n>>>>>>> feature\nbottom\n";

    [Fact]
    public void Blocks_are_found_with_both_sides()
    {
        var blocks = ConflictParser.Parse(One).Where(s => s.IsConflict).ToList();

        Assert.Equal(2, blocks.Count);
        Assert.Equal("ours 1\nours 2\n", blocks[0].Ours);
        Assert.Equal("theirs 1\n", blocks[0].Theirs);
        Assert.Equal(1, blocks[0].StartLine);
        Assert.Equal(6, blocks[0].LineCount);
    }

    [Fact]
    public void Diff3_base_section_is_captured()
    {
        const string text = "<<<<<<< HEAD\nours\n||||||| base\noriginal\n=======\ntheirs\n>>>>>>> x\n";

        var block = Assert.Single(ConflictParser.Parse(text), s => s.IsConflict);

        Assert.Equal("original\n", block.Base);
        Assert.Equal("ours\n", block.Ours);
        Assert.Equal("theirs\n", block.Theirs);
    }

    [Theory]
    [InlineData(ConflictChoice.Ours, "top\nours 1\nours 2\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\n>>>>>>> feature\nbottom\n")]
    [InlineData(ConflictChoice.Theirs, "top\ntheirs 1\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\n>>>>>>> feature\nbottom\n")]
    [InlineData(ConflictChoice.Both, "top\nours 1\nours 2\ntheirs 1\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\n>>>>>>> feature\nbottom\n")]
    public void First_conflict_can_be_resolved_either_way(ConflictChoice choice, string expected) =>
        Assert.Equal(expected, ConflictParser.Resolve(One, 0, choice));

    [Fact]
    public void Resolving_every_block_leaves_no_markers()
    {
        var text = ConflictParser.Resolve(One, 0, ConflictChoice.Ours);
        text = ConflictParser.Resolve(text, 0, ConflictChoice.Theirs);

        Assert.Equal(0, ConflictParser.Count(text));
        Assert.Equal("top\nours 1\nours 2\nmiddle\nB\nbottom\n", text);
    }

    [Fact]
    public void Crlf_line_endings_are_preserved()
    {
        var text = "a\r\n<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> t\r\nz\r\n";

        Assert.Equal("a\r\ny\r\nz\r\n", ConflictParser.Resolve(text, 0, ConflictChoice.Theirs));
    }

    [Fact]
    public void An_unterminated_block_is_left_alone()
    {
        const string text = "a\n<<<<<<< HEAD\nx\n=======\ny\n";

        Assert.Equal(0, ConflictParser.Count(text));
        Assert.Equal(text, string.Concat(ConflictParser.Parse(text).Select(s => s.Text)));
    }

    [Fact]
    public void Text_without_markers_is_one_plain_segment()
    {
        var segments = ConflictParser.Parse("just\ntext\n");

        Assert.False(Assert.Single(segments).IsConflict);
    }
}

public sealed class RebasePlannerTests
{
    private static RebaseEntry E(RebaseAction a, string sha, string msg = "") =>
        new(a, sha, string.IsNullOrEmpty(msg) ? $"msg {sha}" : msg, (string.IsNullOrEmpty(msg) ? $"msg {sha}" : msg) + "\n");

    [Fact]
    public void Plain_picks_and_drops_become_todo_lines()
    {
        var plan = RebasePlanner.Build(new[] { E(RebaseAction.Pick, "a1"), E(RebaseAction.Drop, "b2"), E(RebaseAction.Edit, "c3") }, null);

        Assert.Equal("pick a1 msg a1\ndrop b2 msg b2\nedit c3 msg c3\n".ReplaceLineEndings(), plan.Todo.ReplaceLineEndings());
        Assert.Empty(plan.Messages);
    }

    [Fact]
    public void Squash_becomes_fixup_plus_an_amend_with_the_combined_message()
    {
        var entries = new[] { E(RebaseAction.Pick, "a1"), E(RebaseAction.Squash, "b2"), E(RebaseAction.Squash, "c3") };

        var plan = RebasePlanner.Build(entries, null);

        var lines = plan.Todo.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[]
        {
            "pick a1 msg a1", "fixup b2 msg b2", "fixup c3 msg c3",
            "exec git commit --amend --allow-empty -F {{MSG:0}}"
        }, lines);
        Assert.Equal("msg a1\n\nmsg b2\n\nmsg c3\n", plan.Messages[0]);
    }

    [Fact]
    public void Fixup_alone_needs_no_message_and_keeps_the_head_message()
    {
        var entries = new[] { E(RebaseAction.Pick, "a1"), E(RebaseAction.Fixup, "b2") };

        var plan = RebasePlanner.Build(entries, null);

        Assert.DoesNotContain("exec", plan.Todo);
        Assert.False(RebasePlanner.NeedsMessage(entries, 0));
    }

    [Fact]
    public void Reword_uses_the_supplied_message()
    {
        var entries = new[] { E(RebaseAction.Reword, "a1"), E(RebaseAction.Pick, "b2") };

        var plan = RebasePlanner.Build(entries, new Dictionary<string, string> { ["a1"] = "  better words " });

        Assert.Equal("better words\n", plan.Messages[0]);
        Assert.Contains("pick a1", plan.Todo);
    }

    [Fact]
    public void A_dropped_commit_between_group_members_does_not_split_the_group()
    {
        var entries = new[] { E(RebaseAction.Pick, "a1"), E(RebaseAction.Drop, "x"), E(RebaseAction.Squash, "b2") };

        Assert.Equal(new[] { 2 }, RebasePlanner.Members(entries, 0));
        Assert.True(RebasePlanner.NeedsMessage(entries, 0));
    }

    [Fact]
    public void Each_group_gets_its_own_message()
    {
        var entries = new[]
        {
            E(RebaseAction.Reword, "a1"), E(RebaseAction.Pick, "b2"), E(RebaseAction.Squash, "c3")
        };

        var plan = RebasePlanner.Build(entries, null);

        Assert.Equal(2, plan.Messages.Count);
        Assert.Contains("{{MSG:0}}", plan.Todo);
        Assert.Contains("{{MSG:1}}", plan.Todo);
    }

    [Fact]
    public void Validation_rejects_empty_all_dropped_and_leading_squash()
    {
        Assert.NotNull(RebasePlanner.Validate(Array.Empty<RebaseEntry>()));
        Assert.NotNull(RebasePlanner.Validate(new[] { E(RebaseAction.Drop, "a") }));
        Assert.NotNull(RebasePlanner.Validate(new[] { E(RebaseAction.Squash, "a"), E(RebaseAction.Pick, "b") }));
        Assert.Null(RebasePlanner.Validate(new[] { E(RebaseAction.Drop, "a"), E(RebaseAction.Pick, "b"), E(RebaseAction.Squash, "c") }));
        Assert.Throws<ArgumentException>(() => RebasePlanner.Build(new[] { E(RebaseAction.Fixup, "a") }, null));
    }
}

public sealed class ConflictAndInteractiveRebaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private string? _main;

    public ConflictAndInteractiveRebaseTests()
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
    private string P(string file) => Path.Combine(_dir, file);
    private string Read(string file) => File.ReadAllText(P(file)).Replace("\r\n", "\n");

    private string Commit(string file, string content, string message)
    {
        File.WriteAllText(P(file), content);
        _git.Stage(file);
        _git.Commit(message);
        return _git.GetCommits()[0].Sha;
    }

    private void MakeMergeConflict()
    {
        var baseSha = Commit("f.txt", "one\ntwo\nthree\n", "base");
        var main = Main;
        _git.CreateBranch("feature", baseSha, checkout: true);
        Commit("f.txt", "feature\ntwo\nthree\n", "feature change");
        _git.Checkout(main);
        Commit("f.txt", "main\ntwo\nthree\n", "main change");
    }

    private async Task<(string Base, IReadOnlyList<RebaseEntry> Entries)> ThreeCommits()
    {
        var baseSha = Commit("base.txt", "base\n", "base");
        Commit("a.txt", "a\n", "one");
        Commit("b.txt", "b\n", "two");
        Commit("c.txt", "c\n", "three");
        await Task.CompletedTask;
        return (baseSha, _git.GetRebaseEntries(baseSha));
    }

    private static RebaseRequest Request(IEnumerable<RebaseEntry> entries, Dictionary<string, string>? messages = null) =>
        new(entries.ToList(), messages ?? new Dictionary<string, string>());

    private IReadOnlyList<string> Summaries() => _git.GetCommits().Select(c => c.Summary).ToList();

    // ---- conflicts -------------------------------------------------------------

    [Fact]
    public async Task Conflict_file_exposes_base_ours_theirs_and_the_marked_working_copy()
    {
        MakeMergeConflict();
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        var file = await _git.GetConflictFileAsync("f.txt", Ct);

        Assert.False(file.IsBinary);
        Assert.StartsWith("one", file.Base!.Replace("\r\n", "\n"));
        Assert.StartsWith("main", file.Ours!.Replace("\r\n", "\n"));
        Assert.StartsWith("feature", file.Theirs!.Replace("\r\n", "\n"));
        Assert.Equal(1, ConflictParser.Count(file.Working));
    }

    [Fact]
    public async Task Saving_a_resolution_stages_it_and_lets_the_merge_continue()
    {
        MakeMergeConflict();
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);
        var file = await _git.GetConflictFileAsync("f.txt", Ct);

        await _git.ResolveFileAsync("f.txt", ConflictParser.Resolve(file.Working, 0, ConflictChoice.Both), Ct);

        Assert.DoesNotContain(_git.GetStatus(), s => s.Status == "Conflicted");
        Assert.Contains("main", Read("f.txt"));
        Assert.Contains("feature", Read("f.txt"));
        Assert.True((await _git.ContinueOperationAsync(null, Ct)).Success);
    }

    [Theory]
    [InlineData(ConflictSide.Ours, "main")]
    [InlineData(ConflictSide.Theirs, "feature")]
    public async Task Taking_a_whole_side_resolves_the_file(ConflictSide side, string expected)
    {
        MakeMergeConflict();
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        await _git.ResolveWithSideAsync("f.txt", side, Ct);

        Assert.StartsWith(expected, Read("f.txt"));
        Assert.DoesNotContain(_git.GetStatus(), s => s.Status == "Conflicted");
    }

    [Fact]
    public async Task A_delete_conflict_has_a_missing_side_and_taking_it_removes_the_file()
    {
        var baseSha = Commit("f.txt", "one\n", "base");
        var main = Main;
        _git.CreateBranch("feature", baseSha, checkout: true);
        File.Delete(P("f.txt"));
        _git.Stage("f.txt");
        _git.Commit("delete f");
        _git.Checkout(main);
        Commit("f.txt", "changed\n", "modify f");
        await _git.MergeAsync("feature", MergeMode.Default, null, Ct);

        var file = await _git.GetConflictFileAsync("f.txt", Ct);
        Assert.Null(file.Theirs);
        Assert.NotNull(file.Ours);

        await _git.ResolveWithSideAsync("f.txt", ConflictSide.Theirs, Ct);

        Assert.False(File.Exists(P("f.txt")));
        Assert.DoesNotContain(_git.GetStatus(), s => s.Status == "Conflicted");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../b")]
    public async Task Paths_outside_the_repository_are_rejected(string path)
    {
        Commit("f.txt", "x\n", "base");

        await Assert.ThrowsAsync<ArgumentException>(() => _git.GetConflictFileAsync(path, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _git.ResolveFileAsync(path, "x", Ct));
    }

    // ---- interactive rebase ----------------------------------------------------

    [Fact]
    public async Task Entries_list_the_commits_after_the_base_oldest_first()
    {
        var (_, entries) = await ThreeCommits();

        Assert.Equal(new[] { "one", "two", "three" }, entries.Select(e => e.Summary));
        Assert.All(entries, e => Assert.Equal(RebaseAction.Pick, e.Action));
    }

    [Fact]
    public async Task Commits_can_be_reordered()
    {
        var (baseSha, e) = await ThreeCommits();

        var result = await _git.InteractiveRebaseAsync(baseSha, Request(new[] { e[2], e[0], e[1] }), null, Ct);

        Assert.True(result.Success);
        Assert.Equal(new[] { "two", "one", "three", "base" }, Summaries());
    }

    [Fact]
    public async Task Commits_can_be_squashed_with_a_custom_message()
    {
        var (baseSha, e) = await ThreeCommits();
        var entries = new[] { e[0], e[1] with { Action = RebaseAction.Squash }, e[2] with { Action = RebaseAction.Squash } };

        var result = await _git.InteractiveRebaseAsync(
            baseSha, Request(entries, new() { [e[0].Sha] = "combined work" }), null, Ct);

        Assert.True(result.Success);
        Assert.Equal(new[] { "combined work", "base" }, Summaries());
        Assert.True(File.Exists(P("a.txt")) && File.Exists(P("b.txt")) && File.Exists(P("c.txt")));
    }

    [Fact]
    public async Task Fixup_keeps_the_first_message()
    {
        var (baseSha, e) = await ThreeCommits();
        var entries = new[] { e[0], e[1] with { Action = RebaseAction.Fixup }, e[2] };

        await _git.InteractiveRebaseAsync(baseSha, Request(entries), null, Ct);

        Assert.Equal(new[] { "three", "one", "base" }, Summaries());
    }

    [Fact]
    public async Task A_commit_can_be_reworded()
    {
        var (baseSha, e) = await ThreeCommits();
        var entries = new[] { e[0] with { Action = RebaseAction.Reword }, e[1], e[2] };

        await _git.InteractiveRebaseAsync(baseSha, Request(entries, new() { [e[0].Sha] = "renamed one" }), null, Ct);

        Assert.Equal(new[] { "three", "two", "renamed one", "base" }, Summaries());
    }

    [Fact]
    public async Task A_commit_can_be_dropped()
    {
        var (baseSha, e) = await ThreeCommits();
        var entries = new[] { e[0], e[1] with { Action = RebaseAction.Drop }, e[2] };

        await _git.InteractiveRebaseAsync(baseSha, Request(entries), null, Ct);

        Assert.Equal(new[] { "three", "one", "base" }, Summaries());
        Assert.False(File.Exists(P("b.txt")));
    }

    [Fact]
    public async Task Edit_stops_the_rebase_until_it_is_continued()
    {
        var (baseSha, e) = await ThreeCommits();
        var entries = new[] { e[0], e[1] with { Action = RebaseAction.Edit }, e[2] };

        var result = await _git.InteractiveRebaseAsync(baseSha, Request(entries), null, Ct);

        Assert.True(result.Paused);
        Assert.Equal(RepoOperation.Rebase, _git.GetOperationState().Kind);

        var done = await _git.ContinueOperationAsync(null, Ct);

        Assert.True(done.Success);
        Assert.Equal(new[] { "three", "two", "one", "base" }, Summaries());
    }

    [Fact]
    public async Task Reordering_commits_that_touch_the_same_file_pauses_on_a_conflict()
    {
        var baseSha = Commit("f.txt", "start\n", "base");
        Commit("f.txt", "first\n", "first edit");
        Commit("f.txt", "second\n", "second edit");
        var entries = _git.GetRebaseEntries(baseSha);

        var result = await _git.InteractiveRebaseAsync(baseSha, Request(new[] { entries[1], entries[0] }), null, Ct);

        Assert.True(result.Paused);
        Assert.Contains(_git.GetStatus(), s => s.Status == "Conflicted");

        await _git.AbortOperationAsync(null, Ct);
        Assert.Equal(new[] { "second edit", "first edit", "base" }, Summaries());
    }

    [Fact]
    public async Task Scratch_files_are_removed_once_the_rebase_finishes()
    {
        var (baseSha, e) = await ThreeCommits();

        await _git.InteractiveRebaseAsync(baseSha, Request(e), null, Ct);

        Assert.False(Directory.Exists(Path.Combine(_git.GitDirectory!, "gdx-rebase")));
    }

    [Fact]
    public async Task A_merge_commit_in_range_is_refused()
    {
        var baseSha = Commit("f.txt", "1\n", "base");
        var main = Main;
        _git.CreateBranch("side", baseSha, checkout: true);
        Commit("s.txt", "s\n", "side");
        _git.Checkout(main);
        Commit("m.txt", "m\n", "main");
        await _git.MergeAsync("side", MergeMode.NoFastForward, null, Ct);

        Assert.Throws<InvalidOperationException>(() => _git.GetRebaseEntries(baseSha));
    }

    [Fact]
    public async Task A_base_that_is_not_an_ancestor_is_refused()
    {
        var baseSha = Commit("f.txt", "1\n", "base");
        var main = Main;
        _git.CreateBranch("other", baseSha, checkout: true);
        var elsewhere = Commit("o.txt", "o\n", "other");
        _git.Checkout(main);
        Commit("m.txt", "m\n", "main");

        Assert.Throws<InvalidOperationException>(() => _git.GetRebaseEntries(elsewhere));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_stale_list_is_refused()
    {
        var (baseSha, e) = await ThreeCommits();
        Commit("d.txt", "d\n", "four");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _git.InteractiveRebaseAsync(baseSha, Request(e), null, Ct));
    }
}
