using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class RemoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly string _bare;
    private readonly string _aDir;
    private readonly string _bDir;
    private readonly GitService _a = new();
    private GitService? _b;

    public RemoteTests()
    {
        _bare = Path.Combine(_root, "remote.git");
        _aDir = Path.Combine(_root, "a");
        _bDir = Path.Combine(_root, "b");
        Directory.CreateDirectory(_aDir);
        Repository.Init(_bare, isBare: true);
        Repository.Init(_aDir);
        using (var repo = new Repository(_aDir))
        {
            Configure(repo);
            repo.Network.Remotes.Add("origin", _bare);
        }
        _a.Open(_aDir);
    }

    public void Dispose()
    {
        _a.Dispose();
        _b?.Dispose();
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }

    private static void Configure(Repository repo)
    {
        repo.Config.Set("user.name", "Test");
        repo.Config.Set("user.email", "test@example.com");
    }

    private static void CommitFile(GitService git, string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        git.Stage(file);
        git.Commit(message);
    }

    private async Task<GitService> CloneB()
    {
        await GitClone.CloneAsync(_bare, _bDir, null, CancellationToken.None);
        using (var repo = new Repository(_bDir)) Configure(repo);
        _b = new GitService();
        _b.Open(_bDir);
        return _b;
    }

    private static RefInfo Head(GitService git) => git.GetRefs().First(r => r.IsHead);

    private async Task SeedAndPush()
    {
        CommitFile(_a, _aDir, "f.txt", "1\n", "first");
        await _a.PushAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task First_push_sets_upstream_and_reports_in_sync()
    {
        await SeedAndPush();

        var head = Head(_a);
        Assert.Equal($"origin/{head.Name}", head.Upstream);
        Assert.Equal(0, head.Ahead);
        Assert.Equal(0, head.Behind);
        Assert.Equal("", head.AheadBehindText);
    }

    [Fact]
    public async Task Fetch_reports_behind_and_pull_catches_up()
    {
        await SeedAndPush();
        var b = await CloneB();
        CommitFile(b, _bDir, "g.txt", "x\n", "from b");
        await b.PushAsync(null, CancellationToken.None);

        await _a.FetchAsync(prune: true, null, CancellationToken.None);
        Assert.Equal(1, Head(_a).Behind);
        Assert.Equal("\u21931", Head(_a).AheadBehindText);

        await _a.PullAsync(PullMode.FastForwardOnly, null, CancellationToken.None);
        Assert.Equal(0, Head(_a).Behind);
        Assert.True(File.Exists(Path.Combine(_aDir, "g.txt")));
    }

    [Fact]
    public async Task Ahead_count_is_reported()
    {
        await SeedAndPush();
        CommitFile(_a, _aDir, "f.txt", "2\n", "second");
        Assert.Equal(1, Head(_a).Ahead);
        Assert.Equal("\u21911", Head(_a).AheadBehindText);
    }

    [Fact]
    public async Task Pull_rebase_keeps_history_linear()
    {
        await SeedAndPush();
        var b = await CloneB();
        CommitFile(b, _bDir, "b.txt", "b\n", "from b");
        await b.PushAsync(null, CancellationToken.None);
        CommitFile(_a, _aDir, "a.txt", "a\n", "from a");

        await _a.PullAsync(PullMode.Rebase, null, CancellationToken.None);

        var commits = _a.GetCommits();
        Assert.All(commits, c => Assert.True(c.ParentShas.Count <= 1));
        Assert.Equal(1, Head(_a).Ahead);
        Assert.Equal(0, Head(_a).Behind);
    }

    [Fact]
    public async Task Pull_merge_creates_a_merge_commit_when_diverged()
    {
        await SeedAndPush();
        var b = await CloneB();
        CommitFile(b, _bDir, "b.txt", "b\n", "from b");
        await b.PushAsync(null, CancellationToken.None);
        CommitFile(_a, _aDir, "a.txt", "a\n", "from a");

        await _a.PullAsync(PullMode.Merge, null, CancellationToken.None);

        Assert.Contains(_a.GetCommits(), c => c.ParentShas.Count == 2);
    }

    [Fact]
    public async Task Pull_ff_only_fails_when_diverged()
    {
        await SeedAndPush();
        var b = await CloneB();
        CommitFile(b, _bDir, "b.txt", "b\n", "from b");
        await b.PushAsync(null, CancellationToken.None);
        CommitFile(_a, _aDir, "a.txt", "a\n", "from a");

        await Assert.ThrowsAsync<GitCommandException>(
            () => _a.PullAsync(PullMode.FastForwardOnly, null, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_prune_removes_deleted_remote_branches()
    {
        await SeedAndPush();
        var sha = _a.GetCommits()[0].Sha;
        _a.CreateBranch("topic", sha, checkout: false);
        _a.Checkout("topic");
        await _a.PushAsync(null, CancellationToken.None);
        Assert.Contains(_a.GetRefs(), r => r.Name == "origin/topic");

        using (var bare = new Repository(_bare)) bare.Branches.Remove("topic");

        await _a.FetchAsync(prune: false, null, CancellationToken.None);
        Assert.Contains(_a.GetRefs(), r => r.Name == "origin/topic");

        await _a.FetchAsync(prune: true, null, CancellationToken.None);
        Assert.DoesNotContain(_a.GetRefs(), r => r.Name == "origin/topic");
    }

    [Fact]
    public async Task Set_upstream_links_local_branch_to_remote_branch()
    {
        await SeedAndPush();
        var sha = _a.GetCommits()[0].Sha;
        var main = Head(_a).Name;
        _a.CreateBranch("other", sha, checkout: false);

        _a.SetUpstream("other", $"origin/{main}");

        Assert.Equal($"origin/{main}", _a.GetRefs().First(r => r.Name == "other").Upstream);
    }

    [Fact]
    public async Task Tags_can_be_created_pushed_and_deleted()
    {
        await SeedAndPush();
        var sha = _a.GetCommits()[0].Sha;

        _a.CreateTag("v1", sha, null);
        _a.CreateTag("v2", sha, "release two");
        Assert.Contains(_a.GetRefs(), r => r is { Kind: RefKind.Tag, Name: "v2" });

        await _a.PushTagAsync("v1", null, CancellationToken.None);
        using (var bare = new Repository(_bare)) Assert.NotNull(bare.Tags["v1"]);

        await _a.DeleteRemoteTagAsync("v1", null, CancellationToken.None);
        using (var bare = new Repository(_bare)) Assert.Null(bare.Tags["v1"]);

        _a.DeleteTag("v2");
        Assert.DoesNotContain(_a.GetRefs(), r => r is { Kind: RefKind.Tag, Name: "v2" });
    }

    [Fact]
    public async Task Cancelled_token_aborts_the_operation()
    {
        await SeedAndPush();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _a.FetchAsync(prune: false, null, cts.Token));
    }

    [Fact]
    public async Task Fetch_without_remotes_is_rejected()
    {
        var dir = Path.Combine(_root, "lonely");
        Directory.CreateDirectory(dir);
        Repository.Init(dir);
        using var git = new GitService();
        git.Open(dir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => git.FetchAsync(false, null, CancellationToken.None));
    }

    [Fact]
    public async Task Clone_rejects_option_like_urls()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => GitClone.CloneAsync("--upload-pack=calc", Path.Combine(_root, "x"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_remote_reports_an_error_not_a_hang()
    {
        using var repo = new Repository(_aDir);
        repo.Network.Remotes.Add("dead", Path.Combine(_root, "does-not-exist.git"));
        CommitFile(_a, _aDir, "f.txt", "1\n", "first");

        await Assert.ThrowsAsync<GitCommandException>(
            () => _a.FetchAsync(false, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("Receiving objects:  45% (45/100), 1.2 MiB | 2.0 MiB/s", "Receiving objects", 45)]
    [InlineData("remote: Counting objects: 100% (12/12), done.", "Counting objects", 100)]
    [InlineData("Resolving deltas:   3% (1/30)", "Resolving deltas", 3)]
    public void Progress_lines_are_parsed(string line, string phase, int percent)
    {
        Assert.True(GitProgressParser.TryParse(line, out var p));
        Assert.Equal(phase, p.Phase);
        Assert.Equal(percent, p.Percent);
    }

    [Theory]
    [InlineData("fatal: unable to access: something")]
    [InlineData("From github.com:x/y")]
    public void Non_progress_lines_are_ignored(string line) =>
        Assert.False(GitProgressParser.TryParse(line, out _));

    [Theory]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://x/'", true)]
    [InlineData("git@github.com: Permission denied (publickey).", true)]
    [InlineData("fatal: could not read Username for 'https://x': terminal prompts disabled", true)]
    [InlineData("fatal: refusing to merge unrelated histories", false)]
    public void Auth_failures_are_detected(string stderr, bool expected) =>
        Assert.Equal(expected, GitAuthFailure.IsAuthFailure(stderr));

    [Theory]
    [InlineData("https://github.com/a/b.git", true, "github.com")]
    [InlineData("https://dev.azure.com/org/proj/_git/repo", true, "dev.azure.com")]
    [InlineData("https://host:8443/x", true, "host:8443")]
    [InlineData("git@github.com:a/b.git", false, "")]
    public void Http_urls_are_parsed_for_credentials(string url, bool ok, string host)
    {
        Assert.Equal(ok, GitCredentials.TryParseHttp(url, out _, out var h));
        Assert.Equal(host, h);
    }

    [Theory]
    [InlineData("git@github.com:a/b.git", true)]
    [InlineData("ssh://git@host/a/b", true)]
    [InlineData("https://github.com/a/b", false)]
    public void Ssh_urls_are_recognised(string url, bool expected) =>
        Assert.Equal(expected, GitCredentials.IsSshUrl(url));

    [Theory]
    [InlineData("https://github.com/a/repo.git", "repo")]
    [InlineData("git@github.com:a/repo", "repo")]
    [InlineData("https://dev.azure.com/o/p/_git/my-repo/", "my-repo")]
    public void Folder_name_is_suggested_from_url(string url, string expected) =>
        Assert.Equal(expected, GitClone.SuggestFolderName(url));

    [Fact]
    public async Task Token_with_line_break_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GitCredentials.StoreTokenAsync("https://github.com/a/b", "user", "tok\nprotocol=evil", CancellationToken.None));
    }
}
