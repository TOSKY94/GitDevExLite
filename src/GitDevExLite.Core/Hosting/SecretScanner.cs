using System.Text.RegularExpressions;

namespace GitDevExLite.Core.Hosting;

public sealed record SecretFinding(string Path, int Line, string Rule, string Preview);

/// <summary>Scans text or added diff lines for likely secrets. Append "gitdevex:allow" to a line to ignore it.</summary>
public static class SecretScanner
{
    private static readonly (string Name, Regex Rx)[] Rules =
    {
        ("AWS access key", new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Private key", new Regex(@"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY( BLOCK)?-----", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})\b", RegexOptions.Compiled)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        ("Azure storage key", new Regex(@"AccountKey=[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled)),
        ("Stripe live key", new Regex(@"\b[sr]k_live_[0-9A-Za-z]{16,}\b", RegexOptions.Compiled)),
        ("JSON Web Token", new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.Compiled)),
        ("Hard-coded credential", new Regex(
            @"(?i)\b(password|passwd|pwd|api[_-]?key|secret|token|client[_-]?secret)\b[""']?\s*[:=]\s*[""']([^""'\s]{8,})[""']",
            RegexOptions.Compiled)),
    };

    private static readonly Regex Placeholder = new(
        @"(?i)(example|sample|changeme|placeholder|your[_-]|<.*>|\$\{|\{\{|xxxx|\*\*\*|dummy|test|todo|env\.|getenv)",
        RegexOptions.Compiled);

    public static IReadOnlyList<SecretFinding> ScanText(string path, string? text)
    {
        var results = new List<SecretFinding>();
        if (string.IsNullOrEmpty(text) || text.Contains('\0')) return results;
        var n = 0;
        foreach (var line in text.Split('\n'))
        {
            n++;
            var f = ScanLine(path, n, line.TrimEnd('\r'));
            if (f is not null) results.Add(f);
        }
        return results;
    }

    /// <summary>Scans the added lines of every staged file.</summary>
    public static IReadOnlyList<SecretFinding> ScanStaged(Services.IGitService git)
    {
        var results = new List<SecretFinding>();
        foreach (var change in git.GetStatus().Where(c => c.IsStaged))
        {
            Models.FileDiff diff;
            try { diff = git.GetWorkingDiff(change.Path, staged: true); }
            catch (Exception) { continue; }
            if (diff.IsBinary) continue;
            foreach (var line in diff.Hunks.SelectMany(h => h.Lines))
            {
                if (line.Kind != Models.DiffLineKind.Added || line.NewNo is not { } no) continue;
                var f = ScanLine(change.Path, no, line.Text);
                if (f is not null) results.Add(f);
            }
        }
        return results;
    }

    /// <summary>Scans only added lines of a unified diff.</summary>
    public static IReadOnlyList<SecretFinding> ScanDiff(string path, string? patch)
    {
        var results = new List<SecretFinding>();
        if (string.IsNullOrEmpty(patch)) return results;
        var newLine = 0;
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@"))
            {
                var m = Regex.Match(line, @"\+(\d+)");
                newLine = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
            else if (line.StartsWith("+++") || line.StartsWith("---")) continue;
            else if (line.StartsWith('+'))
            {
                var f = ScanLine(path, newLine, line[1..]);
                if (f is not null) results.Add(f);
                newLine++;
            }
            else if (line.StartsWith(' ')) newLine++;
        }
        return results;
    }

    private static SecretFinding? ScanLine(string path, int lineNo, string line)
    {
        if (line.Length > 2000 || line.Contains("gitdevex:allow", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var (name, rx) in Rules)
        {
            var m = rx.Match(line);
            if (!m.Success) continue;
            if (name == "Hard-coded credential" && Placeholder.IsMatch(m.Groups[2].Value)) continue;
            return new SecretFinding(path, lineNo, name, Redact(m.Value));
        }
        return null;
    }

    private static string Redact(string s) =>
        s.Length <= 8 ? new string('*', s.Length) : s[..4] + new string('*', Math.Min(12, s.Length - 4));
}
