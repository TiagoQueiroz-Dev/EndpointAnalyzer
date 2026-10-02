using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace EndpointAnalyzer.Scanner;

public static class ResolutionStrategies
{
    public const string Direct = "chamada direta";
    public const string ConcreteReceiver = "receiver concreto";
    public const string KnownInstance = "instância conhecida (new)";
    public const string DependencyInjection = "DI";
    public const string Constructor = "construtor";
    public const string Assignment = "atribuição";
    public const string Search = "busca por implementações";
    public const string ThisType = "this concreto";
}

public sealed record CallTarget(IMethodSymbol Method, INamedTypeSymbol? ThisType, string? Via, string Strategy);

public sealed record CallResolution(
    IReadOnlyList<CallTarget> Targets,
    string? Receiver,
    bool Ambiguous = false,
    IReadOnlyList<string>? Candidates = null);

/// <summary>
/// Resolve para qual implementação uma chamada realmente vai, na ordem:
/// 1. tipo concreto conhecido no receiver; 2. implementação registrada na DI;
/// 3. tipo inferido pelo construtor; 4. tipo inferido por atribuição; 5. busca genérica (último recurso).
/// Várias implementações possíveis → resolução ambígua (não expande todas).
/// </summary>
public sealed class CallResolver(Solution solution, DependencyInjectionMap di)
{
    public async Task<CallResolution> ResolveAsync(SyntaxNode call, IMethodSymbol rawMethod, AnalyzedMethod caller, CancellationToken cancellationToken)
    {
        var context = caller.Context;
        var method = context.Resolve(rawMethod);

        // new X(...): o construtor executa com this = X.
        if (call is BaseObjectCreationExpressionSyntax)
            return Single(method, method.ContainingType, null, ResolutionStrategies.Direct, null);

        // : base(...) / : this(...)
        if (call is ConstructorInitializerSyntax)
            return Single(method, context.ThisType ?? method.ContainingType, null, ResolutionStrategies.Direct, null);

        if (method.IsStatic || method.MethodKind == MethodKind.LocalFunction)
            return Single(method, null, null, ResolutionStrategies.Direct, null);

        var receiverExpression = (call as InvocationExpressionSyntax)?.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Expression,
            MemberBindingExpressionSyntax when call.Parent?.Parent is ConditionalAccessExpressionSyntax ca => ca.Expression,
            _ => null,
        };

        // base.Metodo(): sem despacho virtual.
        if (receiverExpression is BaseExpressionSyntax)
            return Single(method, context.ThisType, null, ResolutionStrategies.Direct, "base");

        // Metodo() ou this.Metodo(): o receiver é o this concreto do contexto.
        if (receiverExpression is null or ThisExpressionSyntax)
        {
            var thisType = context.ThisType ?? method.ContainingType;
            var target = Dispatch(thisType, method) ?? method;
            var strategy = context.ThisType is not null && target.ContainingType.OriginalDefinition.Key() != method.ContainingType.OriginalDefinition.Key()
                ? ResolutionStrategies.ThisType
                : ResolutionStrategies.Direct;
            return Single(target, thisType, null, strategy, null);
        }

        var receiverSymbol = caller.Model.GetSymbolInfo(receiverExpression, cancellationToken).Symbol;
        var receiverName = receiverSymbol?.Name ?? receiverExpression.ToString();
        var staticType = context.Resolve(caller.Model.GetTypeInfo(receiverExpression, cancellationToken).Type) as INamedTypeSymbol;

        // 1. Tipo concreto conhecido: instância criada com new / argumento com tipo exato.
        var known = context.KnownTypeOf(receiverExpression, caller.Model, caller.Declaration);
        if (known is { Exact: true, Type: INamedTypeSymbol exact })
            return Resolved(exact, method, staticType, ResolutionStrategies.KnownInstance, receiverName);

        if (known?.Type is INamedTypeSymbol knownType && IsConcrete(knownType))
            return Resolved(knownType, method, staticType, ResolutionStrategies.ConcreteReceiver, receiverName);

        if (staticType is not null && IsConcrete(staticType))
            return Resolved(staticType, method, null, ResolutionStrategies.ConcreteReceiver, receiverName);

        // Interface/abstrato: tenta estreitar o tipo pelo construtor (campo recebido via : base(...)).
        var declaredType = staticType;
        var strategyPrefix = "";
        if (receiverSymbol is IFieldSymbol or IPropertySymbol
            && InferFromConstructor(receiverSymbol, context.ThisType) is { Type: INamedTypeSymbol inferredType } inferred)
        {
            if (inferred.Exact && IsConcrete(inferredType))
                return Resolved(inferredType, method, staticType, ResolutionStrategies.Constructor, receiverName);
            declaredType = context.Resolve(inferredType) as INamedTypeSymbol ?? inferredType;
            strategyPrefix = ResolutionStrategies.Constructor + " + ";
        }

        // 2. DI: implementação registrada para o tipo do receiver (não para o tipo que declara o método).
        if (declaredType is not null)
        {
            var registered = di.ImplementationsOf(declaredType);
            if (registered.Count == 1)
                return Resolved(registered[0], method, staticType, strategyPrefix + ResolutionStrategies.DependencyInjection, receiverName);
            if (registered.Count > 1)
                return Ambiguous(method, receiverName, registered.Select(r => r.Name));
        }

        // 4. Atribuição: local que só recebe um tipo concreto.
        if (receiverSymbol is ILocalSymbol && known is { Type: INamedTypeSymbol assigned } && IsConcrete(assigned))
            return Resolved(assigned, method, staticType, ResolutionStrategies.Assignment, receiverName);

        // 5. Último recurso: busca genérica por implementações na solução.
        var implementations = await SearchImplementationsAsync(method, declaredType, cancellationToken);
        return implementations.Count switch
        {
            0 => Single(method, null, null, ResolutionStrategies.Direct, receiverName),
            1 => Resolved(implementations[0], method, staticType, ResolutionStrategies.Search, receiverName),
            _ => Ambiguous(method, receiverName, implementations.Select(i => i.Name)),
        };
    }

    /// <summary>Método que executa quando a chamada é feita sobre uma instância de <paramref name="concrete"/>.</summary>
    public static IMethodSymbol? Dispatch(INamedTypeSymbol concrete, IMethodSymbol method)
    {
        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            var implementation = concrete.FindImplementationForInterfaceMember(method) as IMethodSymbol;

            // Símbolos de compilações diferentes: localiza a interface equivalente no tipo concreto.
            if (implementation is null)
            {
                var key = method.OriginalDefinition.Key();
                implementation = concrete.AllInterfaces
                    .SelectMany(i => i.GetMembers(method.Name).OfType<IMethodSymbol>())
                    .Where(m => m.OriginalDefinition.Key() == key)
                    .Select(m => concrete.FindImplementationForInterfaceMember(m) as IMethodSymbol)
                    .FirstOrDefault(m => m is not null);
            }

            implementation ??= FindByName(concrete, method);

            // Implementação virtual na base pode estar sobrescrita no tipo concreto.
            return implementation is { IsVirtual: true } or { IsOverride: true } ? Dispatch(concrete, implementation) : implementation;
        }

        if (!method.IsVirtual && !method.IsAbstract && !method.IsOverride)
            return method;

        // Override mais derivado na cadeia do tipo concreto.
        var definition = method.OriginalDefinition.Key();
        for (var t = concrete; t is not null; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                for (var m = member; m is not null; m = m.OverriddenMethod)
                    if (m.OriginalDefinition.Key() == definition)
                        return member.IsAbstract ? null : member;
            }
        }

        return method.IsAbstract ? FindByName(concrete, method) : method;
    }

    private static IMethodSymbol? FindByName(INamedTypeSymbol concrete, IMethodSymbol method)
    {
        for (var t = concrete; t is not null; t = t.BaseType)
        {
            var match = t.GetMembers(method.Name).OfType<IMethodSymbol>()
                .FirstOrDefault(m => !m.IsAbstract && m.Parameters.Length == method.Parameters.Length);
            if (match is not null) return match;
        }
        return null;
    }

    /// <summary>
    /// Inferência pelo construtor (§4.3): campo atribuído no construtor com new X() ou com um parâmetro
    /// que o construtor da classe derivada preenche via : base(...) com um tipo mais específico.
    /// Ex.: ServiceBase._repository (IRepository&lt;TKey,TEntity&gt;) ← VeiculoTipoService(IVeiculoTipoRepository) : base(pRepository).
    /// </summary>
    private KnownType? InferFromConstructor(ISymbol member, INamedTypeSymbol? thisType)
    {
        var declaringType = member.ContainingType;

        // Inicializador do campo: private readonly IX _x = new X();
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            var initializer = syntax switch
            {
                VariableDeclaratorSyntax v => v.Initializer?.Value,
                PropertyDeclarationSyntax p => p.Initializer?.Value,
                _ => null,
            };
            if (initializer is BaseObjectCreationExpressionSyntax creation
                && Model(creation)?.GetTypeInfo(creation).Type is INamedTypeSymbol created)
                return new KnownType(created, Exact: true, NonNull: true);

            // Construtor primário: private readonly IRepo _repo = repo;
            if (initializer is IdentifierNameSyntax && Model(initializer)?.GetSymbolInfo(initializer).Symbol is IParameterSymbol primaryParameter
                && NarrowThroughDerivedConstructors(primaryParameter, declaringType, thisType) is { } narrowedPrimary)
                return narrowedPrimary;
        }

        foreach (var constructor in declaringType.OriginalDefinition.InstanceConstructors)
        {
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not ConstructorDeclarationSyntax syntax) continue;
                var model = Model(syntax);
                if (model is null) continue;

                foreach (var assignment in syntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(assignment.Left).Symbol is not { } assigned || assigned.Key() != member.Key())
                        continue;

                    var right = assignment.Right is PostfixUnaryExpressionSyntax { Operand: var operand } ? operand : assignment.Right;
                    switch (right)
                    {
                        case BaseObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation).Type is INamedTypeSymbol created:
                            return new KnownType(created, Exact: true, NonNull: true);

                        case IdentifierNameSyntax when model.GetSymbolInfo(right).Symbol is IParameterSymbol parameter:
                            if (NarrowThroughDerivedConstructors(parameter, declaringType, thisType) is { } narrowed)
                                return narrowed;
                            break;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Segue o parâmetro do construtor base até o argumento passado em : base(...) pela classe concreta.</summary>
    private KnownType? NarrowThroughDerivedConstructors(IParameterSymbol parameter, INamedTypeSymbol baseType, INamedTypeSymbol? thisType)
    {
        if (thisType is null) return null;

        // Cadeia do this concreto até a classe base que declara o campo.
        var chain = new List<INamedTypeSymbol>();
        for (var t = thisType; t is not null; t = t.BaseType)
        {
            // Compara por chave: o this pode vir de outra compilação (projeto host) que a base (projeto de domínio).
            if (t.OriginalDefinition.Key() == baseType.OriginalDefinition.Key()) break;
            chain.Add(t);
        }
        if (chain.Count == 0) return null;

        // Da classe imediatamente derivada da base até o this: substitui o parâmetro a cada nível.
        var current = parameter;
        KnownType? result = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var found = false;
            foreach (var constructor in chain[i].OriginalDefinition.InstanceConstructors)
            {
                foreach (var reference in constructor.DeclaringSyntaxReferences)
                {
                    var syntax = reference.GetSyntax();
                    // ctor(...) : base(args)  ou  class X(...) : Base(args)  (construtor primário)
                    var arguments = syntax switch
                    {
                        ConstructorDeclarationSyntax { Initializer: { } initializer } when initializer.ThisOrBaseKeyword.IsKind(SyntaxKind.BaseKeyword)
                            => initializer.ArgumentList,
                        TypeDeclarationSyntax type => type.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault()?.ArgumentList,
                        _ => null,
                    };
                    if (arguments is null) continue;
                    var model = Model(syntax);
                    if (model is null || current.Ordinal >= arguments.Arguments.Count) continue;

                    var argument = arguments.Arguments[current.Ordinal].Expression;
                    if (argument is BaseObjectCreationExpressionSyntax creation && model.GetTypeInfo(creation).Type is INamedTypeSymbol created)
                        return new KnownType(created, Exact: true, NonNull: true);

                    if (model.GetSymbolInfo(argument).Symbol is IParameterSymbol derived)
                    {
                        current = derived;
                        result = derived.Type is INamedTypeSymbol narrowed ? new KnownType(narrowed, Exact: false, NonNull: false) : result;
                        found = true;
                    }
                    break;
                }
                if (found) break;
            }
            if (!found) break;
        }

        return result;
    }

    private async Task<List<INamedTypeSymbol>> SearchImplementationsAsync(IMethodSymbol method, INamedTypeSymbol? receiverType, CancellationToken cancellationToken)
    {
        var definition = method.OriginalDefinition;
        IEnumerable<ISymbol> found;
        try
        {
            found = definition.ContainingType.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(definition, solution, cancellationToken: cancellationToken)
                : definition.IsAbstract || definition.IsVirtual
                    ? await SymbolFinder.FindOverridesAsync(definition, solution, cancellationToken: cancellationToken)
                    : [];
        }
        catch (InvalidOperationException)
        {
            found = [];
        }

        var types = found.OfType<IMethodSymbol>()
            .Where(m => m.HasSource() && !m.IsAbstract)
            .Select(m => m.ContainingType)
            // Se o receiver é uma interface mais específica (IVeiculoRepository), só valem os tipos que a implementam.
            .Where(t => receiverType is null || Implements(t, receiverType))
            .GroupBy(t => t.OriginalDefinition.Key())
            .Select(g => g.First())
            .ToList();

        return types;
    }

    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol service)
    {
        var key = service.OriginalDefinition.Key();
        if (type.OriginalDefinition.Key() == key) return true;
        for (var t = type; t is not null; t = t.BaseType)
            if (t.OriginalDefinition.Key() == key) return true;
        return type.AllInterfaces.Any(i => i.OriginalDefinition.Key() == key);
    }

    private CallResolution Resolved(INamedTypeSymbol concrete, IMethodSymbol method, INamedTypeSymbol? declared, string strategy, string? receiver)
    {
        var target = Dispatch(concrete, method);
        if (target is null) return Single(method, null, null, ResolutionStrategies.Direct, receiver);

        var via = declared is not null && declared.OriginalDefinition.Key() != concrete.OriginalDefinition.Key()
            ? declared.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : null;
        return new CallResolution([new CallTarget(target, concrete, via, strategy)], receiver);
    }

    private static CallResolution Single(IMethodSymbol method, INamedTypeSymbol? thisType, string? via, string strategy, string? receiver) =>
        new([new CallTarget(method, thisType, via, strategy)], receiver);

    private static CallResolution Ambiguous(IMethodSymbol method, string? receiver, IEnumerable<string> candidates) =>
        new([new CallTarget(method, null, null, "resolução ambígua")], receiver, Ambiguous: true, Candidates: candidates.Distinct().ToList());

    private static bool IsConcrete(INamedTypeSymbol type) =>
        type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract;

    private SemanticModel? Model(SyntaxNode node)
    {
        var document = solution.GetDocument(node.SyntaxTree);
        return document is not null && document.TryGetSemanticModel(out var model)
            ? model
            : document?.GetSemanticModelAsync().GetAwaiter().GetResult();
    }
}
