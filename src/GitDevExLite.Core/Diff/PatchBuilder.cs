using System.Text;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Diff;

public static class PatchBuilder
{
    public static HashSet<LineRef> HunkChanges(FileDiff diff, int hunkIndex)
    {
        var set = new HashSet<LineRef>();
        var lines = diff.Hunks[hunkIndex].Lines;
        for (var j = 0; j < lines.Count; j++)
            if (lines[j].Kind is DiffLineKind.Added or DiffLineKind.Removed)
                set.Add(new LineRef(hunkIndex, j));
        return set;
    }

    /// <summary>
    /// Builds a patch containing only the selected changes. With <paramref name="reverse"/> the patch is meant for
    /// <c>git apply -R --cached</c> against a HEAD-to-index diff, so unselected changes are baked in instead of dropped.
    /// Returns null when the selection contains no changes.
    /// </summary>
    public static string? Build(FileDiff diff, IReadOnlySet<LineRef> selected, bool reverse)
    {
        var body = new StringBuilder();
        var delta = 0;
        var totalOld = 0;
        var totalNew = 0;
        var emitted = false;

        for (var h = 0; h < diff.Hunks.Count; h++)
        {
            var hunk = diff.Hunks[h];
            var text = new StringBuilder();
            int oldCount = 0, newCount = 0;
            var anyChange = false;
            var lastEmitted = false;

            for (var j = 0; j < hunk.Lines.Count; j++)
            {
                var line = hunk.Lines[j];
                var isSelected = selected.Contains(new LineRef(h, j));
                switch (line.Kind)
                {
                    case DiffLineKind.Context:
                        Emit(' ');
                        oldCount++; newCount++;
                        break;
                    case DiffLineKind.Added when isSelected:
                        Emit('+');
                        newCount++; anyChange = true;
                        break;
                    case DiffLineKind.Added when reverse:
                        Emit(' ');
                        oldCount++; newCount++;
                        break;
                    case DiffLineKind.Removed when isSelected:
                        Emit('-');
                        oldCount++; anyChange = true;
                        break;
                    case DiffLineKind.Removed when !reverse:
                        Emit(' ');
                        oldCount++; newCount++;
                        break;
                    case DiffLineKind.NoNewlineMarker:
                        if (lastEmitted) text.Append('\\').Append(line.Text).Append('\n');
                        break;
                    default:
                        lastEmitted = false;
                        break;
                }

                void Emit(char prefix)
                {
                    text.Append(prefix).Append(line.Text).Append('\n');
                    lastEmitted = true;
                }
            }

            if (!anyChange) continue;

            body.Append($"@@ -{hunk.OldStart},{oldCount} +{hunk.OldStart + delta},{newCount} @@\n").Append(text);
            delta += newCount - oldCount;
            totalOld += oldCount;
            totalNew += newCount;
            emitted = true;
        }

        if (!emitted) return null;

        var oldName = diff.IsNew && totalOld == 0 ? "/dev/null" : $"a/{diff.Path}";
        var newName = diff.IsDeleted && totalNew == 0 ? "/dev/null" : $"b/{diff.Path}";
        return $"diff --git a/{diff.Path} b/{diff.Path}\n--- {oldName}\n+++ {newName}\n{body}";
    }
}
