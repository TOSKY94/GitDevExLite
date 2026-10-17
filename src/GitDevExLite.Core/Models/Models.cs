namespace GitDevExLite.Core.Models;

public enum RefKind { LocalBranch, RemoteBranch, Tag }

public sealed record RefInfo(
    string Name,
    RefKind Kind,
    string TargetSha,
    bool IsHead,
    int? Ahead = null,
    int? Behind = null,
    string? Upstream = null)
{
    /// <summary>Empty when the branch has no upstream or is in sync.</summary>
    public string AheadBehindText => (Ahead, Behind) switch
    {
        (null, null) or (0, 0) or (null, 0) or (0, null) => "",
        (var a, var b) => $"{(a > 0 ? $"\u2191{a}" : "")}{(a > 0 && b > 0 ? " " : "")}{(b > 0 ? $"\u2193{b}" : "")}"
    };
}

public sealed record StashInfo(int Index, string Message, string Sha);

public sealed record CommitInfo(
    string Sha,
    string Summary,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset When,
    IReadOnlyList<string> ParentShas)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
}

/// <summary>A file with both staged and unstaged edits yields one entry per side.</summary>
public sealed record FileChange(string Path, bool IsStaged, string Status);
