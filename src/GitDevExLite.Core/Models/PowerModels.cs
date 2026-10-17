using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed record FileHistoryEntry(CommitInfo Commit, string Path);

public sealed record BlameLine(int LineNumber, string Sha, string Author, DateTimeOffset When, string Summary, string Text)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>One HEAD reflog line. Index 0 is the most recent; PreviousSha is where HEAD pointed before it.</summary>
public sealed record ReflogItem(int Index, string Sha, string PreviousSha, string Message, DateTimeOffset When)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>What "undo the last operation" would do. Exactly one of TargetSha and CheckoutRef is set.</summary>
public sealed record UndoPlan(string Description, string Action, string? TargetSha, string? CheckoutRef);

public sealed record SubmoduleInfo(
    string Name, string Path, string Url, bool Initialized, bool Modified, string? IndexSha, string? WorkdirSha)
{
    public string StatusText => !Initialized ? "not initialized" : Modified ? "modified" : "up to date";
}

public sealed record WorktreeInfo(
    string Path, string? Head, string? Branch, bool IsMain, bool IsDetached, bool IsLocked, bool IsPrunable)
{
    public string Display => Branch ?? (IsDetached && Head is { Length: >= 7 } ? $"(detached {Head[..7]})" : "(no branch)");

    public string Flags => string.Join(", ", new[] { IsMain ? "main" : null, IsLocked ? "locked" : null, IsPrunable ? "missing" : null }
        .Where(f => f is not null));
}
