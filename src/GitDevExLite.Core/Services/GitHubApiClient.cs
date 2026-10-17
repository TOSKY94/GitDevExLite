namespace GitDevExLite.Core.Services;

internal static class GitHubApiClient
{
    public static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitDevExLite");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}