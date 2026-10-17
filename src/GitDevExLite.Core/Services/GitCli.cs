using System.Diagnostics;
using System.Text;

namespace GitDevExLite.Core.Services;

internal static class GitCli
{
    /// <summary>Applies a patch to the index via <c>git apply --cached</c>.</summary>
    public static void ApplyToIndex(string workingDirectory, string patch, bool reverse)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("apply");
        psi.ArgumentList.Add("--cached");
        if (reverse) psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add("--recount");
        psi.ArgumentList.Add("--whitespace=nowarn");
        psi.ArgumentList.Add("-");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Partial staging requires 'git' on PATH.", ex);
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEndAsync();
            process.StandardInput.Write(patch);
            process.StandardInput.Close();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git apply failed: {stderr.GetAwaiter().GetResult().Trim()}");
        }
    }
}
