using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Solução carregada pelo Roslyn, com o diretório usado para gerar caminhos relativos.
/// </summary>
public sealed class LoadedSolution
{
    public required Solution Solution { get; init; }

    public required string Path { get; init; }

    public required string RootDirectory { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    public string RelativePath(string? file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        return System.IO.Path.GetRelativePath(RootDirectory, file).Replace('\\', '/');
    }
}

public interface ISolutionLoader
{
    Task<LoadedSolution> LoadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abre uma .sln, .slnx ou .csproj usando MSBuildWorkspace.
/// </summary>
public class SolutionLoader : ISolutionLoader
{
    public async Task<LoadedSolution> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Arquivo não encontrado: {fullPath}", fullPath);

        var diagnostics = new List<string>();
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e => diagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}"));

        var extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
        Solution solution = extension switch
        {
            ".sln" or ".slnx" => await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken),
            ".csproj" => (await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken)).Solution,
            _ => throw new ArgumentException("Informe um arquivo .sln, .slnx ou .csproj.", nameof(path)),
        };

        return new LoadedSolution
        {
            Solution = solution,
            Path = fullPath,
            RootDirectory = System.IO.Path.GetDirectoryName(fullPath)!,
            Diagnostics = diagnostics,
        };
    }
}
