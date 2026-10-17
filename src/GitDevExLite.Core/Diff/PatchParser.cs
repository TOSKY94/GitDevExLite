using System.Text.RegularExpressions;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Diff;

public static partial class PatchParser
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>Parses a single-file unified diff as produced by libgit2.</summary>
    public static FileDiff Parse(string path, string patch, bool isNew, bool isDeleted)
    {
        if (patch.Contains("\nBinary files ", StringComparison.Ordinal) ||
            patch.StartsWith("Binary files ", StringComparison.Ordinal))
            return new FileDiff(path, Array.Empty<DiffHunk>(), true, isNew, isDeleted);

        var hunks = new List<DiffHunk>();
        var lines = patch.Split('\n');
        var end = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        var i = 0;
        while (i < end && !lines[i].StartsWith("@@ ", StringComparison.Ordinal)) i++;

        while (i < end)
        {
            var m = HunkHeader().Match(lines[i]);
            if (!m.Success) { i++; continue; }

            var oldStart = int.Parse(m.Groups[1].Value);
            var oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            var newStart = int.Parse(m.Groups[3].Value);
            var newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
            var header = lines[i];
            i++;

            var body = new List<DiffLine>();
            var oldNo = oldStart;
            var newNo = newStart;
            while (i < end && !lines[i].StartsWith("@@ ", StringComparison.Ordinal))
            {
                var l = lines[i];
                if (l.Length == 0)
                {
                    body.Add(new DiffLine(DiffLineKind.Context, "", oldNo++, newNo++));
                }
                else
                {
                    var text = l[1..];
                    switch (l[0])
                    {
                        case '+': body.Add(new DiffLine(DiffLineKind.Added, text, null, newNo++)); break;
                        case '-': body.Add(new DiffLine(DiffLineKind.Removed, text, oldNo++, null)); break;
                        case '\\': body.Add(new DiffLine(DiffLineKind.NoNewlineMarker, text, null, null)); break;
                        default: body.Add(new DiffLine(DiffLineKind.Context, text, oldNo++, newNo++)); break;
                    }
                }
                i++;
            }
            hunks.Add(new DiffHunk(header, oldStart, oldCount, newStart, newCount, body));
        }

        return new FileDiff(path, hunks, false, isNew, isDeleted);
    }
}
