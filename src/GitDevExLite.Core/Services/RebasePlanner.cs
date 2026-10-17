using System.Text;

namespace GitDevExLite.Core.Services;

public enum RebaseAction { Pick, Reword, Edit, Squash, Fixup, Drop }

public sealed record RebaseEntry(RebaseAction Action, string Sha, string Summary, string Message);

/// <summary>Messages maps a group's first commit SHA to the final message the user wants for it.</summary>
public sealed record RebaseRequest(IReadOnlyList<RebaseEntry> Entries, IReadOnlyDictionary<string, string> Messages);

/// <summary>The todo text has <c>{{MSG:n}}</c> tokens that stand for the path of Messages[n].</summary>
public sealed record RebasePlan(string Todo, IReadOnlyList<string> Messages);

/// <summary>
/// Turns the user's choices into a git todo list. Squashes become fixups and messages are applied with an
/// <c>exec git commit --amend -F</c> line, so git never has to open an editor.
/// </summary>
public static class RebasePlanner
{
    public static string? Validate(IReadOnlyList<RebaseEntry> entries)
    {
        if (entries.Count == 0) return "There are no commits to rebase.";
        var kept = entries.Where(e => e.Action != RebaseAction.Drop).ToList();
        if (kept.Count == 0) return "At least one commit must be kept.";
        if (kept[0].Action is RebaseAction.Squash or RebaseAction.Fixup)
            return "The first commit cannot be squashed or fixed up; there is nothing to merge it into.";
        return null;
    }

    public static bool NeedsMessage(IReadOnlyList<RebaseEntry> entries, int headIndex) =>
        entries[headIndex].Action == RebaseAction.Reword ||
        Members(entries, headIndex).Any(m => entries[m].Action == RebaseAction.Squash);

    public static string DefaultMessage(IReadOnlyList<RebaseEntry> entries, int headIndex)
    {
        var sb = new StringBuilder(entries[headIndex].Message.Trim());
        foreach (var m in Members(entries, headIndex).Where(m => entries[m].Action == RebaseAction.Squash))
            sb.Append("\n\n").Append(entries[m].Message.Trim());
        return sb.ToString();
    }

    /// <summary>Indexes of the entries squashed or fixed up into the group that starts at <paramref name="headIndex"/>.</summary>
    public static IReadOnlyList<int> Members(IReadOnlyList<RebaseEntry> entries, int headIndex)
    {
        var members = new List<int>();
        for (var i = headIndex + 1; i < entries.Count; i++)
        {
            var a = entries[i].Action;
            if (a == RebaseAction.Drop) continue;
            if (a is RebaseAction.Squash or RebaseAction.Fixup) members.Add(i);
            else break;
        }
        return members;
    }

    public static RebasePlan Build(IReadOnlyList<RebaseEntry> entries, IReadOnlyDictionary<string, string>? messages)
    {
        if (Validate(entries) is { } error) throw new ArgumentException(error);

        var todo = new StringBuilder();
        var texts = new List<string>();
        var execAfter = new Dictionary<int, int>(); // index of a group's last line -> message number

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Action is RebaseAction.Squash or RebaseAction.Fixup or RebaseAction.Drop) continue;
            if (!NeedsMessage(entries, i)) continue;

            var members = Members(entries, i);
            var last = members.Count == 0 ? i : members[^1];
            var text = messages is not null && messages.TryGetValue(entries[i].Sha, out var custom)
                ? custom
                : DefaultMessage(entries, i);
            execAfter[last] = texts.Count;
            texts.Add(text.Trim() + "\n");
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var verb = e.Action switch
            {
                RebaseAction.Edit => "edit",
                RebaseAction.Squash or RebaseAction.Fixup => "fixup",
                RebaseAction.Drop => "drop",
                _ => "pick"
            };
            todo.Append(verb).Append(' ').Append(e.Sha).Append(' ').AppendLine(OneLine(e.Summary));
            if (execAfter.TryGetValue(i, out var n))
                todo.Append("exec git commit --amend --allow-empty -F {{MSG:").Append(n).AppendLine("}}");
        }
        return new RebasePlan(todo.ToString(), texts);
    }

    private static string OneLine(string s) => s.ReplaceLineEndings(" ").Trim();
}
