using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>Handler de uma mensagem: AdicionarPedidoCommandHandler.Handle(AdicionarPedidoCommand, CancellationToken).</summary>
public sealed record MessageHandler(INamedTypeSymbol Type, IMethodSymbol Method, INamedTypeSymbol Interface);

/// <summary>
/// Handlers de comandos, requests e eventos encontrados no código: classes que implementam
/// IRequestHandler&lt;T&gt;, IRequestHandler&lt;T, R&gt;, INotificationHandler&lt;T&gt; (MediatR), ICommandHandler&lt;T&gt;,
/// IEventHandler&lt;T&gt;, IConsumer&lt;T&gt; (MassTransit), IHandleMessages&lt;T&gt; (NServiceBus/Rebus) e afins.
/// Usado para seguir o fluxo de mediator.Send(command) / bus.SendCommand(command) até o handler.
/// </summary>
public sealed class MessageHandlerMap
{
    private static readonly string[] HandlerInterfaces =
        ["IRequestHandler", "INotificationHandler", "ICommandHandler", "IQueryHandler", "IEventHandler", "IIntegrationEventHandler",
         "IDomainEventHandler", "IMessageHandler", "IHandler", "IHandle", "IHandleMessages", "IConsumer"];

    private static readonly string[] HandleMethods = ["Handle", "HandleAsync", "Consume", "ConsumeAsync", "Executar", "Execute", "ExecuteAsync"];

    private readonly Dictionary<string, List<(MessageHandler Handler, bool InHost)>> _handlers = [];

    public int Count => _handlers.Count;

    /// <summary>Handlers da mensagem; se houver handlers nos projetos do host, só esses.</summary>
    public IReadOnlyList<MessageHandler> HandlersOf(ITypeSymbol message)
    {
        if (message is not INamedTypeSymbol || !_handlers.TryGetValue(KeyOf(message), out var list)) return [];
        var host = list.Where(h => h.InHost).Select(h => h.Handler).ToList();
        return host.Count > 0 ? host : list.Select(h => h.Handler).ToList();
    }

    public static async Task<MessageHandlerMap> BuildAsync(Solution solution, Project? host = null, CancellationToken cancellationToken = default)
    {
        var map = new MessageHandlerMap();
        var hostProjects = HostProjects(solution, host);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;
            var inHost = hostProjects.Contains(project.Id);

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                SemanticModel? model = null;

                foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (declaration is InterfaceDeclarationSyntax || declaration.BaseList is null) continue;

                    model ??= compilation.GetSemanticModel(tree);
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type) continue;
                    if (type.IsAbstract || type.IsGenericType || type.TypeKind != TypeKind.Class) continue;

                    foreach (var handler in HandlersDeclaredBy(type))
                        map.Add(handler.Message, handler.Handler, inHost);
                }
            }
        }

        return map;
    }

    private static IEnumerable<(ITypeSymbol Message, MessageHandler Handler)> HandlersDeclaredBy(INamedTypeSymbol type)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (!contract.IsGenericType || !IsHandlerInterface(contract.Name)) continue;
            if (contract.TypeArguments[0] is not INamedTypeSymbol message || message.TypeKind == TypeKind.TypeParameter) continue;

            var method = HandleMethodOf(type, contract, message);
            if (method is not null && method.HasSource())
                yield return (message, new MessageHandler(type, method, contract));
        }

        // Classe base genérica: class AdicionarPedidoHandler : CommandHandler<AdicionarPedidoCommand> { override Handle(...) }
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (!baseType.IsGenericType || !IsHandlerInterface(baseType.Name)) continue;
            if (baseType.TypeArguments[0] is not INamedTypeSymbol message || message.TypeKind == TypeKind.TypeParameter) continue;

            var declared = baseType.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && (m.IsAbstract || m.IsVirtual))
                .FirstOrDefault(m => m.Parameters.Any(p => KeyOf(p.Type) == KeyOf(message)) || HandleMethods.Contains(m.Name));
            if (declared is not null && CallResolver.Dispatch(type, declared) is { IsAbstract: false } method && method.HasSource())
                yield return (message, new MessageHandler(type, method, baseType));
        }
    }

    private static bool IsHandlerInterface(string name) =>
        HandlerInterfaces.Contains(name)
        || name.EndsWith("Handler", StringComparison.Ordinal)
        || name.EndsWith("Consumer", StringComparison.Ordinal);

    /// <summary>Implementação do método da interface que recebe a mensagem (ou o único método da interface).</summary>
    private static IMethodSymbol? HandleMethodOf(INamedTypeSymbol type, INamedTypeSymbol contract, INamedTypeSymbol message)
    {
        var members = contract.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary).ToList();
        var candidates = members.Where(m => m.Parameters.Any(p => KeyOf(p.Type) == KeyOf(message))).ToList();
        if (candidates.Count == 0) candidates = members.Where(m => HandleMethods.Contains(m.Name)).ToList();
        if (candidates.Count == 0 && members.Count == 1) candidates = members;

        foreach (var member in candidates)
            if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol { IsAbstract: false } implementation)
                return implementation;

        return null;
    }

    private void Add(ITypeSymbol message, MessageHandler handler, bool inHost)
    {
        var key = KeyOf(message);
        if (!_handlers.TryGetValue(key, out var list))
            _handlers[key] = list = [];

        if (!list.Any(h => h.Handler.Method.Key() == handler.Method.Key()))
            list.Add((handler, inHost));
    }

    /// <summary>Projeto do host e o que ele referencia (handlers registrados no container do host).</summary>
    private static HashSet<ProjectId> HostProjects(Solution solution, Project? host)
    {
        if (host is null) return solution.ProjectIds.ToHashSet();
        var graph = solution.GetProjectDependencyGraph();
        return graph.GetProjectsThatThisProjectTransitivelyDependsOn(host.Id).Append(host.Id).ToHashSet();
    }

    /// <summary>Chave igual entre compilações diferentes (o comando vem do projeto da API, o handler do Application).</summary>
    private static string KeyOf(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
