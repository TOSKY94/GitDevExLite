using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public interface IPullRequestAuthService
{
    Task<ProviderSession> SignInAsync(HostedRepositoryId repository, CancellationToken ct);
    Task SignOutAsync(HostedRepositoryId repository, CancellationToken ct);
}