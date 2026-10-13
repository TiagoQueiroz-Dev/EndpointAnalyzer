using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace EndpointAnalyzer.AI;

/// <summary>Como executar um CLI: o programa e os argumentos que vão antes dos do chamador (ex.: node + cli.js).</summary>
internal sealed record CliCommand(string Source, string FileName, IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Localiza um CLI (Claude Code, Codex) independentemente de como foi instalado na máquina: caminho configurado, PATH do
/// processo e o gravado no registro (pega instalações feitas depois de abrir o IDE), pastas conhecidas dos instaladores e npm.
/// Os shims .cmd do npm não passam pelo cmd.exe, que corromperia prompts com quebras de linha e caracteres especiais:
/// o alvo do shim é executado diretamente (o binário nativo do pacote, ou node + o script).
/// </summary>
internal static partial class CliExecutable
{
    private static readonly ConcurrentDictionary<string, CliCommand> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="knownDirectories">Pastas fora do PATH onde os instaladores colocam o CLI.</param>
    /// <param name="notInstalled">Exceção quando não encontrado, com os locais procurados.</param>
    // Só resultados encontrados ficam em cache: se o CLI for instalado com a API rodando, a próxima busca acha.
    public static CliCommand Resolve(string executable, Func<IEnumerable<string>> knownDirectories,
        Func<IEnumerable<string>, Exception> notInstalled)
    {
        if (Cache.TryGetValue(executable, out var cached) && File.Exists(cached.Source) && File.Exists(cached.FileName))
            return cached;

        var command = Find(executable, knownDirectories) ?? throw notInstalled(SearchedLocations(executable, knownDirectories));
        Cache[executable] = command;
        return command;
    }

    private static CliCommand? Find(string executable, Func<IEnumerable<string>> knownDirectories)
    {
        foreach (var candidate in Candidates(executable, knownDirectories))
        {
            if (!File.Exists(candidate)) continue;
            if (FromFile(candidate) is { } command) return command;
        }
        return null;
    }

    private static bool IsPath(string executable) => Path.IsPathRooted(executable) || executable.IndexOfAny(['/', '\\']) >= 0;

    private static IEnumerable<string> Candidates(string executable, Func<IEnumerable<string>> knownDirectories)
    {
        string[] names = Path.HasExtension(executable) ? [executable] : [.. Extensions.Select(e => executable + e)];

        // Caminho informado na configuração: respeita exatamente o que foi configurado.
        if (IsPath(executable)) return names.Select(Path.GetFullPath);

        return PathDirectories().Concat(knownDirectories())
            .Distinct(PathComparer)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)));
    }

    private static CliCommand? FromFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cmd" or ".bat" => FromShim(path),
            _ => new CliCommand(path, path, []),
        };

    /// <summary>Lê o shim gerado pelo npm/pnpm (cmd-shim) e devolve o que ele executaria.</summary>
    internal static CliCommand? FromShim(string shim)
    {
        var directory = Path.GetDirectoryName(shim)!;
        var target = ShimTargetRegex().Matches(File.ReadAllText(shim))
            .Select(m => Path.GetFullPath(Path.Combine(directory, m.Groups["target"].Value.TrimStart('\\', '/'))))
            .FirstOrDefault(p => !Path.GetFileName(p).Equals("node.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p));

        if (target is null) return null;
        if (Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new CliCommand(shim, target, []);

        // Mesma regra do shim: node.exe ao lado dele, senão o node do PATH.
        var node = PathDirectories().Prepend(directory).Select(d => Path.Combine(d, "node.exe")).FirstOrDefault(File.Exists);
        return node is null ? null : new CliCommand(shim, node, [target]);
    }

    private static string[] Extensions => OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>PATH do processo e, no Windows, o atual do usuário e da máquina (o do processo pode estar desatualizado).</summary>
    private static IEnumerable<string> PathDirectories()
    {
        var values = OperatingSystem.IsWindows()
            ? new[]
            {
                Environment.GetEnvironmentVariable("PATH"),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            }
            : [Environment.GetEnvironmentVariable("PATH")];

        return values
            .SelectMany(v => (v ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(d => Environment.ExpandEnvironmentVariables(d.Trim('"')))
            .Where(d => d.Length > 0 && Path.IsPathRooted(d));
    }

    private static IEnumerable<string> SearchedLocations(string executable, Func<IEnumerable<string>> knownDirectories) =>
        IsPath(executable) ? Candidates(executable, knownDirectories) : ["PATH", .. knownDirectories()];

    // "%dp0%\node_modules\...\cli.js" (npm) ou "%~dp0\..\...\cli.js" (pnpm); node.exe é descartado depois.
    [GeneratedRegex(@"""%~?dp0%?(?<target>[^""%]+?\.(?:c?js|mjs|exe))""", RegexOptions.IgnoreCase)]
    private static partial Regex ShimTargetRegex();
}
