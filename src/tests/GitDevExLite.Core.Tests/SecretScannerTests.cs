using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;
using Xunit;

namespace GitDevExLite.Core.Tests;

public sealed class SecretScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();
    private readonly SecretScanner _scanner = new();

    public SecretScannerTests()
    {
        Directory.CreateDirectory(_dir);
        Repository.Init(_dir);
        using var repo = new Repository(_dir);
        repo.Config.Set("user.name", "Test");
        repo.Config.Set("user.email", "test@example.com");
        _git.Open(_dir);
    }

    public void Dispose()
    {
        _git.Dispose();
        foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Scan_Finds_Secrets_In_WorkingTree_And_Staged_Content()
    {
        Write("appsettings.json", "{\n  \"password\": \"super-secret-value\"\n}\n");
        _git.Stage("appsettings.json");
        _git.Commit("baseline");

        Write("appsettings.json", "{\n  \"password\": \"working-secret-value\"\n}\n");
        Write("notes.txt", "token = ghp_123456789012345678901234567890123456\n");
        _git.Stage("notes.txt");

        var matches = _scanner.Scan(_git, new[]
        {
            new FileChange("appsettings.json", false, "Modified"),
            new FileChange("notes.txt", true, "Added")
        });

        Assert.Contains(matches, m => m.Path == "appsettings.json" && !m.IsStaged && m.Rule == "Password assignment");
        Assert.Contains(matches, m => m.Path == "notes.txt" && m.IsStaged && m.Rule == "GitHub token");
    }

    [Fact]
    public void Scan_Ignores_Files_Without_Known_Patterns()
    {
        Write("README.md", "hello world\n");

        var matches = _scanner.Scan(_git, new[] { new FileChange("README.md", false, "Modified") });

        Assert.Empty(matches);
    }

    [Fact]
    public void GetFileContent_Returns_Indexed_Text_For_Staged_Files()
    {
        Write("config.txt", "password = \"first-secret\"\n");
        _git.Stage("config.txt");
        _git.Commit("baseline");

        Write("config.txt", "password = \"working-copy\"\n");
        _git.Stage("config.txt");
        Write("config.txt", "password = \"not-staged\"\n");

        var staged = _git.GetFileContent("config.txt", staged: true);
        var working = _git.GetFileContent("config.txt", staged: false);

        Assert.Contains("working-copy", staged);
        Assert.Contains("not-staged", working);
    }

    private void Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}