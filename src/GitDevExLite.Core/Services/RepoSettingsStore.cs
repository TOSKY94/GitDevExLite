using System.Text.Json;

namespace GitDevExLite.Core.Services;

public sealed class RepoSettings
{
    public const int MaxRecent = 20;

    public List<string> RecentRepos { get; set; } = new();
    public List<string> OpenTabs { get; set; } = new();
    public string? ActiveTab { get; set; }
    public bool AutoFetch { get; set; } = true;
    public int AutoFetchMinutes { get; set; } = 5;
    public bool PruneOnFetch { get; set; } = true;
    public AiProviderKind AiProvider { get; set; } = AiProviderKind.Disabled;
    public string AiEndpoint { get; set; } = string.Empty;
    public string AiModel { get; set; } = string.Empty;
    public string AiSecretKey { get; set; } = "default";

    public void AddRecent(string path)
    {
        RecentRepos.RemoveAll(p => PathEquals(p, path));
        RecentRepos.Insert(0, path);
        if (RecentRepos.Count > MaxRecent) RecentRepos.RemoveRange(MaxRecent, RecentRepos.Count - MaxRecent);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

public sealed class RepoSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public RepoSettingsStore(string? file = null) =>
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitDevExLite", "settings.json");

    public RepoSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new RepoSettings();
            return JsonSerializer.Deserialize<RepoSettings>(File.ReadAllText(_file)) ?? new RepoSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new RepoSettings();
        }
    }

    public void Save(RepoSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting UI state is best-effort.
        }
    }

    public AiProviderSettings LoadAiSettings()
    {
        var settings = Load();
        return new AiProviderSettings(settings.AiProvider, settings.AiEndpoint, settings.AiModel, settings.AiSecretKey);
    }

    public void SaveAiSettings(AiProviderSettings aiSettings)
    {
        var settings = Load();
        settings.AiProvider = aiSettings.Kind;
        settings.AiEndpoint = aiSettings.Endpoint;
        settings.AiModel = aiSettings.Model;
        settings.AiSecretKey = string.IsNullOrWhiteSpace(aiSettings.SecretKey) ? "default" : aiSettings.SecretKey.Trim();
        Save(settings);
    }
}

public sealed class RepoAiSettingsStore : IAiSettingsStore
{
    private readonly RepoSettingsStore _store;

    public RepoAiSettingsStore(RepoSettingsStore store)
    {
        _store = store;
    }

    public AiProviderSettings Load() => _store.LoadAiSettings();

    public void Save(AiProviderSettings settings) => _store.SaveAiSettings(settings);
}
