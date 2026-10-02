using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.ChangeDetection;

/// <summary>
/// Conjunto de tipos considerados entidades: DbSet&lt;T&gt; dos DbContexts e modelBuilder.Entity&lt;T&gt;().
/// Sem DbContext no código, usa uma heurística (classe do projeto com propriedade Id que não é DTO).
/// </summary>
public sealed class EntityCatalog
{
    private static readonly string[] NonEntitySuffixes =
        ["Dto", "DTO", "Request", "Response", "Command", "Query", "ViewModel", "Model", "Controller", "Service", "Repository", "Validator", "Options", "Settings", "Result"];

    private readonly HashSet<string> _entities = [];

    public bool UsesHeuristic { get; private set; }

    public IReadOnlyCollection<string> Entities => _entities;

    public bool IsEntity(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Class) return false;

        if (UsesHeuristic)
            return named.HasSource()
                && !NonEntitySuffixes.Any(s => named.Name.EndsWith(s, StringComparison.Ordinal))
                && AllMembers(named).Any(m => m is IPropertySymbol { Name: "Id" } || m.Name == named.Name + "Id");

        for (var t = named; t is not null; t = t.BaseType)
            if (_entities.Contains(t.OriginalDefinition.Key())) return true;

        return false;
    }

    /// <summary>T de DbSet&lt;T&gt;, IQueryable&lt;T&gt;, IEnumerable&lt;T&gt; ou T[].</summary>
    public static ITypeSymbol? ElementType(ITypeSymbol? type) => type switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic => generic.TypeArguments[0],
        _ => null,
    };

    public static bool IsDbSet(ITypeSymbol? type) => type is INamedTypeSymbol { Name: "DbSet", IsGenericType: true };

    public static bool IsDbContext(ITypeSymbol? type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == "DbContext") return true;
        return false;
    }

    public static async Task<EntityCatalog> BuildAsync(Solution solution, CancellationToken cancellationToken = default)
    {
        var catalog = new EntityCatalog();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                var model = compilation.GetSemanticModel(tree);

                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(cls, cancellationToken) is not INamedTypeSymbol type || !IsDbContext(type.BaseType))
                        continue;

                    foreach (var property in type.GetMembers().OfType<IPropertySymbol>().Where(p => IsDbSet(p.Type)))
                        catalog.Add(ElementType(property.Type));

                    // modelBuilder.Entity<T>()
                    foreach (var generic in cls.DescendantNodes().OfType<GenericNameSyntax>().Where(g => g.Identifier.Text == "Entity"))
                        if (generic.TypeArgumentList.Arguments.Count == 1)
                            catalog.Add(model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Type);
                }
            }
        }

        catalog.UsesHeuristic = catalog._entities.Count == 0;
        return catalog;
    }

    private void Add(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol named) _entities.Add(named.OriginalDefinition.Key());
    }

    private static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            foreach (var member in t.GetMembers())
                yield return member;
    }
}
