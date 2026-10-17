using System.Text;

namespace GitDevExLite.Core.Hosting;

/// <summary>Minimal line-based unified diff, for providers that only hand back file contents.</summary>
public static class UnifiedDiff
{
    private const long MaxCells = 4_000_000;

    /// <summary>Returns hunks only (no file header), or null when the files are too large to compare cheaply.</summary>
    public static string? Create(string? oldText, string? newText, int context = 3)
    {
        var a = Split(oldText);
        var b = Split(newText);

        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var ma = a.Count - prefix - suffix;
        var mb = b.Count - prefix - suffix;
        if ((long)(ma + 1) * (mb + 1) > MaxCells) return null;

        var ops = new List<(char Tag, string Text)>();
        for (var i = 0; i < prefix; i++) ops.Add((' ', a[i]));

        var lcs = new int[(ma + 1) * (mb + 1)];
        int At(int i, int j) => i * (mb + 1) + j;
        for (var i = ma - 1; i >= 0; i--)
            for (var j = mb - 1; j >= 0; j--)
                lcs[At(i, j)] = a[prefix + i] == b[prefix + j]
                    ? lcs[At(i + 1, j + 1)] + 1
                    : Math.Max(lcs[At(i + 1, j)], lcs[At(i, j + 1)]);

        int x = 0, y = 0;
        while (x < ma && y < mb)
        {
            if (a[prefix + x] == b[prefix + y]) { ops.Add((' ', a[prefix + x])); x++; y++; }
            else if (lcs[At(x + 1, y)] >= lcs[At(x, y + 1)]) { ops.Add(('-', a[prefix + x])); x++; }
            else { ops.Add(('+', b[prefix + y])); y++; }
        }
        for (; x < ma; x++) ops.Add(('-', a[prefix + x]));
        for (; y < mb; y++) ops.Add(('+', b[prefix + y]));
        for (var i = 0; i < suffix; i++) ops.Add((' ', a[a.Count - suffix + i]));

        return Render(ops, context);
    }

    private static string Render(List<(char Tag, string Text)> ops, int context)
    {
        var sb = new StringBuilder();
        var n = ops.Count;
        var i = 0;
        int oldNo = 1, newNo = 1;
        var starts = new int[n + 1];
        var olds = new int[n + 1];
        for (var k = 0; k < n; k++)
        {
            olds[k] = oldNo; starts[k] = newNo;
            if (ops[k].Tag != '+') oldNo++;
            if (ops[k].Tag != '-') newNo++;
        }

        while (i < n)
        {
            if (ops[i].Tag == ' ') { i++; continue; }
            var from = Math.Max(0, i - context);
            var to = i;
            // Extend while another change is within 2*context lines.
            while (true)
            {
                var j = to;
                while (j < n && ops[j].Tag != ' ') j++;
                var gapEnd = j;
                while (gapEnd < n && ops[gapEnd].Tag == ' ') gapEnd++;
                if (gapEnd < n && gapEnd - j <= context * 2) { to = gapEnd; continue; }
                to = Math.Min(n, j + context);
                break;
            }

            int oc = 0, nc = 0;
            var body = new StringBuilder();
            for (var k = from; k < to; k++)
            {
                body.Append(ops[k].Tag).Append(ops[k].Text).Append('\n');
                if (ops[k].Tag != '+') oc++;
                if (ops[k].Tag != '-') nc++;
            }
            var os = oc == 0 ? olds[from] - 1 : olds[from];
            var ns = nc == 0 ? starts[from] - 1 : starts[from];
            sb.Append($"@@ -{os},{oc} +{ns},{nc} @@\n").Append(body);
            i = to;
        }
        return sb.ToString();
    }

    private static List<string> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
