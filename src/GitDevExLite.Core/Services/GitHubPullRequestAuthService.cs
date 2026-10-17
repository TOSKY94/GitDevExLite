using System.Net.Http.Headers;
using System.Net.Http.Json;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed class GitHubPullRequestAuthService : IPullRequestAuthService
{
    private readonly IProviderSessionStore _sessions;
    private readonly HttpClient _http;
    private readonly Func<string, string, CancellationToken, Task<string?>> _tokenProvider;

    public GitHubPullRequestAuthService(IProviderSessionStore? sessions = null, HttpClient? http = null, Func<string, string, CancellationToken, Task<string?>>? tokenProvider = null)
    {
        _sessions = sessions ?? new InMemoryProviderSessionStore();
        _http = http ?? GitHubApiClient.CreateClient();
        _tokenProvider = tokenProvider ?? ProviderCredentialStore.TryGetTokenAsync;
    }

    public async Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct)
    {
        if (repository.Provider != HostingProviderKind.GitHub)
            throw new NotSupportedException($"{repository.Provider} sign-in is not implemented yet.");

        var token = await _tokenProvider("https", repository.Host, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No GitHub credential was found for this host. Sign in with a personal access token first.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("GitDevExLite");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub sign-in failed: {(int)response.StatusCode} {response.ReasonPhrase}");

        var user = await response.Content.ReadFromJsonAsync<GitHubUserDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("GitHub sign-in failed: user profile was empty.");

        var account = new ProviderAccount(user.Login, user.Name ?? user.Login, user.Login, user.Email);
        var session = new ProviderSession(repository.Provider, repository.Host, account, true, null, repository.RemoteName);
        _sessions.Save(session);
        return session;
    }

    public Task SignOutAsync(HostedRepositoryId repository, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _sessions.Remove(repository.Provider, repository.Host);
        return Task.CompletedTask;
    }

    private sealed record GitHubUserDto(string Login, string? Name, string? Email);
}