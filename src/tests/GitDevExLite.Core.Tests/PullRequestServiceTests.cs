using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using Xunit;

namespace GitDevExLite.Core.Tests;

public class PullRequestServiceTests
{
    private static readonly HostedRepositoryId GitHubRepository = new(
        HostingProviderKind.GitHub,
        "github.com",
        "octo-org",
        "GitDevExLite",
        RemoteName: "origin");

    private static readonly HostedRepositoryId AzureDevOpsRepository = new(
        HostingProviderKind.AzureDevOps,
        "dev.azure.com",
        "octo-org",
        "GitDevExLite",
        Project: "ProjectOne",
        RemoteName: "origin");

    [Fact]
    public async Task ListAsync_RequiresAuthentication()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListAsync(GitHubRepository, PullRequestFilterKind.All, CancellationToken.None));

        Assert.Contains("Sign in", error.Message);
    }

    [Fact]
    public async Task ListAsync_ReturnsGitHubPullRequests_AfterSignIn()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(Session());
        var service = CreateService(CreateHandler(
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls?state=open&per_page=50", "[{\"number\":42,\"title\":\"Add GitHub provider\",\"body\":\"desc\",\"state\":\"open\",\"draft\":false,\"updated_at\":\"2026-10-17T10:00:00Z\",\"html_url\":\"https://github.com/octo-org/GitDevExLite/pull/42\",\"user\":{\"login\":\"octocat\"},\"head\":{\"ref\":\"feature/github-provider\",\"sha\":\"abc123\"},\"base\":{\"ref\":\"main\",\"sha\":\"def456\"},\"requested_reviewers\":[{\"login\":\"octocat\"}]}]")), store);

        var items = await service.ListAsync(GitHubRepository, PullRequestFilterKind.All, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(42, item.Number);
        Assert.True(item.IsMine);
        Assert.False(item.IsReviewRequested);
    }

    [Fact]
    public async Task GetDetailAsync_Maps_Commits_Files_Threads_And_Statuses()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(Session());
        var service = CreateService(CreateHandler(
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls/42", "{\"number\":42,\"title\":\"Add GitHub provider\",\"body\":\"desc\",\"state\":\"open\",\"draft\":false,\"updated_at\":\"2026-10-17T10:00:00Z\",\"html_url\":\"https://github.com/octo-org/GitDevExLite/pull/42\",\"user\":{\"login\":\"octocat\"},\"head\":{\"ref\":\"feature/github-provider\",\"sha\":\"abc123\"},\"base\":{\"ref\":\"main\",\"sha\":\"def456\"},\"requested_reviewers\":[]}"),
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls/42/commits?per_page=100", "[{\"sha\":\"abc123\",\"commit\":{\"message\":\"Add provider\\n\\nMore\",\"author\":{\"name\":\"Octo Cat\",\"date\":\"2026-10-17T09:00:00Z\"}},\"author\":{\"login\":\"octocat\"}}]"),
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls/42/files?per_page=100", "[{\"filename\":\"src/file.cs\",\"previous_filename\":null,\"status\":\"modified\"}]"),
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls/42/reviews?per_page=100", "[{\"id\":7,\"body\":\"Looks good\",\"user\":{\"login\":\"reviewer\"},\"created_at\":\"2026-10-17T09:30:00Z\",\"submitted_at\":\"2026-10-17T09:31:00Z\"}]"),
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/pulls/42/comments?per_page=100", "[{\"id\":8,\"body\":\"Inline\",\"path\":\"src/file.cs\",\"line\":12,\"original_line\":12,\"start_line\":null,\"side\":\"RIGHT\",\"in_reply_to_id\":null,\"user\":{\"login\":\"reviewer\"},\"created_at\":\"2026-10-17T09:32:00Z\"}]"),
            (HttpMethod.Get, "/repos/octo-org/GitDevExLite/commits/abc123/status", "{\"statuses\":[{\"context\":\"build\",\"state\":\"success\",\"description\":\"passed\",\"target_url\":\"https://ci.example/build\"}]}")), store);

        var detail = await service.GetDetailAsync(GitHubRepository, "42", CancellationToken.None);

        Assert.Equal("desc", detail.Description);
        Assert.Single(detail.Commits);
        Assert.Single(detail.Files);
        Assert.Equal(2, detail.Threads.Count);
        Assert.Single(detail.StatusChecks);
    }

    [Fact]
    public async Task CreateAndMerge_Send_GitHub_Requests()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(Session());
        string? createBody = null;
        string? mergeBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/repos/octo-org/GitDevExLite/pulls")
            {
                createBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{\"number\":43,\"title\":\"Create PR\",\"body\":\"desc\",\"state\":\"open\",\"draft\":true,\"updated_at\":\"2026-10-17T11:00:00Z\",\"html_url\":\"https://github.com/octo-org/GitDevExLite/pull/43\",\"user\":{\"login\":\"octocat\"},\"head\":{\"ref\":\"feature/x\",\"sha\":\"abc123\"},\"base\":{\"ref\":\"main\",\"sha\":\"def456\"},\"requested_reviewers\":[]}");
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/repos/octo-org/GitDevExLite/pulls/43/merge")
            {
                mergeBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(handler, store);

        var created = await service.CreateAsync(
            GitHubRepository,
            new CreatePullRequestRequest("Create PR", "desc", "feature/x", "main", true),
            CancellationToken.None);
        await service.MergeAsync(GitHubRepository, created.Id, new PullRequestMergeRequest(PullRequestMergeStrategy.Squash), CancellationToken.None);

        Assert.Equal(43, created.Number);
        Assert.NotNull(createBody);
        Assert.NotNull(mergeBody);
        Assert.Contains("\"draft\":true", createBody);
        Assert.Contains("\"merge_method\":\"squash\"", mergeBody);
    }

    [Fact]
    public async Task SignInAsync_Uses_GitHub_User_Profile()
    {
        var store = new InMemoryProviderSessionStore();
        var auth = new GitHubPullRequestAuthService(
            store,
            new HttpClient(CreateHandler(
                (HttpMethod.Get, "/user", "{\"login\":\"octocat\",\"name\":\"Octo Cat\",\"email\":\"octo@example.com\"}")))
            {
                BaseAddress = new Uri("https://api.github.com/")
            },
            (_, _, _) => Task.FromResult<string?>("test-token"));

        var session = await auth.SignInAsync(GitHubRepository, CancellationToken.None);

        Assert.Equal("octocat", session.Account.UserName);
        Assert.Equal("Octo Cat", session.Account.DisplayName);
    }

    [Fact]
    public async Task AzureDevOps_ListAsync_Returns_PullRequests_AfterSignIn()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(AzureSession());
        const string responseBody = "{\"value\":[{\"pullRequestId\":52,\"title\":\"Add Azure DevOps provider\",\"description\":\"desc\",\"status\":\"active\",\"isDraft\":false,\"creationDate\":\"2026-10-17T10:00:00Z\",\"url\":\"https://dev.azure.com/octo-org/ProjectOne/_git/GitDevExLite/pullrequest/52\",\"sourceRefName\":\"refs/heads/feature/ado\",\"targetRefName\":\"refs/heads/main\",\"createdBy\":{\"id\":\"aad-1\",\"displayName\":\"Octo Cat\",\"uniqueName\":\"octo@example.com\"},\"reviewers\":[{\"id\":\"aad-1\",\"vote\":0}]}]}";
        var service = CreateService(
            sessions: store,
            azureDevOpsHandler: CreateHandler(
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullrequests?searchCriteria.status=active&api-version=7.1", responseBody)));

        var items = await service.ListAsync(AzureDevOpsRepository, PullRequestFilterKind.All, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(52, item.Number);
        Assert.True(item.IsMine);
        Assert.True(item.IsReviewRequested);
    }

    [Fact]
    public async Task AzureDevOps_GetDetailAsync_Maps_Detail_Slices()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(AzureSession());
        var service = CreateService(
            sessions: store,
            azureDevOpsHandler: CreateHandler(
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52?api-version=7.1", "{\"pullRequestId\":52,\"title\":\"Add Azure DevOps provider\",\"description\":\"desc\",\"status\":\"active\",\"isDraft\":false,\"creationDate\":\"2026-10-17T10:00:00Z\",\"url\":\"https://dev.azure.com/octo-org/ProjectOne/_git/GitDevExLite/pullrequest/52\",\"sourceRefName\":\"refs/heads/feature/ado\",\"targetRefName\":\"refs/heads/main\",\"createdBy\":{\"id\":\"aad-1\",\"displayName\":\"Octo Cat\",\"uniqueName\":\"octo@example.com\"},\"reviewers\":[]}"),
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/commits?api-version=7.1", "{\"value\":[{\"commitId\":\"abc123\",\"comment\":\"Add ADO provider\\n\\nMore\",\"author\":{\"name\":\"Octo Cat\",\"date\":\"2026-10-17T09:00:00Z\"}}]}"),
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/threads?api-version=7.1", "{\"value\":[{\"id\":7,\"status\":\"active\",\"threadContext\":{\"filePath\":\"/src/file.cs\",\"rightFileStart\":{\"line\":12}},\"comments\":[{\"id\":8,\"content\":\"Inline\",\"publishedDate\":\"2026-10-17T09:32:00Z\",\"author\":{\"displayName\":\"Reviewer\",\"uniqueName\":\"reviewer@example.com\"}}]}]}"),
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/iterations?api-version=7.1", "{\"value\":[{\"id\":1}]}"),
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/iterations/1/changes?api-version=7.1", "{\"changeEntries\":[{\"changeType\":\"edit\",\"item\":{\"path\":\"/src/file.cs\"}}]}"),
                (HttpMethod.Get, "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/statuses?api-version=7.1", "{\"value\":[{\"state\":\"succeeded\",\"description\":\"passed\",\"targetUrl\":\"https://ci.example/build\",\"context\":{\"name\":\"build\"}}]}")));

        var detail = await service.GetDetailAsync(AzureDevOpsRepository, "52", CancellationToken.None);

        Assert.Equal("desc", detail.Description);
        Assert.Single(detail.Commits);
        Assert.Single(detail.Files);
        Assert.Single(detail.Threads);
        Assert.Single(detail.StatusChecks);
    }

    [Fact]
    public async Task AzureDevOps_CreateAndMerge_Send_Requests()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(AzureSession());
        string? createBody = null;
        string? mergeBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullrequests")
            {
                createBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{\"pullRequestId\":53,\"title\":\"Create PR\",\"description\":\"desc\",\"status\":\"active\",\"isDraft\":true,\"creationDate\":\"2026-10-17T11:00:00Z\",\"url\":\"https://dev.azure.com/octo-org/ProjectOne/_git/GitDevExLite/pullrequest/53\",\"sourceRefName\":\"refs/heads/feature/x\",\"targetRefName\":\"refs/heads/main\",\"createdBy\":{\"id\":\"aad-1\",\"displayName\":\"Octo Cat\",\"uniqueName\":\"octo@example.com\"},\"reviewers\":[]}");
            }

            if (request.Method == HttpMethod.Patch && request.RequestUri!.AbsolutePath == "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullrequests/53")
            {
                mergeBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(sessions: store, azureDevOpsHandler: handler);

        var created = await service.CreateAsync(
            AzureDevOpsRepository,
            new CreatePullRequestRequest("Create PR", "desc", "feature/x", "main", true),
            CancellationToken.None);
        await service.MergeAsync(AzureDevOpsRepository, created.Id, new PullRequestMergeRequest(PullRequestMergeStrategy.Squash), CancellationToken.None);

        Assert.Equal(53, created.Number);
        Assert.NotNull(createBody);
        Assert.NotNull(mergeBody);
        Assert.Contains("\"isDraft\":true", createBody);
        Assert.Contains("\"mergeStrategy\":\"squash\"", mergeBody);
    }

    [Fact]
    public async Task AzureDevOps_SignInAsync_Uses_ConnectionData_Profile()
    {
        var store = new InMemoryProviderSessionStore();
        var auth = new AzureDevOpsPullRequestAuthService(
            store,
            new HttpClient(CreateHandler(
                (HttpMethod.Get, "/octo-org/_apis/connectionData?connectOptions=IncludeServices&lastChangeId=-1&lastChangeId64=-1", "{\"authenticatedUser\":{\"id\":\"aad-1\",\"displayName\":\"Octo Cat\",\"uniqueName\":\"octo@example.com\"}}")))
            {
                BaseAddress = new Uri("https://dev.azure.com/")
            },
            (_, _, _) => Task.FromResult<string?>("test-token"));

        var session = await auth.SignInAsync(AzureDevOpsRepository, CancellationToken.None);

        Assert.Equal("aad-1", session.Account.Id);
        Assert.Equal("Octo Cat", session.Account.DisplayName);
        Assert.Equal("octo@example.com", session.Account.UserName);
    }

    [Fact]
    public async Task AzureDevOps_AddInlineCommentAsync_Creates_Thread_With_Context()
    {
        var store = new InMemoryProviderSessionStore();
        store.Save(AzureSession());
        string? requestBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/octo-org/ProjectOne/_apis/git/repositories/GitDevExLite/pullRequests/52/threads")
            {
                requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{\"id\":9,\"status\":\"active\",\"threadContext\":{\"filePath\":\"/src/file.cs\",\"rightFileStart\":{\"line\":14}},\"comments\":[{\"id\":11,\"content\":\"Please rename this\",\"publishedDate\":\"2026-10-17T12:00:00Z\",\"author\":{\"displayName\":\"Octo Cat\",\"uniqueName\":\"octo@example.com\"}}]}");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(sessions: store, azureDevOpsHandler: handler);

        var thread = await service.AddInlineCommentAsync(
            AzureDevOpsRepository,
            "52",
            new PullRequestCommentDraft(new PullRequestCommentAnchor("src/file.cs", 14, 14), "Please rename this"),
            CancellationToken.None);

        Assert.Equal("9", thread.Id);
        Assert.NotNull(thread.Anchor);
        Assert.Equal("/src/file.cs", thread.Anchor!.Path);
        Assert.Equal(14, thread.Anchor.Line);
        Assert.NotNull(requestBody);

        using var document = JsonDocument.Parse(requestBody);
        var root = document.RootElement;
        Assert.Equal("/src/file.cs", root.GetProperty("threadContext").GetProperty("filePath").GetString());
        Assert.Equal(14, root.GetProperty("threadContext").GetProperty("rightFileStart").GetProperty("line").GetInt32());
        Assert.Equal("Please rename this", root.GetProperty("comments")[0].GetProperty("content").GetString());
    }

    private static PullRequestService CreateService(HttpMessageHandler? handler = null, IProviderSessionStore? sessions = null, HttpMessageHandler? azureDevOpsHandler = null)
    {
        var client = new HttpClient(handler ?? CreateHandler())
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var azureDevOpsClient = new HttpClient(azureDevOpsHandler ?? CreateHandler())
        {
            BaseAddress = new Uri("https://dev.azure.com/")
        };
        return new PullRequestService(
            sessions ?? new InMemoryProviderSessionStore(),
            http: client,
            azureDevOpsHttp: azureDevOpsClient,
            tokenProvider: (_, _, _) => Task.FromResult<string?>("test-token"));
    }

    private static ProviderSession Session() =>
        new(HostingProviderKind.GitHub, "github.com", new ProviderAccount("octocat", "Octo Cat", "octocat", "octo@example.com"), true);

    private static ProviderSession AzureSession() =>
        new(HostingProviderKind.AzureDevOps, "dev.azure.com", new ProviderAccount("aad-1", "Octo Cat", "octo@example.com", "octo@example.com"), true);

    private static HttpMessageHandler CreateHandler(params (HttpMethod Method, string PathAndQuery, string Body)[] responses) =>
        new StubHttpMessageHandler(request =>
        {
            var match = responses.FirstOrDefault(r => r.Method == request.Method && request.RequestUri!.PathAndQuery == r.PathAndQuery);
            return match == default
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(match.Body);
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
