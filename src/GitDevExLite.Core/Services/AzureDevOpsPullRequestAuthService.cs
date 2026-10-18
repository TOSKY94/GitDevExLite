using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed class AzureDevOpsPullRequestAuthService : IPullRequestAuthService
{
    private readonly IProviderSessionStore _sessions;
    private readonly HttpClient _http;
    private readonly Func<string, string, string?, CancellationToken, Task<string?>> _tokenProvider;

    public AzureDevOpsPullRequestAuthService(
        IProviderSessionStore? sessions = null,
        HttpClient? http = null,
        Func<string, string, CancellationToken, Task<string?>>? tokenProvider = null,
        Func<string, string, string?, CancellationToken, Task<string?>>? tokenProviderWithUsername = null)
    {
        _sessions = sessions ?? new InMemoryProviderSessionStore();
        _http = http ?? AzureDevOpsApiClient.CreateClient();
        _tokenProvider = tokenProviderWithUsername ?? (tokenProvider is null
            ? ProviderCredentialStore.TryGetTokenAsync
            : (protocol, host, _, cancellationToken) => tokenProvider(protocol, host, cancellationToken));
    }

    public async Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct)
    {
        if (repository.Provider != HostingProviderKind.AzureDevOps)
            throw new NotSupportedException($"{repository.Provider} sign-in is not implemented yet.");

        var token = await _tokenProvider("https", repository.Host, repository.Owner, ct);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No Azure DevOps credential was found for this host. Sign in with a personal access token first.");

        // Azure DevOps requires the organization segment for this profile endpoint.
        // Calling /_apis/connectionData at the host root returns 404 even when the PAT is valid.
        var organization = Uri.EscapeDataString(repository.Owner);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{organization}/_apis/connectionData?connectOptions=IncludeServices&lastChangeId=-1&lastChangeId64=-1");
        request.Headers.Authorization = CreateBasicAuth(token);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azure DevOps sign-in failed: {(int)response.StatusCode} {response.ReasonPhrase}");

        var user = await response.Content.ReadFromJsonAsync<AzureDevOpsConnectionDataDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Azure DevOps sign-in failed: user profile was empty.");

        var authenticatedUser = user.AuthenticatedUser
            ?? throw new InvalidOperationException("Azure DevOps sign-in failed: authenticated user was missing.");
        var account = new ProviderAccount(
            authenticatedUser.Id ?? authenticatedUser.UniqueName ?? authenticatedUser.DisplayName ?? repository.Owner,
            authenticatedUser.DisplayName ?? authenticatedUser.UniqueName ?? repository.Owner,
            authenticatedUser.UniqueName,
            authenticatedUser.UniqueName);
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

    private static AuthenticationHeaderValue CreateBasicAuth(string token)
    {
        var bytes = Encoding.ASCII.GetBytes($":{token}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    private sealed record AzureDevOpsConnectionDataDto(AzureDevOpsAuthenticatedUserDto? AuthenticatedUser);
    private sealed record AzureDevOpsAuthenticatedUserDto(string? Id, string? DisplayName, string? UniqueName);
}
