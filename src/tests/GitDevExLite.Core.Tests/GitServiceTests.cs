using GitDevExLite.Core.Diff;
using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;
using LibGit2Sharp;

namespace GitDevExLite.Core.Tests;

public sealed class GitServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gdx-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new();

    public GitServiceTests()
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

    private string Numbered(int count, Func<int, string>? line = null) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => line?.Invoke(i) ?? $"line {i}")) + "\n";

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    private string Read(string name) => File.ReadAllText(Path.Combine(_dir, name));

    private void CommitAll(string message)
    {
        _git.Stage("a.txt");
        _git.Commit(message);
    }

    [Fact]
    public void Commit_changes_and_diff_are_reported()
    {
        Write("a.txt", Numbered(5));
        CommitAll("first");
        Write("a.txt", Numbered(5, i => i == 3 ? "changed" : $"line {i}"));
        _git.Stage("a.txt");
        _git.Commit("second");

        var sha = _git.GetCommits()[0].Sha;
        var files = _git.GetCommitChanges(sha);
        Assert.Equal("a.txt", Assert.Single(files).Path);

        var diff = _git.GetCommitDiff(sha, "a.txt");
        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.Lines, l => l.Kind == DiffLineKind.Added && l.Text == "changed");
        Assert.Contains(hunk.Lines, l => l.Kind == DiffLineKind.Removed && l.Text == "line 3");
    }

    [Fact]
    public void Root_commit_diff_is_all_additions()
    {
        Write("a.txt", Numbered(3));
        CommitAll("first");

        var sha = _git.GetCommits()[0].Sha;
        var diff = _git.GetCommitDiff(sha, "a.txt");
        Assert.True(diff.IsNew);
        Assert.All(diff.Hunks.SelectMany(h => h.Lines), l => Assert.Equal(DiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void Status_lists_staged_and_unstaged_sides_separately()
    {
        Write("a.txt", Numbered(5));
        CommitAll("first");
        Write("a.txt", Numbered(5, i => i == 1 ? "x" : $"line {i}"));
        _git.Stage("a.txt");
        Write("a.txt", Numbered(5, i => i is 1 or 5 ? "y" : $"line {i}"));

        var status = _git.GetStatus();
        Assert.Contains(status, s => s.Path == "a.txt" && s.IsStaged);
        Assert.Contains(status, s => s.Path == "a.txt" && !s.IsStaged);
    }

    [Fact]
    public void Stage_hunk_moves_only_that_hunk_to_the_index()
    {
        Write("a.txt", Numbered(30));
        CommitAll("first");
        Write("a.txt", Numbered(30, i => i is 2 or 28 ? "edited" : $"line {i}"));

        var diff = _git.GetWorkingDiff("a.txt", staged: false);
        Assert.Equal(2, diff.Hunks.Count);

        _git.StageLines(diff, PatchBuilder.HunkChanges(diff, 0));

        var staged = _git.GetWorkingDiff("a.txt", staged: true);
        var unstaged = _git.GetWorkingDiff("a.txt", staged: false);
        Assert.Equal("edited", Assert.Single(staged.Hunks).Lines.Single(l => l.Kind == DiffLineKind.Added).Text);
        Assert.Contains(Assert.Single(unstaged.Hunks).Lines, l => l.Kind == DiffLineKind.Added && l.Text == "edited");
        Assert.Equal(Numbered(30, i => i is 2 or 28 ? "edited" : $"line {i}"), Read("a.txt"));
    }

    [Fact]
    public void Stage_single_line_of_a_replacement()
    {
        Write("a.txt", Numbered(10));
        CommitAll("first");
        Write("a.txt", Numbered(10, i => i is 5 or 6 ? $"new {i}" : $"line {i}"));

        var diff = _git.GetWorkingDiff("a.txt", staged: false);
        var hunk = Assert.Single(diff.Hunks);
        var pick = new HashSet<LineRef>();
        for (var j = 0; j < hunk.Lines.Count; j++)
            if (hunk.Lines[j].Text is "line 5" or "new 5" && hunk.Lines[j].Kind != DiffLineKind.Context)
                pick.Add(new LineRef(0, j));

        _git.StageLines(diff, pick);

        var staged = _git.GetWorkingDiff("a.txt", staged: true);
        var changed = staged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != DiffLineKind.Context).ToList();
        Assert.Equal(new[] { "line 5", "new 5" }, changed.Select(l => l.Text));
    }

    [Fact]
    public void Unstage_hunk_restores_index()
    {
        Write("a.txt", Numbered(30));
        CommitAll("first");
        Write("a.txt", Numbered(30, i => i is 2 or 28 ? "edited" : $"line {i}"));
        _git.Stage("a.txt");

        var staged = _git.GetWorkingDiff("a.txt", staged: true);
        Assert.Equal(2, staged.Hunks.Count);
        _git.UnstageLines(staged, PatchBuilder.HunkChanges(staged, 1));

        var after = _git.GetWorkingDiff("a.txt", staged: true);
        Assert.Equal("edited", Assert.Single(after.Hunks).Lines.Single(l => l.Kind == DiffLineKind.Added).Text);
        Assert.Single(_git.GetWorkingDiff("a.txt", staged: false).Hunks);
    }

    [Fact]
    public void Amend_replaces_head_commit()
    {
        Write("a.txt", Numbered(3));
        CommitAll("first");
        Assert.Equal("first", _git.GetHeadMessage());

        _git.Commit("reworded", amend: true);

        Assert.Single(_git.GetCommits());
        Assert.Equal("reworded", _git.GetHeadMessage());
    }

    [Fact]
    public async Task Ai_commit_draft_uses_staged_diff_excerpt()
    {
        Write("a.txt", Numbered(4));
        CommitAll("first");
        Write("a.txt", Numbered(4, i => i == 2 ? "updated line" : $"line {i}"));
        _git.Stage("a.txt");

        var service = CreateAiAssistant(new AiCommitMessageDraft(
            "Update a.txt",
            "- a.txt: +1 / -1",
            new AiPreviewPayload("preview", "diff --git a/a.txt b/a.txt\n-line 2\n+updated line"),
            "test"));

        var draft = await service.GenerateCommitMessageAsync(_git, amend: false, CancellationToken.None);

        Assert.Equal("Update a.txt", draft.Subject);
        Assert.Contains("a.txt: +1 / -1", draft.Body);
        Assert.Contains("diff --git a/a.txt b/a.txt", draft.Preview.Body);
        Assert.Contains("+updated line", draft.Preview.Body);
        Assert.Contains("-line 2", draft.Preview.Body);
    }

    [Fact]
    public async Task Ai_commit_draft_requires_staged_changes_for_non_amend()
    {
        var service = CreateAiAssistant(new AiCommitMessageDraft(
            "unused",
            string.Empty,
            new AiPreviewPayload("preview", string.Empty),
            "test"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateCommitMessageAsync(_git, amend: false, CancellationToken.None));

        Assert.Equal("Stage at least one file before generating an AI commit message.", error.Message);
    }

    [Fact]
    public void Branch_create_rename_delete_and_checkout()
    {
        Write("a.txt", Numbered(3));
        CommitAll("first");
        var sha = _git.GetCommits()[0].Sha;

        _git.CreateBranch("feature", sha, checkout: false);
        _git.RenameBranch("feature", "topic");
        Assert.Contains(_git.GetRefs(), r => r.Name == "topic" && r.Kind == RefKind.LocalBranch);

        _git.Checkout("topic");
        Assert.Throws<InvalidOperationException>(() => _git.DeleteBranch("topic"));

        _git.CreateBranch("other", sha, checkout: true);
        _git.DeleteBranch("topic");
        Assert.DoesNotContain(_git.GetRefs(), r => r.Name == "topic");

        _git.CheckoutCommit(sha);
        Assert.DoesNotContain(_git.GetRefs(), r => r.IsHead);
    }

    private static AiAssistantService CreateAiAssistant(AiCommitMessageDraft draft)
    {
        var settings = new InMemoryAiSettingsStore(new AiProviderSettings(
            AiProviderKind.OpenAiCompatible,
            "https://api.example.test/v1",
            "gpt-test"));
        return new AiAssistantService(settings, new StubAiProviderFactory(draft));
    }

    private sealed class InMemoryAiSettingsStore : IAiSettingsStore
    {
        private AiProviderSettings _settings;

        public InMemoryAiSettingsStore(AiProviderSettings settings)
        {
            _settings = settings;
        }

        public AiProviderSettings Load() => _settings;

        public void Save(AiProviderSettings settings) => _settings = settings;
    }

    private sealed class StubAiProviderFactory : IAiProviderFactory
    {
        private readonly AiCommitMessageDraft _draft;

        public StubAiProviderFactory(AiCommitMessageDraft draft)
        {
            _draft = draft;
        }

        public IReadOnlyList<AiProviderDescriptor> GetAvailableProviders() =>
        [
            new(AiProviderKind.OpenAiCompatible, "OpenAI-compatible")
        ];

        public Task<IAiProvider> CreateAsync(AiProviderSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult<IAiProvider>(new StubAiProvider(_draft));
    }

    private sealed class StubAiProvider : IAiProvider
    {
        private readonly AiCommitMessageDraft _draft;

        public StubAiProvider(AiCommitMessageDraft draft)
        {
            _draft = draft;
        }

        public string Name => _draft.ProviderName;

        public Task TestConnectionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<AiCommitMessageDraft> GenerateCommitMessageAsync(AiGenerateCommitMessageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_draft);
    }
}
