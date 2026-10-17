using System.Text;
using System.Text.RegularExpressions;

namespace GitDevExLite.Core.Services;

public enum PullMode { Merge, Rebase, FastForwardOnly }

/// <summary>Percent is null when git reports a phase without a percentage.</summary>
public sealed record GitProgress(string Phase, int? Percent, string Text);

public class GitCommandException : Exception
{
    public int ExitCode { get; }

    public GitCommandException(string message, int exitCode) : base(message) => ExitCode = exitCode;
}

/// <summary>Raised when a remote rejected or could not obtain credentials.</summary>
public sealed class GitAuthException : GitCommandException
{
    public GitAuthException(string message, int exitCode) : base(message, exitCode) { }
}

public static partial class GitProgressParser
{
    [GeneratedRegex(@"^(?:remote:\s*)?(?<phase>[A-Za-z][A-Za-z ]*?):\s+(?<pct>\d{1,3})%")]
    private static partial Regex Percent();

    public static bool TryParse(string line, out GitProgress progress)
    {
        var m = Percent().Match(line);
        if (!m.Success)
        {
            progress = null!;
            return false;
        }
        progress = new GitProgress(m.Groups["phase"].Value.Trim(), int.Parse(m.Groups["pct"].Value), line.Trim());
        return true;
    }
}

public static class GitAuthFailure
{
    private static readonly string[] Markers =
    {
        "Authentication failed",
        "could not read Username",
        "could not read Password",
        "terminal prompts disabled",
        "Permission denied (publickey",
        "Invalid username or password",
        "The requested URL returned error: 401",
        "The requested URL returned error: 403",
        "HTTP 401",
        "HTTP 403",
        "Host key verification failed",
    };

    public static bool IsAuthFailure(string stderr) =>
        Markers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));

    internal static string Normalize(StringBuilder sb) => sb.ToString().Trim();
}
