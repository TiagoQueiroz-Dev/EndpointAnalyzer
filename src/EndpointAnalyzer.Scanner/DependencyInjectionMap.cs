using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Registros de DI encontrados no código: services.AddScoped&lt;IProgramacaoService, ProgramacaoService&gt;(),
/// AddScoped(typeof(IRepository&lt;&gt;), typeof(Repository&lt;&gt;)), AddScoped&lt;Service&gt;().
/// Só considera os projetos do host do endpoint (o projeto do controller e o que ele referencia).
/// </summary>
public sealed class DependencyInjectionMap
{
    private static readonly string[] RegistrationMethods =
        ["AddScoped", "AddTransient", "AddSingleton", "TryAddScoped", "TryAddTransient", "TryAddSingleton", "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton"];

    private readonly Dictionary<string, List<INamedTypeSymbol>> _registrations = [];

    public int Count => _registrations.Count;

    /// <summary>
    /// Implementações registradas para o serviço. Para genéricos abertos (IRepository&lt;&gt; → Repository&lt;&gt;)
    /// a implementação é construída com os mesmos argumentos do serviço.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> ImplementationsOf(INamedTypeSymbol service)
    {
        if (_registrations.TryGetValue(ExactKey(service), out var exact))
            return exact;

        if (service.IsGenericType && _registrations.TryGetValue(service.OriginalDefinition.Key(), out var open))
        {
            return open
                .Select(impl => impl.IsGenericType && impl.TypeParameters.Length == service.TypeArguments.Length
                    ? TryConstruct(impl.OriginalDefinition, service.TypeArguments)
                    : impl)
                .OfType<INamedTypeSymbol>()
                .ToList();
        }

        return [];
    }

    public static async Task<DependencyInjectionMap> BuildAsync(Solution solution, Project? host = null, CancellationToken cancellationToken = default)
    {
        var map = new DependencyInjectionMap();

        foreach (var project in ProjectsOf(solution, host))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                SemanticModel? model = null;

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax { Name: var name }) continue;
                    if (!RegistrationMethods.Contains(name.Identifier.Text)) continue;

                    model ??= compilation.GetSemanticModel(tree);
                    var (service, implementation) = ReadRegistration(invocation, name, model);
                    if (service is not null && implementation is not null)
                        map.Add(service, implementation);
                }
            }
        }

        return map;
    }

    /// <summary>O projeto do host e todos os projetos que ele referencia (direta ou indiretamente).</summary>
    private static IEnumerable<Project> ProjectsOf(Solution solution, Project? host)
    {
        if (host is null) return solution.Projects;

        var graph = solution.GetProjectDependencyGraph();
        var ids = graph.GetProjectsThatThisProjectTransitivelyDependsOn(host.Id).Append(host.Id).ToHashSet();
        return solution.Projects.Where(p => ids.Contains(p.Id));
    }

    private static (INamedTypeSymbol? Service, INamedTypeSymbol? Implementation) ReadRegistration(
        InvocationExpressionSyntax invocation, SimpleNameSyntax name, SemanticModel model)
    {
        var args = invocation.ArgumentList.Arguments;

        switch (name)
        {
            // AddScoped<IService, Service>()
            case GenericNameSyntax { TypeArgumentList.Arguments: { Count: 2 } typeArgs }:
                return (TypeOf(typeArgs[0], model), TypeOf(typeArgs[1], model));

            // AddScoped<IService>(sp => new Service(...)) ou AddScoped<Service>()
            case GenericNameSyntax { TypeArgumentList.Arguments: { Count: 1 } single }:
                var service = TypeOf(single[0], model);
                var creation = args.SelectMany(a => a.DescendantNodes()).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
                if (creation is not null) return (service, TypeOf(creation.Type, model));
                return args.Count == 0 && service is { TypeKind: TypeKind.Class, IsAbstract: false } ? (service, service) : (service, null);
        }

        // AddScoped(typeof(IService), typeof(Service)) / AddScoped(typeof(IRepository<>), typeof(Repository<>))
        var typeOfs = args.Select(a => a.Expression).OfType<TypeOfExpressionSyntax>().ToList();
        if (typeOfs.Count == 2)
            return (TypeOf(typeOfs[0].Type, model), TypeOf(typeOfs[1].Type, model));

        return (null, null);
    }

    private static INamedTypeSymbol? TypeOf(TypeSyntax syntax, SemanticModel model) =>
        model.GetSymbolInfo(syntax).Symbol as INamedTypeSymbol ?? model.GetTypeInfo(syntax).Type as INamedTypeSymbol;

    private void Add(INamedTypeSymbol service, INamedTypeSymbol implementation)
    {
        var key = service.IsUnboundGenericType ? service.OriginalDefinition.Key() : ExactKey(service);
        if (!_registrations.TryGetValue(key, out var list))
            _registrations[key] = list = [];

        var impl = implementation.IsUnboundGenericType ? implementation.OriginalDefinition : implementation;
        if (!list.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, impl.OriginalDefinition)))
            list.Add(impl);
    }

    /// <summary>Chave do tipo construído (IRepository&lt;int, Veiculo&gt;) ou da definição quando não é genérico.</summary>
    private static string ExactKey(INamedTypeSymbol type) =>
        type.IsGenericType && !type.IsUnboundGenericType && !SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition)
            ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : type.OriginalDefinition.Key();

    private static INamedTypeSymbol? TryConstruct(INamedTypeSymbol definition, IEnumerable<ITypeSymbol> arguments)
    {
        try
        {
            return definition.Construct(arguments.ToArray());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
