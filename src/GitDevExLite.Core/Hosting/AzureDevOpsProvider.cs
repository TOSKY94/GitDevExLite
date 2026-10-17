using System.Text.Json;

namespace GitDevExLite.Core.Hosting;

public sealed class AzureDevOpsProvider : IPullRequestProvider
{
    private const string Api = "api-version=7.1";
    private const int MaxPatchedFiles = 40;
    private readonly ApiClient _api;
    private readonly string _orgBase;
    private readonly string _repoBase;
    private string? _meId;

    public AzureDevOpsProvider(RemoteRepo repo, HttpClient http, string token)
    {
        Repo = repo;
        _api = new ApiClient(http, ApiClient.AuthFor(HostKind.AzureDevOps, token));
        _orgBase = $"https://dev.azure.com/{Uri.EscapeDataString(repo.Owner)}";
        _repoBase = $"{_orgBase}/{Uri.EscapeDataString(repo.Project)}/_apis/git/repositories/{Uri.EscapeDataString(repo.Name)}";
    }

    public string Name => "Azure DevOps";

    public RemoteRepo Repo { get; }

    private Task<JsonElement> Send(HttpMethod m, string url, object? body, CancellationToken ct) =>
        _api.SendAsync(m, url, body, ct);

    private async Task<string> MeAsync(CancellationToken ct) =>
        _meId ??= (await Send(HttpMethod.Get, $"{_orgBase}/_apis/connectionData?api-version=7.1-preview.1", null, ct))
            .Obj("authenticatedUser").Str("id");

    private static string Branch(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    public async Task<IReadOnlyList<PullRequest>> ListAsync(PrFilter filter, CancellationToken ct)
    {
        var url = $"{_repoBase}/pullrequests?searchCriteria.status=active&$top=100&{Api}";
        if (filter != PrFilter.All)
        {
            var me = await MeAsync(ct);
            url += filter == PrFilter.Mine ? $"&searchCriteria.creatorId={me}" : $"&searchCriteria.reviewerId={me}";
        }
        var list = await Send(HttpMethod.Get, url, null, ct);
        return list.Arr("value").Select(Map).ToList();
    }

    private PullRequest Map(JsonElement p)
    {
        var id = p.Int("pullRequestId");
        var source = Branch(p.Str("sourceRefName"));
        var status = p.Str("status");
        return new PullRequest(
            id, p.Str("title"), p.Str("description"), p.Obj("createdBy").Str("displayName"),
            source, Branch(p.Str("targetRefName")), status == "active" ? "open" : status, p.Bool("isDraft"),
            $"{_orgBase}/{Uri.EscapeDataString(Repo.Project)}/_git/{Uri.EscapeDataString(Repo.Name)}/pullrequest/{id}",
            p.Date("creationDate") ?? default, p.Obj("lastMergeSourceCommit").Str("commitId"),
            p.Arr("reviewers").Select(r => r.Str("displayName")).ToList(),
            $"+refs/heads/{source}:refs/heads/pr-{id}");
    }

    public async Task<PullRequestDetail> GetAsync(int number, CancellationToken ct)
    {
        var prJson = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}?{Api}", null, ct);
        var pr = Map(prJson);
        var targetSha = prJson.Obj("lastMergeTargetCommit").Str("commitId");

        var commitsJson = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}/commits?{Api}", null, ct);
        var commits = commitsJson.Arr("value").Select(c => new PrCommit(
            c.Str("commitId"), c.Str("comment"), c.Obj("author").Str("name"), c.Obj("author").Date("date"))).ToList();

        var files = await GetFilesAsync(number, targetSha, pr.HeadSha, ct);
        var comments = await GetCommentsAsync(number, ct);
        var checks = await GetChecksAsync(number, prJson.Obj("repository").Obj("project").Str("id"), ct);

        var votes = prJson.Arr("reviewers").Where(r => r.Int("vote") != 0)
            .Select(r => $"{r.Str("displayName")}: {VoteText(r.Int("vote"))}").ToList();
        return new PullRequestDetail(pr, commits, files, comments, checks,
            votes.Count == 0 ? "No reviews yet." : string.Join(", ", votes));
    }

    private static string VoteText(int vote) => vote switch
    {
        10 => "approved",
        5 => "approved with suggestions",
        -5 => "waiting for author",
        -10 => "rejected",
        _ => "no vote"
    };

    private async Task<IReadOnlyList<PrFile>> GetFilesAsync(int number, string targetSha, string sourceSha, CancellationToken ct)
    {
        var iterations = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}/iterations?{Api}", null, ct);
        var last = iterations.Arr("value").Select(i => i.Int("id")).DefaultIfEmpty(0).Max();
        if (last == 0) return Array.Empty<PrFile>();

        var changes = await Send(HttpMethod.Get,
            $"{_repoBase}/pullrequests/{number}/iterations/{last}/changes?$compareTo=0&{Api}", null, ct);
        var files = new List<PrFile>();
        var patched = 0;
        foreach (var c in changes.Arr("changeEntries"))
        {
            var item = c.Obj("item");
            var path = item.Str("path");
            if (path.Length == 0 || item.Bool("isFolder")) continue;
            var kind = c.Str("changeType");
            var status = kind.Contains("add") ? "added" : kind.Contains("delete") ? "removed" : kind.Contains("rename") ? "renamed" : "modified";

            string? patch = null;
            var additions = 0;
            var deletions = 0;
            if (patched < MaxPatchedFiles && targetSha.Length > 0 && sourceSha.Length > 0)
            {
                patched++;
                var oldPath = c.Str("originalPath") is { Length: > 0 } op ? op : path;
                var before = status == "added" ? null : await GetFileAsync(oldPath, targetSha, ct);
                var after = status == "removed" ? null : await GetFileAsync(path, sourceSha, ct);
                if (before is null || after is null || (!before.Contains('\0') && !after.Contains('\0')))
                    patch = UnifiedDiff.Create(before, after);
                if (patch is not null)
                    foreach (var line in patch.Split('\n'))
                    {
                        if (line.StartsWith('+')) additions++;
                        else if (line.StartsWith('-')) deletions++;
                    }
            }
            files.Add(new PrFile(path.TrimStart('/'), status, additions, deletions, patch is { Length: > 0 } ? patch : null));
        }
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Task<string?> GetFileAsync(string path, string commit, CancellationToken ct) =>
        _api.GetTextAsync(
            $"{_repoBase}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={commit}" +
            $"&versionDescriptor.versionType=commit&$format=text&{Api}", ct);

    private async Task<IReadOnlyList<PrComment>> GetCommentsAsync(int number, CancellationToken ct)
    {
        var threads = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}/threads?{Api}", null, ct);
        var result = new List<PrComment>();
        foreach (var t in threads.Arr("value"))
        {
            if (t.Bool("isDeleted")) continue;
            var threadId = t.Id("id");
            var context = t.Obj("threadContext");
            var path = context.Str("filePath") is { Length: > 0 } fp ? fp.TrimStart('/') : null;
            var line = context.Obj("rightFileStart").Int("line") is > 0 and var l ? l : (int?)null;
            var first = true;
            foreach (var c in t.Arr("comments"))
            {
                if (c.Str("commentType") == "system" || c.Bool("isDeleted")) continue;
                result.Add(new PrComment(c.Id("id"), threadId, c.Obj("author").Str("displayName"), c.Str("content"),
                    c.Date("publishedDate"), path, line, !first));
                first = false;
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<PrCheck>> GetChecksAsync(int number, string projectId, CancellationToken ct)
    {
        var checks = new List<PrCheck>();
        try
        {
            var statuses = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}/statuses?{Api}", null, ct);
            foreach (var s in statuses.Arr("value"))
            {
                var ctx = s.Obj("context");
                var name = ctx.Str("genre") is { Length: > 0 } g ? $"{g}/{ctx.Str("name")}" : ctx.Str("name");
                checks.Add(new PrCheck(name, s.Str("state") switch
                {
                    "succeeded" => CheckState.Success,
                    "pending" => CheckState.Pending,
                    "notApplicable" or "notSet" => CheckState.Neutral,
                    _ => CheckState.Failure
                }, s.Str("targetUrl")));
            }

            if (projectId.Length > 0)
            {
                var artifact = Uri.EscapeDataString($"vstfs:///CodeReview/CodeReviewId/{projectId}/{number}");
                var policies = await Send(HttpMethod.Get,
                    $"{_orgBase}/{projectId}/_apis/policy/evaluations?artifactId={artifact}&api-version=7.1-preview.1", null, ct);
                foreach (var e in policies.Arr("value"))
                {
                    var config = e.Obj("configuration");
                    var name = config.Obj("settings").Str("displayName") is { Length: > 0 } d ? d : config.Obj("type").Str("displayName");
                    checks.Add(new PrCheck(name, e.Str("status") switch
                    {
                        "approved" => CheckState.Success,
                        "rejected" or "broken" => CheckState.Failure,
                        "notApplicable" => CheckState.Neutral,
                        _ => CheckState.Pending
                    }, null));
                }
            }
        }
        catch (HostingException ex) when (ex is not HostingAuthException)
        {
            // Pipeline status is informational; the PR itself is still usable without it.
        }
        return checks;
    }

    public async Task<PullRequest> CreateAsync(CreatePrRequest r, CancellationToken ct) =>
        Map(await Send(HttpMethod.Post, $"{_repoBase}/pullrequests?{Api}", new Dictionary<string, object?>
        {
            ["sourceRefName"] = $"refs/heads/{r.SourceBranch}",
            ["targetRefName"] = $"refs/heads/{r.TargetBranch}",
            ["title"] = r.Title,
            ["description"] = r.Body,
            ["isDraft"] = r.Draft
        }, ct));

    public async Task ReviewAsync(int number, ReviewVerdict verdict, string? body, CancellationToken ct)
    {
        if (verdict != ReviewVerdict.Approve && string.IsNullOrWhiteSpace(body))
            throw new HostingException("A comment is required for this review.");
        if (verdict != ReviewVerdict.Comment)
        {
            var me = await MeAsync(ct);
            await Send(HttpMethod.Put, $"{_repoBase}/pullrequests/{number}/reviewers/{me}?{Api}",
                new Dictionary<string, object?> { ["vote"] = verdict == ReviewVerdict.Approve ? 10 : -5 }, ct);
        }
        if (!string.IsNullOrWhiteSpace(body)) await CommentAsync(number, body, ct);
    }

    public Task CommentAsync(int number, string body, CancellationToken ct) => PostThread(number, body, null, ct);

    public Task AddInlineCommentAsync(int number, InlineComment c, CancellationToken ct) => PostThread(number, c.Body, c, ct);

    private Task PostThread(int number, string body, InlineComment? inline, CancellationToken ct)
    {
        var thread = new Dictionary<string, object?>
        {
            ["comments"] = new[] { new Dictionary<string, object?> { ["parentCommentId"] = 0, ["content"] = body, ["commentType"] = 1 } },
            ["status"] = 1
        };
        if (inline is not null)
        {
            var pos = new Dictionary<string, object?> { ["line"] = inline.Line, ["offset"] = 1 };
            thread["threadContext"] = new Dictionary<string, object?>
            {
                ["filePath"] = "/" + inline.Path.TrimStart('/'), ["rightFileStart"] = pos, ["rightFileEnd"] = pos
            };
        }
        return Send(HttpMethod.Post, $"{_repoBase}/pullrequests/{number}/threads?{Api}", thread, ct);
    }

    public Task ReplyAsync(int number, string threadId, string body, CancellationToken ct) =>
        Send(HttpMethod.Post, $"{_repoBase}/pullrequests/{number}/threads/{threadId}/comments?{Api}",
            new Dictionary<string, object?> { ["content"] = body, ["parentCommentId"] = 1, ["commentType"] = 1 }, ct);

    public async Task MergeAsync(int number, PrMergeMethod method, string? message, CancellationToken ct)
    {
        var pr = await Send(HttpMethod.Get, $"{_repoBase}/pullrequests/{number}?{Api}", null, ct);
        var options = new Dictionary<string, object?>
        {
            ["mergeStrategy"] = method switch { PrMergeMethod.Squash => "squash", PrMergeMethod.Rebase => "rebase", _ => "noFastForward" },
            ["deleteSourceBranch"] = false
        };
        if (!string.IsNullOrWhiteSpace(message)) options["mergeCommitMessage"] = message.Trim();

        await Send(HttpMethod.Patch, $"{_repoBase}/pullrequests/{number}?{Api}", new Dictionary<string, object?>
        {
            ["status"] = "completed",
            ["lastMergeSourceCommit"] = new Dictionary<string, object?> { ["commitId"] = pr.Obj("lastMergeSourceCommit").Str("commitId") },
            ["completionOptions"] = options
        }, ct);
    }
}
