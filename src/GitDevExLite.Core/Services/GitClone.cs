namespace GitDevExLite.Core.Services;

public static class GitClone
{
    /// <summary>Clones <paramref name="url"/> into <paramref name="destination"/> using the git CLI.</summary>
    public static async Task CloneAsync(
        string url, string destination, IProgress<GitProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || url.TrimStart().StartsWith('-'))
            throw new ArgumentException("Enter a valid repository URL.");
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Choose a destination folder.");

        var target = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new ArgumentException("Choose a destination folder.");
        Directory.CreateDirectory(parent);

        // "--" keeps a hostile URL from being parsed as an option.
        await GitProcess.RunAsync(parent, new[] { "clone", "--progress", "--", url.Trim(), target }, progress, ct);
    }

    /// <summary>Suggests a folder name from a clone URL, e.g. ".../repo.git" becomes "repo".</summary>
    public static string SuggestFolderName(string url)
    {
        var s = url.Trim().TrimEnd('/', '\\');
        var i = s.LastIndexOfAny(new[] { '/', '\\', ':' });
        var name = i >= 0 ? s[(i + 1)..] : s;
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
