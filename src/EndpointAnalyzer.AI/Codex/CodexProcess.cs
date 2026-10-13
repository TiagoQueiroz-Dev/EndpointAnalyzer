using System.Diagnostics;
using EndpointAnalyzer.AI.ClaudeCode;

namespace EndpointAnalyzer.AI.Codex;

/// <summary>Processos do Codex CLI com o login do projeto (CODEX_HOME) e sem credenciais herdadas do ambiente.</summary>
internal static class CodexProcess
{
    public const string Tool = "Codex";

    // Credenciais que o Codex usaria no lugar do login do projeto, desviando o consumo para a API ou outra conta.
    private static readonly string[] CredentialOverrides = ["OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL"];

    /// <summary>Diretório neutro: não carrega AGENTS.md nem configurações de projeto, e não há arquivos para o agente mexer.</summary>
    public static string WorkingDirectory
    {
        get
        {
            var directory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "codex");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <exception cref="CodexNotInstalledException">Executável não encontrado.</exception>
    public static ProcessStartInfo StartInfo(CodexOptions options, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var info = ProcessRunner.StartInfo(CodexExecutable.Resolve(options.Executable), arguments, workingDirectory ?? WorkingDirectory);
        Directory.CreateDirectory(options.HomeDirectory);
        info.Environment["CODEX_HOME"] = options.HomeDirectory;
        info.Environment["NO_COLOR"] = "1";
        foreach (var name in CredentialOverrides) info.Environment.Remove(name);
        return info;
    }

    public static Process Start(ProcessStartInfo info) =>
        ProcessRunner.Start(info, () => new CodexNotInstalledException(info.FileName));

    public static Task<ProcessResult> RunAsync(ProcessStartInfo info, string? input, TimeSpan timeout, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(info, input, timeout, cancellationToken, Tool, () => new CodexNotInstalledException(info.FileName));
}

/// <summary>Localiza o Codex CLI: PATH, npm, instalador e o CLI que acompanha o app desktop do Codex.</summary>
internal static class CodexExecutable
{
    public static CliCommand Resolve(string executable) =>
        CliExecutable.Resolve(executable, KnownDirectories, searched => new CodexNotInstalledException(executable, searched));

    private static IEnumerable<string> KnownDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return
            [
                Path.Combine(appData, "npm"),
                Path.Combine(home, ".local", "bin"),
                // App desktop: %LOCALAPPDATA%\OpenAI\Codex\bin\<versão>\codex.exe (a mais recente primeiro).
                .. Versions(Path.Combine(localAppData, "OpenAI", "Codex", "bin")),
            ];
        }

        return
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".npm-global", "bin"),
            "/usr/local/bin",
            "/opt/homebrew/bin",
            "/Applications/Codex.app/Contents/Resources",
        ];
    }

    private static IEnumerable<string> Versions(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Select(d => d.FullName).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

public class CodexNotInstalledException(string executable, IEnumerable<string>? searched = null)
    : Exception($"Codex não encontrado ('{executable}'{Searched(searched)}). " +
                "Instale com 'npm install -g @openai/codex' ou o app do Codex (https://developers.openai.com/codex), " +
                "ou informe o caminho completo em Codex:Executable, e tente novamente.")
{
    private static string Searched(IEnumerable<string>? searched) =>
        searched is null ? "" : $"; procurado em: {string.Join(", ", searched)}";
}
