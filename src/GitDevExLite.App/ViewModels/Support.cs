using GitDevExLite.Core.Models;

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

public interface IDialogService
{
    Task<string?> PromptAsync(string title, string label, string initial = "");
    Task<bool> ConfirmAsync(string title, string message);
    Task CopyTextAsync(string text);
}
