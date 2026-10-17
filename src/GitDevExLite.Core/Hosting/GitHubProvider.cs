using System.Text.Json;

namespace GitDevExLite.Core.Hosting;

public sealed class GitHubProvider : IPullRequestProvider
{
    private readonly ApiClient _api;
    private readonly string _base;
    private string? _me;

    public GitHubProvider(RemoteRepo repo, HttpClient http, string token)
    {
        Repo = repo;
        _api = new ApiClient(http, ApiClient.AuthFor(HostKind.GitHub, token));
        var api = repo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : $"https://{repo.Host}/api/v3";
        _base = $"{api}/repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";
        _apiRoot = api;
    }

    private readonly string _apiRoot;

    public string Name => "GitHub";

    public RemoteRepo Repo { get; }

    private static void Headers(HttpRequestMessage r)
    {
        r.Headers.Accept.Clear();
        r.Headers.Accept.ParseAdd("application/vnd.github+json");
        r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private Task<JsonElement> Send(HttpMethod m, string url, object? body, CancellationToken ct) =>
        _api.SendAsync(m, url, body, ct, Headers);

    private async Task<string> MeAsync(CancellationToken ct) =>
        _me ??= (await Send(HttpMethod.Get, $"{_apiRoot}/user", null, ct)).Str("login");

    public async Task<IReadOnlyList<PullRequest>> ListAsync(PrFilter filter, CancellationToken ct)
    {
        var list = await Send(HttpMethod.Get, $"{_base}/pulls?state=open&per_page=100", null, ct);
        var all = list.Arr().Select(Map).ToList();
        if (filter == PrFilter.All) return all;

        var me = await MeAsync(ct);
        return filter == PrFilter.Mine
            ? all.Where(p => p.Author.Equals(me, StringComparison.OrdinalIgnoreCase)).ToList()
            : all.Where(p => p.Reviewers.Contains(me, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static PullRequest Map(JsonElement p)
    {
        var number = p.Int("number");
        return new PullRequest(
            number, p.Str("title"), p.Str("body"), p.Obj("user").Str("login"),
            p.Obj("head").Str("ref"), p.Obj("base").Str("ref"),
            p.Bool("merged") ? "merged" : p.Str("state"), p.Bool("draft"), p.Str("html_url"),
            p.Date("created_at") ?? default, p.Obj("head").Str("sha"),
            p.Arr("requested_reviewers").Select(r => r.Str("login")).ToList(),
            // GitHub exposes every PR (including forks) under refs/pull.
            $"+refs/pull/{number}/head:refs/heads/pr-{number}");
    }

    public async Task<PullRequestDetail> GetAsync(int number, CancellationToken ct)
    {
        var prJson = await Send(HttpMethod.Get, $"{_base}/pulls/{number}", null, ct);
        var pr = Map(prJson);

        var commitsTask = Send(HttpMethod.Get, $"{_base}/pulls/{number}/commits?per_page=100", null, ct);
        var filesTask = Send(HttpMethod.Get, $"{_base}/pulls/{number}/files?per_page=100", null, ct);
        var reviewCommentsTask = Send(HttpMethod.Get, $"{_base}/pulls/{number}/comments?per_page=100", null, ct);
        var issueCommentsTask = Send(HttpMethod.Get, $"{_base}/issues/{number}/comments?per_page=100", null, ct);
        var reviewsTask = Send(HttpMethod.Get, $"{_base}/pulls/{number}/reviews?per_page=100", null, ct);
        await Task.WhenAll(commitsTask, filesTask, reviewCommentsTask, issueCommentsTask, reviewsTask);

        var commits = commitsTask.Result.Arr().Select(c => new PrCommit(
            c.Str("sha"), c.Obj("commit").Str("message"),
            c.Obj("commit").Obj("author").Str("name"), c.Obj("commit").Obj("author").Date("date"))).ToList();

        var files = filesTask.Result.Arr().Select(f => new PrFile(
            f.Str("filename"), f.Str("status"), f.Int("additions"), f.Int("deletions"),
            f.Str("patch") is { Length: > 0 } patch ? patch : null)).ToList();

        var comments = new List<PrComment>();
        foreach (var c in issueCommentsTask.Result.Arr())
            comments.Add(new PrComment(c.Id("id"), "issue-" + c.Id("id"), c.Obj("user").Str("login"),
                c.Str("body"), c.Date("created_at"), null, null, false));
        foreach (var c in reviewCommentsTask.Result.Arr())
        {
            var id = c.Id("id");
            var replyTo = c.Id("in_reply_to_id") is { Length: > 0 } r ? r : null;
            var line = c.Int("line") is > 0 and var l ? l : c.Int("original_line") is > 0 and var ol ? ol : (int?)null;
            comments.Add(new PrComment(id, replyTo ?? id, c.Obj("user").Str("login"), c.Str("body"),
                c.Date("created_at"), c.Str("path"), line, replyTo is not null));
        }

        var checks = await GetChecksAsync(pr.HeadSha, ct);
        var latest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reviewsTask.Result.Arr())
            if (r.Str("state") is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
                latest[r.Obj("user").Str("login")] = r.Str("state");
        var summary = latest.Count == 0 ? "No reviews yet."
            : string.Join(", ", latest.Select(kv => $"{kv.Key}: {kv.Value.Replace('_', ' ').ToLowerInvariant()}"));

        return new PullRequestDetail(pr, commits, files, comments.OrderBy(c => c.Created).ToList(), checks, summary);
    }

    private async Task<IReadOnlyList<PrCheck>> GetChecksAsync(string sha, CancellationToken ct)
    {
        var checks = new List<PrCheck>();
        if (sha.Length == 0) return checks;
        try
        {
            var runs = await Send(HttpMethod.Get, $"{_base}/commits/{sha}/check-runs?per_page=100", null, ct);
            foreach (var r in runs.Arr("check_runs"))
            {
                var state = r.Str("status") != "completed" ? CheckState.Pending
                    : r.Str("conclusion") switch
                    {
                        "success" => CheckState.Success,
                        "neutral" or "skipped" => CheckState.Neutral,
                        _ => CheckState.Failure
                    };
                checks.Add(new PrCheck(r.Str("name"), state, r.Str("html_url")));
            }

            var status = await Send(HttpMethod.Get, $"{_base}/commits/{sha}/status", null, ct);
            foreach (var s in status.Arr("statuses"))
                checks.Add(new PrCheck(s.Str("context"), s.Str("state") switch
                {
                    "success" => CheckState.Success,
                    "pending" => CheckState.Pending,
                    _ => CheckState.Failure
                }, s.Str("target_url")));
        }
        catch (HostingException ex) when (ex is not HostingAuthException)
        {
            // CI status is informational; the PR itself is still usable without it.
        }
        return checks;
    }

    public async Task<PullRequest> CreateAsync(CreatePrRequest r, CancellationToken ct) =>
        Map(await Send(HttpMethod.Post, $"{_base}/pulls", new Dictionary<string, object?>
        {
            ["title"] = r.Title, ["body"] = r.Body, ["head"] = r.SourceBranch, ["base"] = r.TargetBranch, ["draft"] = r.Draft
        }, ct));

    public async Task ReviewAsync(int number, ReviewVerdict verdict, string? body, CancellationToken ct)
    {
        var ev = verdict switch
        {
            ReviewVerdict.Approve => "APPROVE",
            ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
            _ => "COMMENT"
        };
        if (verdict != ReviewVerdict.Approve && string.IsNullOrWhiteSpace(body))
            throw new HostingException("A comment is required for this review.");
        var payload = new Dictionary<string, object?> { ["event"] = ev };
        if (!string.IsNullOrWhiteSpace(body)) payload["body"] = body;
        await Send(HttpMethod.Post, $"{_base}/pulls/{number}/reviews", payload, ct);
    }

    public Task CommentAsync(int number, string body, CancellationToken ct) =>
        Send(HttpMethod.Post, $"{_base}/issues/{number}/comments", new Dictionary<string, object?> { ["body"] = body }, ct);

    public async Task AddInlineCommentAsync(int number, InlineComment c, CancellationToken ct)
    {
        var pr = await Send(HttpMethod.Get, $"{_base}/pulls/{number}", null, ct);
        await Send(HttpMethod.Post, $"{_base}/pulls/{number}/comments", new Dictionary<string, object?>
        {
            ["body"] = c.Body, ["commit_id"] = pr.Obj("head").Str("sha"), ["path"] = c.Path, ["line"] = c.Line, ["side"] = "RIGHT"
        }, ct);
    }

    public Task ReplyAsync(int number, string threadId, string body, CancellationToken ct)
    {
        if (threadId.StartsWith("issue-", StringComparison.Ordinal))
            return CommentAsync(number, body, ct);
        return Send(HttpMethod.Post, $"{_base}/pulls/{number}/comments/{threadId}/replies",
            new Dictionary<string, object?> { ["body"] = body }, ct);
    }

    public Task MergeAsync(int number, PrMergeMethod method, string? message, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["merge_method"] = method switch { PrMergeMethod.Squash => "squash", PrMergeMethod.Rebase => "rebase", _ => "merge" }
        };
        if (!string.IsNullOrWhiteSpace(message))
        {
            var lines = message.Trim().Split('\n', 2);
            payload["commit_title"] = lines[0].TrimEnd('\r');
            if (lines.Length > 1) payload["commit_message"] = lines[1].Trim();
        }
        return Send(HttpMethod.Put, $"{_base}/pulls/{number}/merge", payload, ct);
    }
}
