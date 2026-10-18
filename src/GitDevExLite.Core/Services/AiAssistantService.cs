using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Models;

namespace GitDevExLite.Core.Services;

public enum AiProviderKind
{
    Disabled,
    OpenAiCompatible,
    GitHubModels,
    AzureFoundry
}

public sealed record AiProviderDescriptor(
    AiProviderKind Kind,
    string Label,
    string DefaultEndpoint = "",
    string DefaultModel = "",
    string ModelLabel = "Model",
    string EndpointHint = "",
    string ModelHint = "",
    string Description = "");

public sealed record AiProviderSettings(
    AiProviderKind Kind,
    string Endpoint,
    string Model,
    string SecretKey = "default")
{
    public static AiProviderSettings Disabled { get; } = new(AiProviderKind.Disabled, string.Empty, string.Empty);

    public bool IsConfigured =>
        Kind != AiProviderKind.Disabled &&
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Model);
}

public interface IAiSettingsStore
{
    AiProviderSettings Load();
    void Save(AiProviderSettings settings);
}

public interface IAiCredentialStore
{
    Task<string?> TryGetApiKeyAsync(AiProviderSettings settings, CancellationToken cancellationToken);
    Task SaveApiKeyAsync(AiProviderSettings settings, string apiKey, CancellationToken cancellationToken);
}

public interface IAiProvider
{
    string Name { get; }
    Task<AiCommitMessageDraft> GenerateCommitMessageAsync(AiGenerateCommitMessageRequest request, CancellationToken cancellationToken);
    Task TestConnectionAsync(CancellationToken cancellationToken);
}

public interface IAiProviderFactory
{
    IReadOnlyList<AiProviderDescriptor> GetAvailableProviders();
    Task<IAiProvider> CreateAsync(AiProviderSettings settings, CancellationToken cancellationToken);
}

public sealed class AiAssistantService
{
    private readonly IAiSettingsStore _settingsStore;
    private readonly IAiProviderFactory _providerFactory;

    public AiAssistantService(IAiSettingsStore settingsStore, IAiProviderFactory providerFactory)
    {
        _settingsStore = settingsStore;
        _providerFactory = providerFactory;
    }

    public IReadOnlyList<AiProviderDescriptor> GetAvailableProviders() => _providerFactory.GetAvailableProviders();

    public AiProviderSettings LoadSettings() => _settingsStore.Load();

    public void SaveSettings(AiProviderSettings settings) => _settingsStore.Save(settings);

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        await TestConnectionAsync(settings, cancellationToken);
    }

    public async Task TestConnectionAsync(AiProviderSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Configure an AI provider before testing the connection.");

        var provider = await _providerFactory.CreateAsync(settings, cancellationToken);
        await provider.TestConnectionAsync(cancellationToken);
    }

    public async Task<AiCommitMessageDraft> GenerateCommitMessageAsync(IGitService git, bool amend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);

        var settings = _settingsStore.Load();
        if (!settings.IsConfigured)
            throw new InvalidOperationException("Configure an AI provider before generating a commit message.");

        var provider = await _providerFactory.CreateAsync(settings, cancellationToken);

        var repositoryPath = git.RepositoryPath ?? throw new InvalidOperationException("Open a repository before generating an AI commit message.");
        var staged = git.GetStatus().Where(change => change.IsStaged).ToList();
        if (staged.Count == 0 && !amend)
            throw new InvalidOperationException("Stage at least one file before generating an AI commit message.");

        var files = new List<AiFileExcerpt>();
        foreach (var change in staged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diff = git.GetWorkingDiff(change.Path, staged: true);
            var patch = BuildPatch(diff);
            var summary = Summarize(diff, change.Status);
            files.Add(new AiFileExcerpt(change.Path, summary, patch));
        }

        if (files.Count == 0 && amend)
            files.Add(new AiFileExcerpt("HEAD", "rewrite commit message", git.GetHeadMessage() ?? string.Empty));

        var request = new AiGenerateCommitMessageRequest(repositoryPath, files, amend);
        return await provider.GenerateCommitMessageAsync(request, cancellationToken);
    }

    private static string Summarize(FileDiff diff, string fallbackStatus)
    {
        var added = 0;
        var removed = 0;
        foreach (var line in diff.Hunks.SelectMany(hunk => hunk.Lines))
        {
            if (line.Kind == DiffLineKind.Added) added++;
            if (line.Kind == DiffLineKind.Removed) removed++;
        }

        if (diff.IsNew) return $"new file, +{added}";
        if (diff.IsDeleted) return $"deleted file, -{removed}";
        if (added > 0 || removed > 0) return $"+{added} / -{removed}";
        return fallbackStatus;
    }

    private static string BuildPatch(FileDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"diff --git a/{diff.Path} b/{diff.Path}");

        foreach (var hunk in diff.Hunks)
        {
            builder.AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@");
            foreach (var line in hunk.Lines)
            {
                var prefix = line.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' '
                };
                builder.Append(prefix).AppendLine(line.Text);
            }
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed class OpenAiCompatibleAiProviderFactory : IAiProviderFactory
{
    private static readonly IReadOnlyList<AiProviderDescriptor> Providers =
    [
        new(AiProviderKind.Disabled, "Disabled", string.Empty, string.Empty, string.Empty, string.Empty, "Turn off AI-assisted commit drafting."),
        new(
            AiProviderKind.OpenAiCompatible,
            "OpenAI-compatible",
            "https://api.example.com/v1",
            "gpt-4.1-mini",
            "Model",
            "https://api.example.com/v1",
            "gpt-4.1-mini",
            "Use any chat-completions endpoint that accepts a bearer API key."),
        new(
            AiProviderKind.GitHubModels,
            "GitHub Models",
            "https://models.inference.ai.azure.com",
            "gpt-4o-mini",
            "Model",
            "https://models.inference.ai.azure.com",
            "gpt-4o-mini",
            "Uses the GitHub Models inference endpoint with a GitHub token."),
        new(
            AiProviderKind.AzureFoundry,
            "Azure Foundry",
            "https://<resource>.services.ai.azure.com/models",
            "gpt-4o-mini",
            "Deployment or model name",
            "https://<resource>.services.ai.azure.com/models",
            "Use your Foundry deployment or model name",
            "Use the Azure AI Foundry model inference endpoint. Endpoint format: https://<resource>.services.ai.azure.com/models . Enter the deployment or model name exactly as configured in Foundry.")
    ];

    private readonly IAiCredentialStore _credentialStore;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleAiProviderFactory(IAiCredentialStore credentialStore, HttpClient? httpClient = null)
    {
        _credentialStore = credentialStore;
        _httpClient = httpClient ?? new HttpClient();
    }

    public IReadOnlyList<AiProviderDescriptor> GetAvailableProviders() => Providers;

    public async Task<IAiProvider> CreateAsync(AiProviderSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Kind == AiProviderKind.Disabled)
            throw new InvalidOperationException("AI provider is disabled.");
        var descriptor = Providers.FirstOrDefault(provider => provider.Kind == settings.Kind);
        if (descriptor is null)
            throw new InvalidOperationException($"Unsupported AI provider '{settings.Kind}'.");

        var apiKey = await _credentialStore.TryGetApiKeyAsync(settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Store an AI API key before generating a commit message.");

        return new OpenAiCompatibleCommitMessageProvider(_httpClient, settings, apiKey);
    }
}

public sealed class GitCredentialAiKeyStore : IAiCredentialStore
{
    private const string Username = "gitdevexlite-ai";

    public async Task<string?> TryGetApiKeyAsync(AiProviderSettings settings, CancellationToken cancellationToken)
    {
        var key = BuildCredentialKey(settings);
        return await ProviderCredentialStore.TryGetTokenAsync(key.Protocol, key.Host, key.Username, cancellationToken);
    }

    public async Task SaveApiKeyAsync(AiProviderSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        var key = BuildCredentialKey(settings);
        await GitCredentials.StoreTokenAsync(key.Protocol, key.Host, key.Username, apiKey, cancellationToken);
    }

    private static (string Protocol, string Host, string Username) BuildCredentialKey(AiProviderSettings settings)
    {
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Enter a valid AI endpoint URL before storing credentials.");

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var suffix = string.IsNullOrWhiteSpace(settings.SecretKey) ? "default" : settings.SecretKey.Trim();
        return (uri.Scheme, $"ai.{host}", $"{Username}:{suffix}");
    }
}

public sealed class OpenAiCompatibleCommitMessageProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly AiProviderSettings _settings;
    private readonly string _apiKey;

    public OpenAiCompatibleCommitMessageProvider(HttpClient httpClient, AiProviderSettings settings, string apiKey)
    {
        _httpClient = httpClient;
        _settings = settings;
        _apiKey = apiKey;
    }

    public string Name => _settings.Model;

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(_settings))
        {
            Content = JsonContent.Create(new
            {
                model = _settings.Model,
                messages = new object[]
                {
                    new { role = "system", content = "Reply with the single word ok." },
                    new { role = "user", content = "Connection test" }
                },
                temperature = 0,
                max_tokens = 8
            })
        };
        ApplyAuthentication(message, _settings, _apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI provider request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {payload}".Trim());

        using var json = JsonDocument.Parse(payload);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("AI provider returned an empty response during connection test.");
    }

    public async Task<AiCommitMessageDraft> GenerateCommitMessageAsync(AiGenerateCommitMessageRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(_settings))
        {
            Content = JsonContent.Create(new
            {
                model = _settings.Model,
                messages = BuildMessages(request),
                temperature = 0.2,
                max_tokens = 220
            })
        };
        ApplyAuthentication(message, _settings, _apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI provider request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {payload}".Trim());

        using var json = JsonDocument.Parse(payload);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("AI provider returned an empty commit message.");

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var subject = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? throw new InvalidOperationException("AI provider returned an empty commit message.");
        var body = string.Join(Environment.NewLine, lines.Skip(1)).Trim();
        return new AiCommitMessageDraft(
            subject,
            body,
            new AiPreviewPayload("Staged diff excerpt sent to AI", BuildPreview(request)),
            Name);
    }

    private static string BuildChatCompletionsUri(AiProviderSettings settings)
    {
        var trimmed = settings.Endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/models/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return settings.Kind switch
        {
            AiProviderKind.AzureFoundry when trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                => $"{trimmed}/chat/completions",
            AiProviderKind.GitHubModels when !trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                => $"{trimmed}/chat/completions",
            _ => $"{trimmed}/chat/completions"
        };
    }

    private static void ApplyAuthentication(HttpRequestMessage message, AiProviderSettings settings, string apiKey)
    {
        if (settings.Kind == AiProviderKind.AzureFoundry)
        {
            message.Headers.Add("api-key", apiKey);
            return;
        }

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static object[] BuildMessages(AiGenerateCommitMessageRequest request) =>
    [
        new
        {
            role = "system",
            content = "Write concise git commit messages. Return plain text only. Use a short imperative subject line, then a blank line, then optional bullet points only when they add value."
        },
        new
        {
            role = "user",
            content = BuildPrompt(request)
        }
    ];

    private static string BuildPrompt(AiGenerateCommitMessageRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Repository: {request.RepositoryPath}");
        builder.AppendLine($"Mode: {(request.Amend ? "amend" : "commit")}");
        builder.AppendLine("Summaries:");
        foreach (var file in request.Files)
            builder.AppendLine($"- {file.Path}: {file.Summary}");
        builder.AppendLine();
        builder.AppendLine("Patch excerpts:");
        foreach (var file in request.Files)
        {
            builder.AppendLine($"# {file.Path}");
            builder.AppendLine(file.Patch);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPreview(AiGenerateCommitMessageRequest request) => BuildPrompt(request);
}
