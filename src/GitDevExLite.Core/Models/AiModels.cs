namespace GitDevExLite.Core.Models;

public sealed record AiFileExcerpt(string Path, string Summary, string Patch);

public sealed record AiGenerateCommitMessageRequest(
    string RepositoryPath,
    IReadOnlyList<AiFileExcerpt> Files,
    bool Amend);

public sealed record AiPreviewPayload(string Title, string Body);

public sealed record AiCommitMessageDraft(
    string Subject,
    string Body,
    AiPreviewPayload Preview,
    string ProviderName)
{
    public string FullMessage => string.IsNullOrWhiteSpace(Body)
        ? Subject
        : $"{Subject}{Environment.NewLine}{Environment.NewLine}{Body}";
}
