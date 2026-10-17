namespace GitDevExLite.Core.Services;

public enum RepoOperation { None, Merge, Rebase, CherryPick, Revert }

public enum MergeMode { Default, NoFastForward, FastForwardOnly, Squash }

public enum ResetMode { Soft, Mixed, Hard }

public enum ConflictSide { Ours, Theirs }

/// <summary>A null side means the file does not exist there (a delete or add conflict). Working is empty for binary files.</summary>
public sealed record ConflictFile(string Path, string? Base, string? Ours, string? Theirs, string Working, bool IsBinary);

/// <summary>Step and Total are only known for rebases.</summary>
public sealed record RepoOperationState(RepoOperation Kind, int? Step = null, int? Total = null)
{
    public static readonly RepoOperationState None = new(RepoOperation.None);

    public bool InProgress => Kind != RepoOperation.None;

    /// <summary>A merge has no "skip"; everything else does.</summary>
    public bool CanSkip => Kind is RepoOperation.Rebase or RepoOperation.CherryPick or RepoOperation.Revert;

    public string Describe() => Kind switch
    {
        RepoOperation.Merge => "Merge in progress",
        RepoOperation.Rebase => Step is { } s && Total is { } t ? $"Rebase in progress ({s}/{t})" : "Rebase in progress",
        RepoOperation.CherryPick => "Cherry-pick in progress",
        RepoOperation.Revert => "Revert in progress",
        _ => ""
    };

    public static RepoOperationState Read(string? gitDirectory)
    {
        if (gitDirectory is null || !Directory.Exists(gitDirectory)) return None;

        var merge = Path.Combine(gitDirectory, "rebase-merge");
        if (Directory.Exists(merge))
            return new(RepoOperation.Rebase, ReadInt(Path.Combine(merge, "msgnum")), ReadInt(Path.Combine(merge, "end")));

        var apply = Path.Combine(gitDirectory, "rebase-apply");
        if (Directory.Exists(apply))
            return new(RepoOperation.Rebase, ReadInt(Path.Combine(apply, "next")), ReadInt(Path.Combine(apply, "last")));

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD"))) return new(RepoOperation.Merge);
        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))) return new(RepoOperation.CherryPick);
        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))) return new(RepoOperation.Revert);

        // A multi-commit pick or revert keeps its queue here between stops.
        var todo = Path.Combine(gitDirectory, "sequencer", "todo");
        if (File.Exists(todo))
        {
            var first = File.ReadLines(todo).FirstOrDefault(l => l.Length > 0 && l[0] != '#') ?? "";
            if (first.StartsWith("revert", StringComparison.Ordinal)) return new(RepoOperation.Revert);
            if (first.StartsWith("pick", StringComparison.Ordinal) || first.StartsWith("p ", StringComparison.Ordinal))
                return new(RepoOperation.CherryPick);
        }
        return None;
    }

    private static int? ReadInt(string file)
    {
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var n) ? n : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>Success is false when git stopped; Paused tells whether it left an operation to continue or abort.</summary>
public sealed record GitOpResult(bool Success, bool Paused, string Output);

/// <summary>Result of a dry-run merge. CanPreview is false when git is too old to simulate one.</summary>
public sealed record MergePreview(
    bool CanPreview,
    bool IsUpToDate,
    bool IsFastForward,
    IReadOnlyList<string> ConflictedFiles,
    string? Message)
{
    public bool IsClean => CanPreview && ConflictedFiles.Count == 0;
}
