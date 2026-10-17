using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.Core.Tests;

public sealed class SearchAndSettingsTests
{
    private static CommitInfo C(string sha, string msg, string author = "Ann", string email = "ann@x.io") =>
        new(sha, msg, msg, author, email, DateTimeOffset.UnixEpoch, Array.Empty<string>());

    [Fact]
    public void Empty_query_means_no_filter()
    {
        var result = GraphSearch.Match(new[] { C("abc", "m") }, new Dictionary<string, List<RefInfo>>(), "  ");
        Assert.Null(result);
    }

    [Fact]
    public void Matches_message_author_sha_and_branch()
    {
        var commits = new[] { C("abc123", "Fix login"), C("def456", "Other", "Bob", "bob@x.io"), C("999000", "Third") };
        var refs = new Dictionary<string, List<RefInfo>>
        {
            ["999000"] = new() { new RefInfo("release/1.0", RefKind.LocalBranch, "999000", false) }
        };

        Assert.Equal(new[] { "abc123" }, GraphSearch.Match(commits, refs, "LOGIN")!);
        Assert.Equal(new[] { "def456" }, GraphSearch.Match(commits, refs, "bob")!);
        Assert.Equal(new[] { "def456" }, GraphSearch.Match(commits, refs, "def4")!);
        Assert.Equal(new[] { "999000" }, GraphSearch.Match(commits, refs, "release")!);
    }

    [Fact]
    public void Recent_repos_are_mru_unique_and_capped()
    {
        var s = new RepoSettings();
        s.AddRecent(@"C:\a");
        s.AddRecent(@"C:\b");
        s.AddRecent(@"c:\A\");
        Assert.Equal(new[] { @"c:\A\", @"C:\b" }, s.RecentRepos);

        for (var i = 0; i < 30; i++) s.AddRecent($@"C:\r{i}");
        Assert.Equal(RepoSettings.MaxRecent, s.RecentRepos.Count);
    }

    [Fact]
    public void Store_round_trips_and_tolerates_corrupt_file()
    {
        var file = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"), "s.json");
        var store = new RepoSettingsStore(file);

        var s = new RepoSettings { ActiveTab = @"C:\x" };
        s.OpenTabs.Add(@"C:\x");
        store.Save(s);
        Assert.Equal(@"C:\x", store.Load().ActiveTab);

        File.WriteAllText(file, "{ not json");
        Assert.Empty(store.Load().OpenTabs);
    }
}
