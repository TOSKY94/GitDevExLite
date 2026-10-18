using GitDevExLite.Core.Models;
using GitDevExLite.Core.Services;

namespace GitDevExLite.App.ViewModels;

/// <summary>Set of diff lines the user ticked for partial staging; mutated by the diff view.</summary>
public sealed class LineSelection
{
    public HashSet<LineRef> Lines { get; } = new();

    public event EventHandler? Changed;

    public void Toggle(LineRef line)
    {
        if (!Lines.Add(line)) Lines.Remove(line);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (Lines.Count == 0) return;
        Lines.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ContextAction(string Header, Func<Task> Execute);

/// <summary>Why credentials are needed. <see cref="Url"/> is the remote the failed operation talked to.</summary>
public sealed record CredentialRequest(string? Url, string Message, bool IsSsh, bool GcmAvailable);

/// <summary>UseGcm triggers an interactive Git Credential Manager sign-in; otherwise Username/Token are stored.</summary>
public sealed record CredentialChoice(bool UseGcm, string? Username, string? Token, string? Organization = null);

public sealed record PullRequestTemplateOption(string Name, string Content, bool IsDefault = false);

public sealed record PullRequestCreationFormResult(string SourceBranch, string TargetBranch, string Title, string Description, bool IsDraft);

public delegate Task CloneRunner(string url, string destination, IProgress<GitProgress> progress, CancellationToken ct);

public interface IDialogService
{
    Task<string?> PromptAsync(string title, string label, string initial = "");
    Task<bool> ConfirmAsync(string title, string message);
    Task<PullRequestMergeStrategy?> ChoosePullRequestMergeStrategyAsync(IReadOnlyList<PullRequestMergeStrategy> supportedStrategies);
    Task<PullRequestCreationFormResult?> ShowPullRequestCreationAsync(
        HostedRepositoryId repository,
        IReadOnlyList<string> sourceBranches,
        IReadOnlyList<string> targetBranches,
        CreatePullRequestDraft draft,
        IReadOnlyList<PullRequestTemplateOption> templates);
    Task CopyTextAsync(string text);
    Task<CredentialChoice?> PromptCredentialsAsync(CredentialRequest request);

    /// <summary>Shows the clone dialog and runs the clone inside it; returns the cloned folder, or null if cancelled.</summary>
    Task<string?> ShowCloneDialogAsync(CloneRunner clone, string defaultParentFolder);

    /// <summary>Lets the user pick, reorder, squash, reword and drop commits; null if cancelled.</summary>
    Task<RebaseRequest?> ShowInteractiveRebaseAsync(IReadOnlyList<RebaseEntry> entries, string description);

    Task ShowFileHistoryAsync(FileHistoryViewModel history, bool startOnBlame);
    Task ShowRepoToolsAsync(RepoToolsViewModel tools);
}

/// <summary>Observable options shared by the shell and every tab.</summary>
public sealed partial class RemoteOptions : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _autoFetch = true;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _pruneOnFetch = true;
    public int AutoFetchMinutes { get; set; } = 5;
}

public static class AuthRetry
{
    /// <summary>Runs <paramref name="operation"/>; on an auth failure asks for credentials once and retries.</summary>
    public static async Task RunAsync(IDialogService dialogs, Func<string?> remoteUrl, Func<Task> operation, CancellationToken ct, string? repositoryPath = null)
    {
        try
        {
            await operation();
        }
        catch (GitAuthException ex)
        {
            var url = remoteUrl();
            var request = new CredentialRequest(
                url, ex.Message, GitCredentials.IsSshUrl(url), await GitCredentials.IsGcmAvailableAsync());
            var choice = await dialogs.PromptCredentialsAsync(request);
            if (choice is null || request.IsSsh || url is null) throw;

            var azureOrganization = string.IsNullOrWhiteSpace(choice.Organization)
                ? GitCredentials.GetAzureDevOpsOrganization(url)
                : choice.Organization.Trim();
            if (GitCredentials.IsAzureDevOpsUrl(url) &&
                GitCredentials.RequiresAzureDevOpsOrganization(ex.Message) &&
                string.IsNullOrWhiteSpace(azureOrganization))
                throw new InvalidOperationException("Enter the Azure DevOps organization name to continue.");

            if (repositoryPath is not null)
                await GitCredentials.EnableHttpPathAsync(repositoryPath, url, ct, azureOrganization);
            if (choice.UseGcm) await GitCredentials.SignInAsync(url, ct, azureOrganization);
            else await GitCredentials.StoreTokenAsync(url, choice.Username ?? "", choice.Token ?? "", ct, azureOrganization);
            await operation();
        }
    }
}
