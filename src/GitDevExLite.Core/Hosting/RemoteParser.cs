using System.Text.RegularExpressions;

namespace GitDevExLite.Core.Hosting;

/// <summary>Works out the hosting provider and repository coordinates from a remote URL.</summary>
public static partial class RemoteParser
{
    [GeneratedRegex(@"^(?:ssh://)?(?:[\w.\-]+@)?(?<host>[\w.\-]+)(?::\d+)?[:/](?<path>.+?)/?$")]
    private static partial Regex SshLike();

    public static RemoteRepo? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        string host, path;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh")
        {
            host = uri.Host;
            path = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        }
        else if (SshLike().Match(url) is { Success: true } m)
        {
            host = m.Groups["host"].Value;
            path = Uri.UnescapeDataString(m.Groups["path"].Value.Trim('/'));
        }
        else return null;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var h = host.ToLowerInvariant();

        if (h is "dev.azure.com" or "ssh.dev.azure.com" or "vs-ssh.visualstudio.com" || h.EndsWith(".visualstudio.com"))
            return ParseAzure(h, parts, url);

        if (h.Contains("github") && parts.Length >= 2)
            return new RemoteRepo(HostKind.GitHub, host, parts[0], "", parts[1], url);
        if (h.Contains("gitlab") && parts.Length >= 2)
            return new RemoteRepo(HostKind.GitLab, host, parts[0], "", parts[^1], url);
        if (h.Contains("bitbucket") && parts.Length >= 2)
            return new RemoteRepo(HostKind.Bitbucket, host, parts[0], "", parts[1], url);
        return new RemoteRepo(HostKind.Unknown, host, parts.Length > 0 ? parts[0] : "", "", parts.Length > 1 ? parts[^1] : "", url);
    }

    private static RemoteRepo? ParseAzure(string lowerHost, string[] parts, string url)
    {
        string org, project, repo;
        var git = Array.IndexOf(parts, "_git");

        if (lowerHost is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com")
        {
            // v3/org/project/repo
            if (parts.Length < 4 || parts[0] != "v3") return null;
            (org, project, repo) = (parts[1], parts[2], parts[3]);
        }
        else if (lowerHost == "dev.azure.com")
        {
            if (git < 2 || git + 1 >= parts.Length) return null;
            (org, project, repo) = (parts[0], parts[git - 1], parts[git + 1]);
        }
        else
        {
            // https://org.visualstudio.com/[DefaultCollection/]project/_git/repo
            if (git < 1 || git + 1 >= parts.Length) return null;
            org = lowerHost[..lowerHost.IndexOf('.')];
            (project, repo) = (parts[git - 1], parts[git + 1]);
        }
        return new RemoteRepo(HostKind.AzureDevOps, "dev.azure.com", org, project, repo, url);
    }
}
