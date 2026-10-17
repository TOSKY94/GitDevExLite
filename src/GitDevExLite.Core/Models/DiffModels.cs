namespace GitDevExLite.Core.Models;

public sealed record ChangedFile(string Path, string? OldPath, string Status);

public enum DiffLineKind { Context, Added, Removed, NoNewlineMarker }

/// <summary>Text excludes the leading diff prefix character and the line terminator '\n'.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNo, int? NewNo);

public sealed record DiffHunk(
    string Header,
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<DiffLine> Lines);

public sealed record FileDiff(
    string Path,
    IReadOnlyList<DiffHunk> Hunks,
    bool IsBinary,
    bool IsNew,
    bool IsDeleted)
{
    public static FileDiff Empty(string path) => new(path, Array.Empty<DiffHunk>(), false, false, false);
}

/// <summary>Identifies one line inside a <see cref="FileDiff"/> by hunk and line index.</summary>
public readonly record struct LineRef(int Hunk, int Line);
