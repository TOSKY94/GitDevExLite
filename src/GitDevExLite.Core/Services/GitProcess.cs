using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitDevExLite.Core.Services;

/// <summary>Exit code and captured output of a finished git process.</summary>
internal sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => string.Join("\n", new[] { StdOut.Trim(), StdErr.Trim() }.Where(s => s.Length > 0));
}

/// <summary>Runs the git CLI asynchronously with progress reporting and cancellation.</summary>
internal static class GitProcess
{
    public static async Task<string> RunAsync(
        string? workingDirectory,
        IEnumerable<string> args,
        IProgress<GitProgress>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null)
    {
        var result = await ExecuteAsync(workingDirectory, args, progress, ct, env, stdin);
        if (result.ExitCode == 0) return result.StdOut;

        var message = string.IsNullOrWhiteSpace(result.StdErr)
            ? $"git exited with code {result.ExitCode}."
            : result.StdErr;
        throw GitAuthFailure.IsAuthFailure(result.StdErr)
            ? new GitAuthException(message, result.ExitCode)
            : new GitCommandException(message, result.ExitCode);
    }

    /// <summary>Like <see cref="RunAsync"/> but returns non-zero exits instead of throwing, for commands whose exit code carries meaning.</summary>
    public static async Task<GitResult> ExecuteAsync(
        string? workingDirectory,
        IEnumerable<string> args,
        IProgress<GitProgress>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never block on a console prompt; the app surfaces auth failures itself.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_EDITOR"] = "true";
        psi.Environment["LC_ALL"] = "C";
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("This operation requires 'git' on PATH.", ex);
        }

        using (process)
        using (ct.Register(() => TryKill(process)))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = ReadStderrAsync(process.StandardError, progress);

            if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                try { await Task.WhenAll(stdout, stderr); } catch { /* pipes closed by kill */ }
                throw;
            }

            var output = await stdout;
            var errors = await stderr;

            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return new GitResult(process.ExitCode, output, errors);
        }
    }

    private static async Task<string> ReadStderrAsync(StreamReader reader, IProgress<GitProgress>? progress)
    {
        var errors = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[1];

        void Flush()
        {
            if (line.Length == 0) return;
            var text = line.ToString();
            line.Clear();
            if (GitProgressParser.TryParse(text, out var p)) progress?.Report(p);
            else errors.AppendLine(text);
        }

        // git redraws progress in place with '\r', so both '\r' and '\n' end a line.
        while (await reader.ReadAsync(buffer, 0, 1) > 0)
        {
            if (buffer[0] is '\r' or '\n') Flush();
            else line.Append(buffer[0]);
        }
        Flush();
        return GitAuthFailure.Normalize(errors);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
