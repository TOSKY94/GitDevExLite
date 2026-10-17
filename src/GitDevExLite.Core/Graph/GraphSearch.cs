using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Graph;

public static class GraphSearch
{
    /// <summary>Returns SHAs of commits matching the query by message, author, SHA prefix or ref name; null for an empty query.</summary>
    public static HashSet<string>? Match(
        IEnumerable<CommitInfo> commits,
        IReadOnlyDictionary<string, List<RefInfo>> refsByCommit,
        string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return null;

        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        var result = new HashSet<string>();
        foreach (var c in commits)
        {
            var hit = c.Sha.StartsWith(q, cmp)
                   || c.Message.Contains(q, cmp)
                   || c.AuthorName.Contains(q, cmp)
                   || c.AuthorEmail.Contains(q, cmp)
                   || (refsByCommit.TryGetValue(c.Sha, out var refs) && refs.Any(r => r.Name.Contains(q, cmp)));
            if (hit) result.Add(c.Sha);
        }
        return result;
    }
}
