namespace GitDevExLite.Core.Models;

public enum RefKind { LocalBranch, RemoteBranch, Tag }

public sealed record RefInfo(string Name, RefKind Kind, string TargetSha, bool IsHead);

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

public sealed record FileChange(string Path, bool IsStaged, string Status);
