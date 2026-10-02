using EndpointAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EndpointAnalyzer.Scanner;

public interface IMethodResolver
{
    Task<IMethodSymbol?> ResolveAsync(LoadedSolution solution, EndpointInfo endpoint, CancellationToken cancellationToken = default);
}

/// <summary>
/// Endpoint → IMethodSymbol (ex.: ProgramacaoController.Criar).
/// </summary>
public class MethodResolver : IMethodResolver
{
    public async Task<IMethodSymbol?> ResolveAsync(LoadedSolution loaded, EndpointInfo endpoint, CancellationToken cancellationToken = default)
    {
        // Procura primeiro no projeto onde o endpoint foi encontrado.
        var projects = loaded.Solution.Projects
            .OrderByDescending(p => p.Name == endpoint.Project)
            .Where(p => p.Language == LanguageNames.CSharp);

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            if (!string.IsNullOrEmpty(endpoint.MethodId)
                && DocumentationCommentId.GetFirstSymbolForDeclarationId(endpoint.MethodId, compilation) is IMethodSymbol byId
                && byId.HasSource())
                return byId;

            var byName = compilation.GetSymbolsWithName(endpoint.Action, SymbolFilter.Member, cancellationToken)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.ContainingType.Name == endpoint.Controller);
            if (byName is not null) return byName;
        }

        return null;
    }
}
