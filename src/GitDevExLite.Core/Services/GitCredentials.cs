using System.Text.RegularExpressions;

namespace GitDevExLite.Core.Services;

/// <summary>Credential helpers built on <c>git credential</c>, so Git Credential Manager and any configured helper are reused.</summary>
public static partial class GitCredentials
{
    [GeneratedRegex(@"^[\w.\-]+@[\w.\-]+:")]
    private static partial Regex ScpLike();

    private static Task<bool>? _gcmAvailable;

    public static bool IsSshUrl(string? url) =>
        url is not null && (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || ScpLike().IsMatch(url));

    public static bool IsAzureDevOpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase));

    public static bool TryParseHttp(string? url, out string protocol, out string host)
    {
        protocol = host = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return false;
        protocol = uri.Scheme;
        host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return true;
    }

    public static Task<bool> IsGcmAvailableAsync() => _gcmAvailable ??= ProbeGcmAsync();

    private static async Task<bool> ProbeGcmAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await GitProcess.RunAsync(null, new[] { "credential-manager", "--version" }, null, cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Forces Git Credential Manager's interactive sign-in for the URL's host and stores the result.</summary>
    public static async Task SignInAsync(string url, CancellationToken ct)
    {
        var request = BuildRequest(url, includePath: IsAzureDevOpsUrl(url));
        var env = new Dictionary<string, string> { ["GCM_INTERACTIVE"] = "always" };
        var filled = await GitProcess.RunAsync(null, new[] { "-c", "credential.useHttpPath=true", "credential", "fill" }, null, ct, env, request);
        if (!filled.Contains("password=", StringComparison.Ordinal))
            throw new GitCommandException("Sign-in did not return a credential.", 1);
        await GitProcess.RunAsync(null, new[] { "-c", "credential.useHttpPath=true", "credential", "approve" }, null, ct, null, filled.TrimEnd() + "\n\n");
    }

    public static async Task StoreTokenAsync(string url, string username, string token, CancellationToken ct)
    {
        RejectControlChars(username);
        RejectControlChars(token);
        if (username.Length == 0 || token.Length == 0)
            throw new ArgumentException("Username and token are required.");

        var input = BuildRequest(url, includePath: IsAzureDevOpsUrl(url)).TrimEnd('\n') + $"\nusername={username}\npassword={token}\n\n";
        await GitProcess.RunAsync(null, new[] { "credential", "approve" }, null, ct, null, input);
        if (IsAzureDevOpsUrl(url))
        {
            // PR API authentication requests a host-level credential, while Git operations
            // need the organization path. Keep both entries available to the configured helper.
            var hostOnly = BuildRequest(url).TrimEnd('\n') + $"\nusername={username}\npassword={token}\n\n";
            await GitProcess.RunAsync(null, new[] { "credential", "approve" }, null, ct, null, hostOnly);
        }
    }

    public static async Task StoreTokenAsync(string protocol, string host, string username, string token, CancellationToken ct)
    {
        RejectControlChars(protocol);
        RejectControlChars(host);
        RejectControlChars(username);
        RejectControlChars(token);
        if (protocol.Length == 0 || host.Length == 0 || username.Length == 0 || token.Length == 0)
            throw new ArgumentException("Protocol, host, username and token are required.");

        var input = $"protocol={protocol}\nhost={host}\nusername={username}\npassword={token}\n\n";
        await GitProcess.RunAsync(null, new[] { "credential", "approve" }, null, ct, null, input);
    }

    public static async Task EnableHttpPathAsync(string repositoryPath, string url, CancellationToken ct)
    {
        if (!IsAzureDevOpsUrl(url)) return;
        await GitProcess.RunAsync(repositoryPath, new[] { "config", "--local", "credential.useHttpPath", "true" }, null, ct);
    }

    private static string BuildRequest(string url, bool includePath = false)
    {
        if (!TryParseHttp(url, out var protocol, out var host))
            throw new ArgumentException("Credentials can only be stored for http(s) remotes.");
        RejectControlChars(host);
        var path = includePath ? Uri.UnescapeDataString(new Uri(url).AbsolutePath).TrimStart('/') : "";
        return $"protocol={protocol}\nhost={host}\n" + (path.Length > 0 ? $"path={path}\n" : "") + "\n";
    }

    private static void RejectControlChars(string value)
    {
        if (value.Any(c => c is '\n' or '\r' or '\0'))
            throw new ArgumentException("Credential values must not contain line breaks.");
    }
}
