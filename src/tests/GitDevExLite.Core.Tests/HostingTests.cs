using System.Net;
using System.Text;
using GitDevExLite.Core.Hosting;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class HostingTests
{
    [Theory]
    [InlineData("https://github.com/octo/repo.git", HostKind.GitHub, "octo", "", "repo")]
    [InlineData("git@github.com:octo/repo.git", HostKind.GitHub, "octo", "", "repo")]
    [InlineData("https://dev.azure.com/org/proj/_git/my repo", HostKind.AzureDevOps, "org", "proj", "my repo")]
    [InlineData("https://org@dev.azure.com/org/proj/_git/app", HostKind.AzureDevOps, "org", "proj", "app")]
    [InlineData("git@ssh.dev.azure.com:v3/org/proj/app", HostKind.AzureDevOps, "org", "proj", "app")]
    [InlineData("https://org.visualstudio.com/proj/_git/app", HostKind.AzureDevOps, "org", "proj", "app")]
    public void RemoteParser_recognises_hosts(string url, HostKind kind, string owner, string project, string name)
    {
        var r = RemoteParser.Parse(url);
        Assert.NotNull(r);
        Assert.Equal(kind, r!.Kind);
        Assert.Equal(owner, r.Owner);
        Assert.Equal(project, r.Project);
        Assert.Equal(name, r.Name);
    }

    [Fact]
    public void RemoteParser_flags_other_hosts()
    {
        Assert.Equal(HostKind.GitLab, RemoteParser.Parse("https://gitlab.com/a/b.git")?.Kind);
        Assert.Equal(HostKind.Bitbucket, RemoteParser.Parse("https://bitbucket.org/a/b.git")?.Kind);
        Assert.Null(RemoteParser.Parse(null));
    }

    [Fact]
    public void SecretScanner_finds_known_secrets_and_respects_allow_marker()
    {
        var text = string.Join("\n",
            "key = AKIAABCDEFGHIJKLMNOP",
            "-----BEGIN RSA PRIVATE KEY-----",
            "password = \"hunter2hunter2\"",
            "password = \"changeme-please\"",
            "token = \"" + "ghp_" + new string('a', 36) + "\"",
            "api_key = \"abcdefgh12345678\" // gitdevex:allow",
            "var x = 1;");
        var found = SecretScanner.ScanText("a.txt", text);
        Assert.Equal(new[] { 1, 2, 3, 5 }, found.Select(f => f.Line).ToArray());
        Assert.All(found, f => Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", f.Preview));
    }

    [Fact]
    public void SecretScanner_scans_only_added_lines_of_a_diff()
    {
        var patch = "@@ -1,2 +1,2 @@\n-password = \"oldsecret99\"\n+password = \"newsecret99\"\n context\n";
        var found = SecretScanner.ScanDiff("a.txt", patch);
        var f = Assert.Single(found);
        Assert.Equal(1, f.Line);
    }

    [Fact]
    public void UnifiedDiff_produces_parseable_hunks()
    {
        var patch = UnifiedDiff.Create("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n");
        Assert.NotNull(patch);
        var diff = GitDevExLite.Core.Diff.PatchParser.Parse("f.txt", patch!, false, false);
        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Removed && l.Text == "b");
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text == "B");
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text == "e");
        Assert.Equal("", UnifiedDiff.Create("same\n", "same\n"));
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            var (status, json) = respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task GitHub_lists_prs_and_maps_fields()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, """
            [{"number":7,"title":"Add x","body":"desc","user":{"login":"bob"},"state":"open","draft":false,
              "html_url":"https://github.com/o/r/pull/7","created_at":"2024-01-02T03:04:05Z",
              "head":{"ref":"feat","sha":"abc"},"base":{"ref":"main"},"requested_reviewers":[{"login":"me"}]}]
            """));
        var repo = RemoteParser.Parse("https://github.com/o/r.git")!;
        var provider = new GitHubProvider(repo, new HttpClient(handler), "tok");

        var list = await provider.ListAsync(PrFilter.All, CancellationToken.None);

        var pr = Assert.Single(list);
        Assert.Equal(7, pr.Number);
        Assert.Equal("bob", pr.Author);
        Assert.Equal("feat", pr.SourceBranch);
        Assert.Equal("pr-7", pr.LocalBranch);
        Assert.Equal("+refs/pull/7/head:refs/heads/pr-7", pr.CheckoutRefspec);
        Assert.Contains("api.github.com/repos/o/r/pulls", handler.Requests[0].Url);
    }

    [Fact]
    public async Task GitHub_maps_auth_failure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"));
        var provider = new GitHubProvider(RemoteParser.Parse("https://github.com/o/r")!, new HttpClient(handler), "bad");
        await Assert.ThrowsAsync<HostingAuthException>(() => provider.ListAsync(PrFilter.All, CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_merge_sends_method()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{\"merged\":true}"));
        var provider = new GitHubProvider(RemoteParser.Parse("https://github.com/o/r")!, new HttpClient(handler), "t");
        await provider.MergeAsync(3, PrMergeMethod.Squash, null, CancellationToken.None);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.EndsWith("/pulls/3/merge", req.Url);
        Assert.Contains("\"squash\"", req.Body);
    }

    [Fact]
    public async Task AzureDevOps_lists_prs_and_sends_vote()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("connectionData")
            ? (HttpStatusCode.OK, "{\"authenticatedUser\":{\"id\":\"u1\"}}")
            : r.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """
                    {"value":[{"pullRequestId":12,"title":"T","description":"D","createdBy":{"displayName":"Ann"},
                    "sourceRefName":"refs/heads/feat","targetRefName":"refs/heads/main","status":"active",
                    "creationDate":"2024-01-01T00:00:00Z","lastMergeSourceCommit":{"commitId":"c1"},"reviewers":[]}]}
                    """)
                : (HttpStatusCode.OK, "{}"));
        var provider = new AzureDevOpsProvider(RemoteParser.Parse("https://dev.azure.com/org/proj/_git/app")!, new HttpClient(handler), "pat");

        var list = await provider.ListAsync(PrFilter.Mine, CancellationToken.None);
        var pr = Assert.Single(list);
        Assert.Equal("feat", pr.SourceBranch);
        Assert.Equal("+refs/heads/feat:refs/heads/pr-12", pr.CheckoutRefspec);
        Assert.Contains(handler.Requests, r => r.Url.Contains("searchCriteria.creatorId=u1"));

        await provider.ReviewAsync(12, ReviewVerdict.Approve, null, CancellationToken.None);
        var vote = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, vote.Method);
        Assert.Contains("/reviewers/u1", vote.Url);
        Assert.Contains("10", vote.Body);
    }

    [Fact]
    public async Task Force_push_with_lease_updates_diverged_remote()
    {
        var root = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, "r.git");
        var dir = Path.Combine(root, "a");
        Directory.CreateDirectory(dir);
        Repository.Init(bare, isBare: true);
        Repository.Init(dir);
        try
        {
            using (var repo = new Repository(dir))
            {
                repo.Config.Set("user.name", "T");
                repo.Config.Set("user.email", "t@e.com");
                repo.Network.Remotes.Add("origin", bare);
            }
            using var git = new GitService();
            git.Open(dir);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "1\n");
            git.Stage("f.txt");
            git.Commit("one");
            File.WriteAllText(Path.Combine(dir, "f.txt"), "2\n");
            git.Stage("f.txt");
            git.Commit("two");
            await git.PushAsync(null, CancellationToken.None);

            await git.ResetAsync(git.GetCommits()[1].Sha, GitDevExLite.Core.Services.ResetMode.Hard, null, CancellationToken.None);
            File.WriteAllText(Path.Combine(dir, "f.txt"), "3\n");
            git.Stage("f.txt");
            git.Commit("three");

            await Assert.ThrowsAnyAsync<Exception>(() => git.PushAsync(null, CancellationToken.None));
            await git.PushAsync(null, CancellationToken.None, forceWithLease: true);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
