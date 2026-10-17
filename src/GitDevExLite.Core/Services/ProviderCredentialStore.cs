namespace GitDevExLite.Core.Services;

public static class ProviderCredentialStore
{
    public static Func<string, string, string?, CancellationToken, Task<string?>> TokenResolver { get; set; } = TryGetTokenCoreAsync;

    public static async Task<string?> TryGetTokenAsync(string protocol, string host, CancellationToken ct)
        => await TokenResolver(protocol, host, null, ct);

    public static async Task<string?> TryGetTokenAsync(string protocol, string host, string? username, CancellationToken ct)
        => await TokenResolver(protocol, host, username, ct);

    private static async Task<string?> TryGetTokenCoreAsync(string protocol, string host, string? username, CancellationToken ct)
    {
        var input = string.IsNullOrWhiteSpace(username)
            ? $"protocol={protocol}\nhost={host}\n\n"
            : $"protocol={protocol}\nhost={host}\nusername={username}\n\n";
        var output = await GitProcess.RunAsync(null, new[] { "credential", "fill" }, null, ct, null, input);
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..];
        }

        return null;
    }
}