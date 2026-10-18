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

    public static bool RequiresAzureDevOpsOrganization(string message) =>
        message.Contains("Cannot determine the organization name", StringComparison.OrdinalIgnoreCase);

    public static string? GetAzureDevOpsOrganization(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAzureDevOpsUrl(url)) return null;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo)) return Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return uri.Host[..^".visualstudio.com".Length];
        return uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

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
    public static async Task SignInAsync(string url, CancellationToken ct, string? azureOrganization = null)
    {
        var isAzureDevOps = IsAzureDevOpsUrl(url);
        azureOrganization = string.IsNullOrWhiteSpace(azureOrganization) ? GetAzureDevOpsOrganization(url) : azureOrganization.Trim();
        if (isAzureDevOps && string.IsNullOrWhiteSpace(azureOrganization))
            throw new InvalidOperationException("Enter the Azure DevOps organization name to continue.");

        var request = BuildRequest(url, includePath: isAzureDevOps, username: azureOrganization);
        var env = new Dictionary<string, string> { ["GCM_INTERACTIVE"] = "always" };
        var filled = await GitProcess.RunAsync(null, new[] { "-c", "credential.useHttpPath=true", "credential", "fill" }, null, ct, env, request);
        if (!filled.Contains("password=", StringComparison.Ordinal))
            throw new GitCommandException("Sign-in did not return a credential.", 1);
        await GitProcess.RunAsync(null, new[] { "-c", "credential.useHttpPath=true", "credential", "approve" }, null, ct, null, filled.TrimEnd() + "\n\n");
        if (isAzureDevOps && TryReadPassword(filled, out var password))
        {
            var hostCredential = BuildRequest(url, username: azureOrganization).TrimEnd('\n') + $"\npassword={password}\n\n";
            await GitProcess.RunAsync(null, new[] { "-c", "credential.useHttpPath=true", "credential", "approve" }, null, ct, null, hostCredential);
        }
    }

    public static async Task StoreTokenAsync(string url, string username, string token, CancellationToken ct, string? azureOrganization = null)
    {
        RejectControlChars(username);
        RejectControlChars(token);
        if (username.Length == 0 || token.Length == 0)
            throw new ArgumentException("Username and token are required.");

        var isAzureDevOps = IsAzureDevOpsUrl(url);
        var credentialUsername = isAzureDevOps && !string.IsNullOrWhiteSpace(azureOrganization)
            ? azureOrganization
            : username;
        RejectControlChars(credentialUsername);
        var input = BuildRequest(url, includePath: isAzureDevOps, username: credentialUsername).TrimEnd('\n') + $"\npassword={token}\n\n";
        var approveArgs = isAzureDevOps
            ? new[] { "-c", "credential.useHttpPath=true", "credential", "approve" }
            : new[] { "credential", "approve" };
        await GitProcess.RunAsync(null, approveArgs, null, ct, null, input);
        if (isAzureDevOps && !string.IsNullOrWhiteSpace(azureOrganization))
        {
            // PR API authentication requests a host-level credential, while Git operations
            // need the organization path. Store a host-level entry under the organization identity too.
            var hostOnly = BuildRequest(url, username: azureOrganization).TrimEnd('\n') + $"\npassword={token}\n\n";
            await GitProcess.RunAsync(null, approveArgs, null, ct, null, hostOnly);
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

    public static async Task EnableHttpPathAsync(string repositoryPath, string url, CancellationToken ct, string? azureOrganization = null)
    {
        if (!IsAzureDevOpsUrl(url)) return;
        await GitProcess.RunAsync(repositoryPath, new[] { "config", "--local", "credential.useHttpPath", "true" }, null, ct);
        if (!string.IsNullOrWhiteSpace(azureOrganization))
        {
            RejectControlChars(azureOrganization);
            var uri = new Uri(url);
            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            var urlPrefix = $"{uri.Scheme}://{authority}";
            await GitProcess.RunAsync(repositoryPath,
                new[] { "config", "--local", $"credential.{urlPrefix}.username", azureOrganization }, null, ct);
        }
    }

    private static string BuildRequest(string url, bool includePath = false, string? username = null)
    {
        if (!TryParseHttp(url, out var protocol, out var host))
            throw new ArgumentException("Credentials can only be stored for http(s) remotes.");
        RejectControlChars(host);
        var path = includePath ? Uri.UnescapeDataString(new Uri(url).AbsolutePath).TrimStart('/') : "";
        if (!string.IsNullOrWhiteSpace(username)) RejectControlChars(username);
        return $"protocol={protocol}\nhost={host}\n" +
               (path.Length > 0 ? $"path={path}\n" : "") +
               (string.IsNullOrWhiteSpace(username) ? "" : $"username={username}\n") + "\n";
    }

    private static bool TryReadPassword(string credential, out string password)
    {
        password = credential.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..] ?? "";
        return password.Length > 0;
    }

    private static void RejectControlChars(string value)
    {
        if (value.Any(c => c is '\n' or '\r' or '\0'))
            throw new ArgumentException("Credential values must not contain line breaks.");
    }
}
