using System.Text.RegularExpressions;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public sealed partial class SecretScanner : ISecretScanner
{
    private const int MaxPreviewLength = 120;

    private static readonly SecretRule[] Rules =
    [
        new("GitHub token", GitHubTokenRegex()),
        new("Azure storage key", AzureStorageKeyRegex()),
        new("Connection string secret", ConnectionStringSecretRegex()),
        new("Password assignment", PasswordAssignmentRegex()),
        new("API key assignment", ApiKeyAssignmentRegex())
    ];

    public IReadOnlyList<SecretMatch> Scan(IGitService git, IReadOnlyList<FileChange> files)
    {
        var matches = new List<SecretMatch>();
        foreach (var group in files.GroupBy(f => (f.Path, f.IsStaged)))
        {
            var content = git.GetFileContent(group.Key.Path, group.Key.IsStaged);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                foreach (var rule in Rules)
                {
                    if (!rule.Pattern.IsMatch(line)) continue;
                    matches.Add(new SecretMatch(
                        group.Key.Path,
                        group.Key.IsStaged,
                        index + 1,
                        rule.Name,
                        TrimPreview(line)));
                    break;
                }
            }
        }

        return matches;
    }

    private static string TrimPreview(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxPreviewLength ? trimmed : trimmed[..MaxPreviewLength] + "...";
    }

    private sealed record SecretRule(string Name, Regex Pattern);

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9_]{20,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"AccountKey\s*=\s*[A-Za-z0-9+/=]{40,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AzureStorageKeyRegex();

    [GeneratedRegex(@"(password|pwd|secret|token)\s*=\s*[^;\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecretRegex();

    [GeneratedRegex(@"[""']?(password|passwd)[""']?\s*[:=]\s*[""'][^""']{4,}[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordAssignmentRegex();

    [GeneratedRegex(@"[""']?(api[-_ ]?key|client[-_ ]?secret|access[-_ ]?token)[""']?\s*[:=]\s*[""'][^""']{8,}[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyAssignmentRegex();
}