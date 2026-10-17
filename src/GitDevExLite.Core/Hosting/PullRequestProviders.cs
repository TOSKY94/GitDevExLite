using GitDevExLite.Core.Services;

namespace GitDevExLite.Core.Hosting;

public static class PullRequestProviders
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static IPullRequestProvider Create(RemoteRepo repo, string token, HttpClient? http = null) => repo.Kind switch
    {
        HostKind.GitHub => new GitHubProvider(repo, http ?? Shared, token),
        HostKind.AzureDevOps => new AzureDevOpsProvider(repo, http ?? Shared, token),
        _ => throw new HostingException($"Pull requests are not supported for {repo.Kind} yet.")
    };

    /// <summary>Finds a stored token without prompting: environment variables first, then the git credential helper.</summary>
    public static async Task<string?> TryGetTokenAsync(RemoteRepo repo, CancellationToken ct)
    {
        var names = repo.Kind == HostKind.GitHub
            ? new[] { "GITHUB_TOKEN", "GH_TOKEN" }
            : new[] { "AZURE_DEVOPS_EXT_PAT", "AZURE_DEVOPS_PAT" };
        foreach (var n in names)
            if (Environment.GetEnvironmentVariable(n) is { Length: > 0 } v) return v;

        var host = repo.Kind == HostKind.AzureDevOps ? "dev.azure.com" : repo.Host;
        try
        {
            var env = new Dictionary<string, string> { ["GCM_INTERACTIVE"] = "never", ["GIT_TERMINAL_PROMPT"] = "0" };
            var output = await GitProcess.RunAsync(null, new[] { "credential", "fill" }, null, ct, env,
                $"protocol=https\nhost={host}\n\n");
            foreach (var line in output.Split('\n'))
                if (line.StartsWith("password=", StringComparison.Ordinal))
                    return line["password=".Length..].TrimEnd('\r') is { Length: > 0 } p ? p : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
        return null;
    }

    public static (string Title, string Body) DraftFor(string branch, IReadOnlyList<string> commitMessages)
    {
        var title = commitMessages.Count == 1
            ? commitMessages[0].Split('\n')[0]
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                branch.Split('/').Last().Replace('-', ' ').Replace('_', ' '));
        var body = commitMessages.Count == 0
            ? ""
            : "## Changes\n\n" + string.Join("\n", commitMessages.Select(m => "- " + m.Split('\n')[0]));
        return (title, body);
    }
}
