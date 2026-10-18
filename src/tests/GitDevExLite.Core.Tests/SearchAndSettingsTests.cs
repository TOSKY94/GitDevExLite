using GitDevExLite.Core.Graph;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using System.Net;
using System.Net.Http;
using System.Text;

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

    [Fact]
    public void Ai_settings_round_trip_through_repo_settings_store()
    {
        var file = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"), "s.json");
        var store = new RepoSettingsStore(file);

        store.SaveAiSettings(new AiProviderSettings(
            AiProviderKind.OpenAiCompatible,
            "https://api.example.test/v1",
            "gpt-test",
            "work"));

        var loaded = store.LoadAiSettings();
        Assert.Equal(AiProviderKind.OpenAiCompatible, loaded.Kind);
        Assert.Equal("https://api.example.test/v1", loaded.Endpoint);
        Assert.Equal("gpt-test", loaded.Model);
        Assert.Equal("work", loaded.SecretKey);
    }

    [Fact]
    public void Ai_provider_factory_lists_supported_provider_kinds()
    {
        var factory = new OpenAiCompatibleAiProviderFactory(new InMemoryAiCredentialStore());

        var providers = factory.GetAvailableProviders();

        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.OpenAiCompatible);
        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.GitHubModels);
        Assert.Contains(providers, provider => provider.Kind == AiProviderKind.AzureFoundry);
    }

    [Fact]
    public async Task Openai_provider_connection_test_succeeds_on_non_empty_reply()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"choices":[{"message":{"content":"ok"}}]}
                """, Encoding.UTF8, "application/json")
        });
        var provider = new OpenAiCompatibleCommitMessageProvider(
            new HttpClient(handler),
            new AiProviderSettings(AiProviderKind.OpenAiCompatible, "https://api.example.test/v1", "gpt-test"),
            "token");

        await provider.TestConnectionAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Github_models_connection_uses_bearer_auth()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"choices":[{"message":{"content":"ok"}}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var provider = new OpenAiCompatibleCommitMessageProvider(
            new HttpClient(handler),
            new AiProviderSettings(AiProviderKind.GitHubModels, "https://models.inference.ai.azure.com", "gpt-4o-mini"),
            "token");

        await provider.TestConnectionAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("token", captured.Headers.Authorization?.Parameter);
        Assert.False(captured.Headers.Contains("api-key"));
        Assert.Equal("https://models.inference.ai.azure.com/chat/completions", captured.RequestUri?.ToString());
    }

    [Fact]
    public async Task Azure_foundry_connection_uses_api_key_header()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"choices":[{"message":{"content":"ok"}}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var provider = new OpenAiCompatibleCommitMessageProvider(
            new HttpClient(handler),
            new AiProviderSettings(AiProviderKind.AzureFoundry, "https://demo.services.ai.azure.com/models", "gpt-4o-mini"),
            "token");

        await provider.TestConnectionAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.Headers.Authorization);
        Assert.True(captured.Headers.TryGetValues("api-key", out var values));
        Assert.Equal("token", Assert.Single(values));
        Assert.Equal("https://demo.services.ai.azure.com/models/chat/completions", captured.RequestUri?.ToString());
    }

    private sealed class InMemoryAiCredentialStore : IAiCredentialStore
    {
        public Task<string?> TryGetApiKeyAsync(AiProviderSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("token");

        public Task SaveApiKeyAsync(AiProviderSettings settings, string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

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
