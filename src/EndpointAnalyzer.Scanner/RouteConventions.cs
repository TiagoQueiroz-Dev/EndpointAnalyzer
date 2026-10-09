using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Lê as convenções registradas no projeto e interpreta apenas operações de string/booleanas do código-fonte.
/// Não instancia nem executa classes da API analisada.
/// </summary>
internal sealed class RouteConventions
{
    private readonly List<(SourceMethod Transform, SourceMethod? Applies)> _rules = [];

    public static async Task<RouteConventions> ReadAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var result = new RouteConventions();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var registration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (registration.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } add
                    || add.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Conventions" }) continue;
                foreach (var argument in registration.ArgumentList.Arguments)
                {
                    if (model.GetTypeInfo(argument.Expression, cancellationToken).Type is not INamedTypeSymbol convention
                        || !Bases(convention).Any(t => t.Name == "RouteTokenTransformerConvention")) continue;
                    var creation = ResolveCreation(argument.Expression, model);
                    if (creation is null) throw Unsupported(convention.Name);
                    var transformerArgument = creation.ArgumentList?.Arguments.FirstOrDefault(a =>
                        model.GetTypeInfo(a.Expression, cancellationToken).Type is INamedTypeSymbol t
                        && t.AllInterfaces.Any(i => i.Name == "IOutboundParameterTransformer"));
                    if (transformerArgument is null
                        || model.GetTypeInfo(transformerArgument.Expression, cancellationToken).Type is not INamedTypeSymbol transformer)
                        throw Unsupported(convention.Name);
                    var transform = await SourceMethod.FindAsync(project.Solution, transformer, "TransformOutbound", cancellationToken)
                        ?? throw Unsupported(transformer.Name);
                    var applies = await SourceMethod.FindAsync(project.Solution, convention, "ShouldApply", cancellationToken);
                    result._rules.Add((transform, applies));
                }
            }
        }
        return result;
    }

    public Func<string, string>? For(INamedTypeSymbol controller)
    {
        SourceMethod? selected = null;
        foreach (var (transform, applies) in _rules)
            if (applies is null || applies.Invoke(controller) is true) selected = transform;
        return selected is null ? null : value => selected.Invoke(value) as string ?? "";
    }

    private static ObjectCreationExpressionSyntax? ResolveCreation(ExpressionSyntax expression, SemanticModel model)
    {
        if (expression is ObjectCreationExpressionSyntax creation) return creation;
        return model.GetSymbolInfo(expression).Symbol?.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .OfType<VariableDeclaratorSyntax>().Select(v => v.Initializer?.Value).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
    }

    private static IEnumerable<INamedTypeSymbol> Bases(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType) yield return current;
    }

    private static InvalidOperationException Unsupported(string source) => new(
        $"Não foi possível resolver a convenção de rota {source} pelo código-fonte. A rota não será presumida, pois poderia causar HTTP 404.");

    private sealed class SourceMethod(MethodDeclarationSyntax syntax, SemanticModel model)
    {
        public static async Task<SourceMethod?> FindAsync(Solution solution, INamedTypeSymbol type, string name, CancellationToken token)
        {
            foreach (var current in Bases(type))
            {
                var method = current.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 1);
                if (method?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(token) is not MethodDeclarationSyntax declaration) continue;
                var document = solution.GetDocument(declaration.SyntaxTree);
                if (document is not null && await document.GetSemanticModelAsync(token) is { } semantic)
                    return new SourceMethod(declaration, semantic);
            }
            return null;
        }

        public object? Invoke(object value)
        {
            var locals = new Dictionary<string, object?> { [syntax.ParameterList.Parameters[0].Identifier.ValueText] = value };
            try
            {
                if (syntax.ExpressionBody is { } arrow) return Evaluate(arrow.Expression, locals);
                if (syntax.Body is { } body && Execute(body, locals, out var result)) return result;
                throw Unsupported(syntax.Identifier.ValueText);
            }
            catch (Exception e) when (e is NotSupportedException or RegexMatchTimeoutException or ArgumentException)
            {
                throw Unsupported(syntax.Identifier.ValueText);
            }
        }

        private bool Execute(StatementSyntax statement, Dictionary<string, object?> locals, out object? result)
        {
            result = null;
            switch (statement)
            {
                case BlockSyntax block:
                    foreach (var child in block.Statements) if (Execute(child, locals, out result)) return true;
                    return false;
                case ReturnStatementSyntax { Expression: { } expression }:
                    result = Evaluate(expression, locals);
                    return true;
                case LocalDeclarationStatementSyntax declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                        locals[variable.Identifier.ValueText] = variable.Initializer is null ? null : Evaluate(variable.Initializer.Value, locals);
                    return false;
                case IfStatementSyntax condition:
                    var branch = Evaluate(condition.Condition, locals) is true ? condition.Statement : condition.Else?.Statement;
                    return branch is not null && Execute(branch, locals, out result);
                default: throw new NotSupportedException();
            }
        }

        private object? Evaluate(ExpressionSyntax expression, Dictionary<string, object?> locals, object? receiver = null)
        {
            var constant = model.GetConstantValue(expression);
            if (constant.HasValue) return constant.Value;
            switch (expression)
            {
                case IdentifierNameSyntax identifier when locals.TryGetValue(identifier.Identifier.ValueText, out var value): return value;
                case ParenthesizedExpressionSyntax parentheses: return Evaluate(parentheses.Expression, locals, receiver);
                case ConditionalExpressionSyntax conditional:
                    return Evaluate(Evaluate(conditional.Condition, locals) is true ? conditional.WhenTrue : conditional.WhenFalse, locals);
                case ConditionalAccessExpressionSyntax access:
                    var target = Evaluate(access.Expression, locals, receiver);
                    return target is null ? null : Evaluate(access.WhenNotNull, locals, target);
                case MemberBindingExpressionSyntax binding: return Member(receiver, binding.Name.Identifier.ValueText);
                case MemberAccessExpressionSyntax access:
                    return Member(Evaluate(access.Expression, locals, receiver), access.Name.Identifier.ValueText);
                case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
                    return Evaluate(unary.Operand, locals) is not true;
                case BinaryExpressionSyntax binary:
                    var left = Evaluate(binary.Left, locals);
                    if (binary.IsKind(SyntaxKind.CoalesceExpression)) return left ?? Evaluate(binary.Right, locals);
                    if (binary.IsKind(SyntaxKind.LogicalAndExpression)) return left is true && Evaluate(binary.Right, locals) is true;
                    if (binary.IsKind(SyntaxKind.LogicalOrExpression)) return left is true || Evaluate(binary.Right, locals) is true;
                    var right = Evaluate(binary.Right, locals);
                    if (binary.IsKind(SyntaxKind.EqualsExpression)) return Equals(left, right);
                    if (binary.IsKind(SyntaxKind.NotEqualsExpression)) return !Equals(left, right);
                    throw new NotSupportedException();
                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax, Name.Identifier.ValueText: "ShouldApply" }) return true;
                    var arguments = invocation.ArgumentList.Arguments.Select(a => Evaluate(a.Expression, locals)).ToArray();
                    if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { ContainingType.Name: "Regex", Name: "Replace" })
                        return Regex.Replace((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!,
                            arguments.Length > 3 ? (RegexOptions)Convert.ToInt32(arguments[3]) : RegexOptions.None, TimeSpan.FromMilliseconds(100));
                    var (instance, name) = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax member => (Evaluate(member.Expression, locals, receiver), member.Name.Identifier.ValueText),
                        MemberBindingExpressionSyntax member => (receiver, member.Name.Identifier.ValueText),
                        _ => throw new NotSupportedException(),
                    };
                    return name switch
                    {
                        "ToString" when arguments.Length == 0 => instance?.ToString(),
                        "ToLower" when instance is string s && arguments.Length == 0 => s.ToLower(),
                        "ToLowerInvariant" when instance is string s && arguments.Length == 0 => s.ToLowerInvariant(),
                        "ToUpper" when instance is string s && arguments.Length == 0 => s.ToUpper(),
                        "ToUpperInvariant" when instance is string s && arguments.Length == 0 => s.ToUpperInvariant(),
                        "Contains" when instance is string s && arguments.Length == 1 => s.Contains((string)arguments[0]!),
                        "Replace" when instance is string s && arguments.Length == 2 => s.Replace((string)arguments[0]!, (string)arguments[1]!),
                        _ => throw new NotSupportedException(),
                    };
                default: throw new NotSupportedException();
            }
        }

        private static object? Member(object? target, string member) => (target, member) switch
        {
            (INamedTypeSymbol type, "Controller") => type,
            (INamedTypeSymbol type, "ControllerType") => type,
            (INamedTypeSymbol type, "BaseType") => type.BaseType,
            (INamedTypeSymbol type, "Name") => type.Name,
            _ => throw new NotSupportedException(),
        };
    }
}
