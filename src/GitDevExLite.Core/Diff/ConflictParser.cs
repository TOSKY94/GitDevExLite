using System.Text;

namespace GitDevExLite.Core.Diff;

public enum ConflictChoice { Ours, Theirs, Both }

/// <summary>
/// A plain stretch of the file, or one conflict block. For a block, <see cref="Raw"/> is the original marker text,
/// and <see cref="StartLine"/> / <see cref="LineCount"/> locate it in the document (0-based).
/// </summary>
public sealed record ConflictSegment(
    bool IsConflict, string Text, string Ours, string Theirs, string? Base, string Raw, int StartLine, int LineCount);

/// <summary>Reads and edits git conflict markers (merge and diff3 styles) in file text.</summary>
public static class ConflictParser
{
    public static IReadOnlyList<ConflictSegment> Parse(string text)
    {
        var lines = SplitKeepEnds(text);
        var segments = new List<ConflictSegment>();
        var plain = new StringBuilder();
        var plainStart = 0;

        void FlushPlain(int end)
        {
            if (plain.Length == 0) return;
            segments.Add(new ConflictSegment(false, plain.ToString(), "", "", null, plain.ToString(), plainStart, end - plainStart));
            plain.Clear();
        }

        var i = 0;
        while (i < lines.Count)
        {
            if (!IsMarker(lines[i], '<') || !TryReadBlock(lines, i, out var block, out var next))
            {
                if (plain.Length == 0) plainStart = i;
                plain.Append(lines[i]);
                i++;
                continue;
            }

            FlushPlain(i);
            segments.Add(block with { StartLine = i, LineCount = next - i });
            i = next;
            plainStart = i;
        }
        FlushPlain(lines.Count);
        return segments;
    }

    public static int Count(string text) => Parse(text).Count(s => s.IsConflict);

    /// <summary>Replaces the <paramref name="index"/>-th conflict block with the chosen side(s).</summary>
    public static string Resolve(string text, int index, ConflictChoice choice)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var s in Parse(text))
        {
            if (!s.IsConflict) { sb.Append(s.Text); continue; }
            if (n++ != index) { sb.Append(s.Raw); continue; }
            sb.Append(choice switch
            {
                ConflictChoice.Ours => s.Ours,
                ConflictChoice.Theirs => s.Theirs,
                _ => s.Ours + s.Theirs
            });
        }
        return sb.ToString();
    }

    private static bool TryReadBlock(List<string> lines, int start, out ConflictSegment block, out int next)
    {
        var ours = new StringBuilder();
        var theirs = new StringBuilder();
        StringBuilder? baseText = null;
        var raw = new StringBuilder(lines[start]);
        var section = 0; // 0 ours, 1 base, 2 theirs

        for (var j = start + 1; j < lines.Count; j++)
        {
            var line = lines[j];
            raw.Append(line);
            if (section == 0 && IsMarker(line, '|')) { baseText = new StringBuilder(); section = 1; continue; }
            if (section < 2 && IsSeparator(line)) { section = 2; continue; }
            if (section == 2 && IsMarker(line, '>'))
            {
                block = new ConflictSegment(true, "", ours.ToString(), theirs.ToString(), baseText?.ToString(), raw.ToString(), start, 0);
                next = j + 1;
                return true;
            }
            (section == 0 ? ours : section == 1 ? baseText! : theirs).Append(line);
        }

        block = null!;
        next = start + 1;
        return false;
    }

    private static string Trimmed(string line) => line.TrimEnd('\r', '\n');

    private static bool IsMarker(string line, char c)
    {
        var t = Trimmed(line);
        return t.Length >= 7 && t.StartsWith(new string(c, 7), StringComparison.Ordinal) && (t.Length == 7 || t[7] == ' ');
    }

    private static bool IsSeparator(string line) => Trimmed(line) == "=======";

    private static List<string> SplitKeepEnds(string text)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            list.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        if (start < text.Length) list.Add(text[start..]);
        return list;
    }
}
