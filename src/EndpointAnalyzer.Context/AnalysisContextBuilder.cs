using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.ChangeDetection.Relevance;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Context;

public interface IAnalysisContextBuilder
{
    EndpointAnalysisContext Build(CallGraph graph, IReadOnlyList<ConditionInfo> conditions, EntityChangeAnalysis changes);
}

/// <summary>
/// Monta o contexto mínimo enviado para a IA a partir do grafo de negócio (sem ruído):
/// métodos relevantes, condições (registro C1, C2...), efeitos, alterações, DTOs/enums/entidades e evidências.
/// O grafo técnico completo continua no contexto para debug e auditoria.
/// </summary>
public class AnalysisContextBuilder(AnalyzerOptions options, IRelevanceAnalyzer relevance) : IAnalysisContextBuilder
{
    private const int MaxTypeSnippetChars = 4000;

    public EndpointAnalysisContext Build(CallGraph graph, IReadOnlyList<ConditionInfo> conditions, EntityChangeAnalysis changes)
    {
        var result = relevance.Analyze(graph, conditions, changes);

        var references = result.Changes.Changes
            .SelectMany(c => c.PropertyChanges.Select(p => p.Source).Append(c.Source).Append(c.CreationSource))
            .Concat(result.Changes.PersistencePoints)
            .Concat(result.Effects.Select(e => e.Source));
        foreach (var reference in references)
            if (reference?.Code is not null) reference.Code = SecretSanitizer.Sanitize(reference.Code);

        // Evidência para a IA: métodos do grafo de negócio e os de persistência que alteram entidades.
        var evidence = graph.Methods
            .Where(m => result.BusinessMethods.Contains(m) || (result.SliceMethods.Contains(m) && result.Changes.Raw.Any(r => r.Method == m)))
            .ToList();

        return new EndpointAnalysisContext
        {
            Endpoint = graph.Endpoint,
            CallGraph = result.BusinessGraph.Flatten().Select(n => n.FullName).Distinct().ToList(),
            BusinessGraph = result.BusinessGraph,
            CallTree = graph.Root,
            ConditionRegistry = result.ConditionRegistry,
            Conditions = result.Conditions.Select(Sanitize).ToList(),
            EntityChanges = result.Changes.Changes,
            Effects = result.Effects,
            PersistencePoints = result.Changes.PersistencePoints
                .Where(p => graph.Methods.Any(m => result.SliceMethods.Contains(m) && m.DisplayName == p.Method))
                .ToList(),
            Methods = evidence.Select(m => new CodeSnippet
            {
                Name = m.DisplayName,
                File = m.SourceFile,
                Line = m.Declaration.StartLine(),
                Code = Truncate(SecretSanitizer.Sanitize(m.Declaration.ToString()), options.MaxSnippetChars),
            }).ToList(),
            Types = RelevantTypes(graph, evidence, result.Changes),
            Stats = result.Stats,
        };
    }

    /// <summary>DTOs recebidos pelo endpoint, enums usados no fluxo relevante e entidades alteradas.</summary>
    private static List<CodeSnippet> RelevantTypes(CallGraph graph, IReadOnlyList<AnalyzedMethod> methods, EntityChangeAnalysis changes)
    {
        var types = new Dictionary<string, INamedTypeSymbol>();

        void Add(ITypeSymbol? type)
        {
            if (type is INamedTypeSymbol named && named.HasSource())
                types.TryAdd(named.OriginalDefinition.Key(), named.OriginalDefinition);
        }

        foreach (var parameter in graph.EntryPoint.Symbol.Parameters)
            Add(parameter.Type);

        foreach (var method in methods)
        {
            foreach (var access in method.Declaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (method.IsReachable(access) && method.Model.GetSymbolInfo(access).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } field)
                    Add(field.ContainingType);
            }
        }

        var changedEntities = changes.Changes.Where(c => c.EffectClass == EffectClasses.Primary).Select(c => c.Entity).ToHashSet();
        foreach (var method in graph.Methods)
        {
            foreach (var typeSyntax in method.Declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (changedEntities.Contains(typeSyntax.Identifier.Text) && method.Model.GetSymbolInfo(typeSyntax).Symbol is INamedTypeSymbol entity)
                    Add(entity);
            }
            if (method.ResolveType(method.Symbol.ContainingType) is INamedTypeSymbol owner && changedEntities.Contains(owner.Name))
                Add(owner);
            foreach (var argument in method.Context.Generics.Entries.Values)
                if (changedEntities.Contains(argument.Name)) Add(argument);
        }

        return types.Values
            .Select(type =>
            {
                var declaration = type.DeclaringSyntaxReferences.First().GetSyntax();
                return new CodeSnippet
                {
                    Name = type.Name,
                    File = graph.Solution.RelativePath(declaration.SyntaxTree.FilePath),
                    Line = declaration.StartLine(),
                    Code = Truncate(SecretSanitizer.Sanitize(declaration.ToString()), MaxTypeSnippetChars),
                };
            })
            .ToList();
    }

    private static ConditionInfo Sanitize(ConditionInfo condition)
    {
        condition.Code = condition.Code is null ? null : SecretSanitizer.Sanitize(condition.Code);
        return condition;
    }

    private static string Truncate(string code, int max) =>
        code.Length <= max ? code : code[..max] + "\n// ... (trecho truncado)";
}
