using System.Diagnostics;

namespace EndpointAnalyzer.Application;

/// <summary>Commit e branch do repositório analisado (quando for um repositório git).</summary>
public static class GitInfo
{
    public static (string? Commit, string? Branch) Read(string directory) =>
        (Run(directory, "rev-parse --short HEAD"), Run(directory, "rev-parse --abbrev-ref HEAD"));

    private static string? Run(string directory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
