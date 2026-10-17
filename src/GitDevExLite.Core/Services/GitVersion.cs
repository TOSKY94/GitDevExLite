using System.Text.RegularExpressions;

namespace GitDevExLite.Core.Services;

public static partial class GitVersion
{
    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex Pattern();

    private static Task<(int Major, int Minor)?>? _cached;

    public static (int Major, int Minor)? Parse(string text)
    {
        var m = Pattern().Match(text);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;
    }

    /// <summary>The installed git version, or null when git is missing.</summary>
    public static Task<(int Major, int Minor)?> GetAsync() => _cached ??= ProbeAsync();

    public static async Task<bool> AtLeastAsync(int major, int minor) =>
        await GetAsync() is { } v && (v.Major > major || (v.Major == major && v.Minor >= minor));

    private static async Task<(int, int)?> ProbeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return Parse(await GitProcess.RunAsync(null, new[] { "--version" }, null, cts.Token));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
