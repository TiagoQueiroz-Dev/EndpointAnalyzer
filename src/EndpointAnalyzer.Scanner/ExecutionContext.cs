using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>Tipo conhecido de um valor: Exact quando veio de "new X()" (o tipo em tempo de execução é exatamente X).</summary>
public sealed record KnownType(ITypeSymbol Type, bool Exact, bool NonNull);

/// <summary>Valor constante conhecido (bool, enum, null, número, string).</summary>
public sealed record KnownValue(object? Value);

/// <summary>
/// Contexto de execução de um nó do call graph: método construído, tipo concreto do "this",
/// genéricos (TEntity = Veiculo), tipos e valores conhecidos dos argumentos.
/// </summary>
public sealed class AnalysisExecutionContext
{
    public required IMethodSymbol Method { get; init; }

    /// <summary>Tipo concreto em que o método executa (ex.: VeiculoTipoService, mesmo se o método está em ServiceBase).</summary>
    public INamedTypeSymbol? ThisType { get; init; }

    public GenericTypeMap Generics { get; init; } = GenericTypeMap.Empty;

    /// <summary>Tipos conhecidos dos parâmetros (propagados da chamada).</summary>
    public Dictionary<string, KnownType> Arguments { get; init; } = [];

    /// <summary>Valores constantes conhecidos dos parâmetros.</summary>
    public Dictionary<string, KnownValue> Values { get; init; } = [];

    public ITypeSymbol? Resolve(ITypeSymbol? type) => Generics.Substitute(type);

    public IMethodSymbol Resolve(IMethodSymbol method) => Generics.Substitute(method);

    /// <summary>Tipo conhecido de uma expressão neste contexto (criação, local, parâmetro, genérico).</summary>
    public KnownType? KnownTypeOf(ExpressionSyntax expression, SemanticModel model, SyntaxNode scope)
    {
        expression = Unwrap(expression);

        switch (expression)
        {
            case BaseObjectCreationExpressionSyntax creation when Resolve(model.GetTypeInfo(creation).Type) is { } created:
                return new KnownType(created, Exact: true, NonNull: true);

            case IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }:
                var symbol = model.GetSymbolInfo(expression).Symbol;
                if (symbol is IParameterSymbol parameter && Arguments.TryGetValue(ParameterKey(parameter), out var argument))
                    return argument;
                if (symbol is ILocalSymbol local && LocalCreation(local, model, scope) is { } localType)
                    return localType;
                break;
        }

        var staticType = Resolve(model.GetTypeInfo(expression).Type);
        return staticType is null || staticType.TypeKind == TypeKind.Error ? null : new KnownType(staticType, Exact: false, NonNull: false);
    }

    /// <summary>Valor constante conhecido de uma expressão (literal, constante, enum ou parâmetro propagado).</summary>
    public KnownValue? KnownValueOf(ExpressionSyntax expression, SemanticModel model)
    {
        expression = Unwrap(expression);
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue) return new KnownValue(constant.Value);

        if (model.GetSymbolInfo(expression).Symbol is IParameterSymbol parameter && Values.TryGetValue(ParameterKey(parameter), out var value))
            return value;

        return null;
    }

    public static string ParameterKey(IParameterSymbol parameter) =>
        $"{parameter.ContainingSymbol.OriginalDefinition.Key()}#{parameter.Ordinal}";

    /// <summary>Local que só recebe "new X()" no método: o tipo em tempo de execução é X.</summary>
    private KnownType? LocalCreation(ILocalSymbol local, SemanticModel model, SyntaxNode scope)
    {
        var assigned = new List<ExpressionSyntax>();

        foreach (var reference in local.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
                assigned.Add(initializer);

        foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && assignment.Left is IdentifierNameSyntax
                && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, local))
                assigned.Add(assignment.Right);

        if (assigned.Count == 0) return null;

        var types = assigned
            .Select(Unwrap)
            .Select(e => e is BaseObjectCreationExpressionSyntax creation ? Resolve(model.GetTypeInfo(creation).Type) : null)
            .ToList();
        if (types.Any(t => t is null)) return null;

        var first = types[0]!;
        return types.All(t => SymbolEqualityComparer.Default.Equals(t, first)) ? new KnownType(first, Exact: true, NonNull: true) : null;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax p: expression = p.Expression; continue;
                case AwaitExpressionSyntax a: expression = a.Expression; continue;
                case PostfixUnaryExpressionSyntax s when s.IsKind(SyntaxKind.SuppressNullableWarningExpression): expression = s.Operand; continue;
                case CastExpressionSyntax c: expression = c.Expression; continue;
                default: return expression;
            }
        }
    }
}

/// <summary>
/// Mapa de parâmetros de tipo para tipos concretos (TEntity → Veiculo) e substituição em tipos e métodos.
/// </summary>
public sealed class GenericTypeMap
{
    public static readonly GenericTypeMap Empty = new([]);

    private readonly Dictionary<string, ITypeSymbol> _map;

    private GenericTypeMap(Dictionary<string, ITypeSymbol> map) => _map = map;

    public bool IsEmpty => _map.Count == 0;

    public IReadOnlyDictionary<string, ITypeSymbol> Entries => _map;

    /// <summary>
    /// Mapa a partir do método construído e do tipo concreto do this:
    /// argumentos de tipo do método, do tipo que o contém e de toda a cadeia de bases do this.
    /// </summary>
    public static GenericTypeMap For(IMethodSymbol method, INamedTypeSymbol? thisType, GenericTypeMap? outer = null)
    {
        var map = new Dictionary<string, ITypeSymbol>();

        void AddType(INamedTypeSymbol? type)
        {
            for (var t = type; t is not null; t = t.ContainingType)
            {
                if (!t.IsGenericType || t.IsUnboundGenericType) continue;
                var parameters = t.OriginalDefinition.TypeParameters;
                for (var i = 0; i < parameters.Length && i < t.TypeArguments.Length; i++)
                    // Definição não construída (TEntity → TEntity) não acrescenta informação.
                    if (!SymbolEqualityComparer.Default.Equals(t.TypeArguments[i], parameters[i]))
                        map.TryAdd(Key(parameters[i]), t.TypeArguments[i]);
            }
        }

        for (var t = thisType; t is not null; t = t.BaseType)
            AddType(t);
        AddType(method.ContainingType);

        if (method.IsGenericMethod)
        {
            var parameters = method.OriginalDefinition.TypeParameters;
            for (var i = 0; i < parameters.Length && i < method.TypeArguments.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(method.TypeArguments[i], parameters[i]))
                    map.TryAdd(Key(parameters[i]), method.TypeArguments[i]);
        }

        var result = new GenericTypeMap(map);

        // Argumentos de tipo que ainda são parâmetros do chamador: resolve pelo mapa externo.
        if (outer is { IsEmpty: false })
            foreach (var key in map.Keys.ToList())
                map[key] = outer.Substitute(map[key])!;

        return result;
    }

    public ITypeSymbol? Substitute(ITypeSymbol? type)
    {
        if (type is null || _map.Count == 0) return type;

        switch (type)
        {
            case ITypeParameterSymbol parameter:
                return _map.TryGetValue(Key(parameter), out var concrete) && !SymbolEqualityComparer.Default.Equals(concrete, parameter)
                    ? Substitute(concrete)
                    : type;

            case INamedTypeSymbol { IsGenericType: true, IsUnboundGenericType: false } named:
                var arguments = named.TypeArguments.Select(a => Substitute(a)!).ToArray();
                var changed = arguments.Where((a, i) => !SymbolEqualityComparer.Default.Equals(a, named.TypeArguments[i])).Any();
                if (!changed) return type;
                try
                {
                    return named.OriginalDefinition.Construct(arguments);
                }
                catch (ArgumentException)
                {
                    return type;
                }

            default:
                return type;
        }
    }

    /// <summary>IRepository&lt;TKey, TEntity&gt;.Adicionar → IRepository&lt;int, Veiculo&gt;.Adicionar.</summary>
    public IMethodSymbol Substitute(IMethodSymbol method)
    {
        if (_map.Count == 0) return method;

        var result = method;
        if (Substitute(method.ContainingType) is INamedTypeSymbol containing
            && !SymbolEqualityComparer.Default.Equals(containing, method.ContainingType))
        {
            var match = containing.GetMembers(method.Name).OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.OriginalDefinition.Key() == method.OriginalDefinition.Key());
            if (match is not null) result = match;
        }

        if (method.IsGenericMethod)
        {
            var arguments = method.TypeArguments.Select(a => Substitute(a)!).ToArray();
            if (arguments.Where((a, i) => !SymbolEqualityComparer.Default.Equals(a, method.TypeArguments[i])).Any())
            {
                try
                {
                    var definition = result.IsGenericMethod ? result.ConstructedFrom : result;
                    result = definition.Construct(arguments);
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return result;
    }

    public static string Key(ITypeParameterSymbol parameter) =>
        $"{parameter.ContainingSymbol?.OriginalDefinition.Key()}#{parameter.TypeParameterKind}#{parameter.Ordinal}";

    /// <summary>Ex.: "TEntity = Veiculo, TKey = int".</summary>
    public string Describe(INamedTypeSymbol? type)
    {
        if (type is null) return "";
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType) continue;
            var parameters = t.OriginalDefinition.TypeParameters;
            for (var i = 0; i < parameters.Length && i < t.TypeArguments.Length; i++)
                parts.Add($"{parameters[i].Name} = {t.TypeArguments[i].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}");
        }
        return string.Join(", ", parts.Distinct());
    }
}
