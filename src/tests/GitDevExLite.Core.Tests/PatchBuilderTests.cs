using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Tests;

public sealed class PatchBuilderTests
{
    private const string Sample =
        "diff --git a/f b/f\n--- a/f\n+++ b/f\n" +
        "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n" +
        "@@ -10,3 +10,4 @@\n x\n y\n+z\n w\n";

    private static FileDiff Diff() => PatchParser.Parse("f", Sample, false, false);

    [Fact]
    public void Parser_reads_hunks_and_line_numbers()
    {
        var d = Diff();
        Assert.Equal(2, d.Hunks.Count);
        var removed = d.Hunks[0].Lines.Single(l => l.Kind == DiffLineKind.Removed);
        Assert.Equal((2, (int?)null), (removed.OldNo!.Value, removed.NewNo));
        Assert.Equal(11, d.Hunks[1].Lines.First(l => l.Text == "y").NewNo);
    }

    [Fact]
    public void Whole_hunk_selection_emits_only_that_hunk_with_adjusted_header()
    {
        var d = Diff();
        var patch = PatchBuilder.Build(d, PatchBuilder.HunkChanges(d, 1), reverse: false)!;
        Assert.Contains("@@ -10,3 +10,4 @@", patch);
        Assert.DoesNotContain("-b", patch);
    }

    [Fact]
    public void Header_new_start_accounts_for_earlier_hunks()
    {
        var d = Diff();
        var all = new HashSet<LineRef>(PatchBuilder.HunkChanges(d, 0));
        all.UnionWith(PatchBuilder.HunkChanges(d, 1));
        var onlyAdd = new HashSet<LineRef> { new(0, 2), new(1, 2) };

        // Selecting only the '+' of hunk 0 leaves '-b' as context, so the first hunk grows by one new line.
        var patch = PatchBuilder.Build(d, onlyAdd, reverse: false)!;
        Assert.Contains("@@ -1,3 +1,4 @@", patch);
        Assert.Contains("@@ -10,3 +11,4 @@", patch);
        Assert.NotNull(PatchBuilder.Build(d, all, reverse: false));
    }

    [Fact]
    public void Unselected_removal_becomes_context_and_unselected_addition_is_dropped()
    {
        var d = Diff();
        var patch = PatchBuilder.Build(d, new HashSet<LineRef> { new(0, 1) }, reverse: false)!;
        Assert.Contains("\n-b\n", patch);
        Assert.DoesNotContain("+B", patch);
        Assert.Contains("@@ -1,3 +1,2 @@", patch);
    }

    [Fact]
    public void Reverse_mode_keeps_unselected_additions_as_context_and_drops_unselected_removals()
    {
        var d = Diff();
        var patch = PatchBuilder.Build(d, new HashSet<LineRef> { new(0, 1) }, reverse: true)!;
        Assert.Contains("\n-b\n", patch);
        Assert.Contains("\n B\n", patch);
        Assert.Contains("@@ -1,4 +1,3 @@", patch);
    }

    [Fact]
    public void Empty_selection_returns_null()
    {
        Assert.Null(PatchBuilder.Build(Diff(), new HashSet<LineRef>(), reverse: false));
    }

    [Fact]
    public void New_file_header_uses_dev_null()
    {
        var d = PatchParser.Parse("n", "diff --git a/n b/n\n--- /dev/null\n+++ b/n\n@@ -0,0 +1,2 @@\n+a\n+b\n", true, false);
        var patch = PatchBuilder.Build(d, PatchBuilder.HunkChanges(d, 0), false)!;
        Assert.Contains("--- /dev/null", patch);
    }

    [Fact]
    public void No_newline_marker_follows_only_emitted_lines()
    {
        var d = PatchParser.Parse("f",
            "@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n", false, false);
        var patch = PatchBuilder.Build(d, new HashSet<LineRef> { new(0, 1) }, false)!;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(patch, @"\\ No newline"));
    }
}
