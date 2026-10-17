using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public static class PullRequestRemoteResolver
{
    public static HostedRepositoryId? Resolve(string remoteName, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (TryResolveGitHub(remoteName, url, out var github)) return github;
        if (TryResolveAzureDevOps(remoteName, url, out var azureDevOps)) return azureDevOps;
        return null;
    }

    private static bool TryResolveGitHub(string remoteName, string url, out HostedRepositoryId? repository)
    {
        repository = null;
        if (!TryParse(url, out var host, out var segments)) return false;
        if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || segments.Count < 2) return false;

        repository = new HostedRepositoryId(
            HostingProviderKind.GitHub,
            host,
            segments[0],
            TrimGitSuffix(segments[1]),
            RemoteName: remoteName);
        return true;
    }

    private static bool TryResolveAzureDevOps(string remoteName, string url, out HostedRepositoryId? repository)
    {
        repository = null;
        if (!TryParse(url, out var host, out var segments)) return false;

        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Count < 4 || !segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase)) return false;
            repository = new HostedRepositoryId(
                HostingProviderKind.AzureDevOps,
                host,
                segments[0],
                TrimGitSuffix(segments[3]),
                Project: segments[1],
                RemoteName: remoteName);
            return true;
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var owner = host[..host.IndexOf('.')];
            if (segments.Count >= 3 && segments[1].Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                repository = new HostedRepositoryId(
                    HostingProviderKind.AzureDevOps,
                    host,
                    owner,
                    TrimGitSuffix(segments[2]),
                    Project: segments[0],
                    RemoteName: remoteName);
                return true;
            }

            if (segments.Count >= 4 && segments[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase)
                && segments[2].Equals("_git", StringComparison.OrdinalIgnoreCase))
            {
                repository = new HostedRepositoryId(
                    HostingProviderKind.AzureDevOps,
                    host,
                    owner,
                    TrimGitSuffix(segments[3]),
                    Project: segments[1],
                    RemoteName: remoteName);
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string url, out string host, out List<string> segments)
    {
        host = "";
        segments = new List<string>();

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToList();
            return true;
        }

        var marker = url.IndexOf(':');
        var at = url.IndexOf('@');
        if (marker <= 0 || at < 0 || at > marker) return false;

        host = url[(at + 1)..marker];
        segments = url[(marker + 1)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(TrimGitSuffix)
            .ToList();
        return segments.Count > 0;
    }

    private static string TrimGitSuffix(string value) =>
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}
