namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>Localiza o Claude Code (instalador nativo, npm ou PATH): ver <see cref="CliExecutable"/>.</summary>
internal static class ClaudeExecutable
{
    public static CliCommand Resolve(string executable) =>
        CliExecutable.Resolve(executable, KnownDirectories, searched => new ClaudeCodeNotInstalledException(executable, searched));

    /// <summary>Onde os instaladores colocam o Claude Code, mesmo que a pasta não esteja no PATH.</summary>
    private static IEnumerable<string> KnownDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return
            [
                Path.Combine(home, ".local", "bin"),
                Path.Combine(appData, "npm"),
                Path.Combine(home, ".claude", "local"),
            ];
        }

        return
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".npm-global", "bin"),
            "/usr/local/bin",
            "/opt/homebrew/bin",
        ];
    }
}
