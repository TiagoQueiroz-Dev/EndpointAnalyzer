using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.ChangeDetection;

public interface IConditionAnalyzer
{
    Task<IReadOnlyList<ConditionInfo>> AnalyzeAsync(CallGraph graph, CancellationToken cancellationToken = default);
}

/// <summary>
/// Coleta if, switch, throw, guardas (?? throw, ThrowIfNull) e validações
/// (DataAnnotations dos DTOs e FluentValidation AbstractValidator&lt;T&gt;).
/// </summary>
public class ConditionAnalyzer : IConditionAnalyzer
{
    private static readonly HashSet<string> DataAnnotations =
        ["Required", "MaxLength", "MinLength", "StringLength", "Range", "EmailAddress", "RegularExpression", "Phone", "Url", "Compare", "CreditCard", "AllowedValues", "DeniedValues", "Length"];

    public async Task<IReadOnlyList<ConditionInfo>> AnalyzeAsync(CallGraph graph, CancellationToken cancellationToken = default)
    {
        var conditions = new List<ConditionInfo>();

        foreach (var method in graph.Methods)
            conditions.AddRange(AnalyzeMethod(method));

        var parameterTypes = graph.EntryPoint.Symbol.Parameters
            .Select(p => p.Type)
            .OfType<INamedTypeSymbol>()
            .Where(t => t.HasSource())
            .ToList();

        conditions.AddRange(DataAnnotationValidations(graph));
        conditions.AddRange(await FluentValidationRulesAsync(graph, parameterTypes, cancellationToken));

        return conditions;
    }

    private static IEnumerable<ConditionInfo> AnalyzeMethod(AnalyzedMethod method)
    {
        var model = method.Model;

        foreach (var node in method.Declaration.DescendantNodes())
        {
            // Branch impossível no contexto de execução: não é regra deste endpoint.
            if (!method.IsReachable(node)) continue;

            switch (node)
            {
                case IfStatementSyntax ifs:
                    var (action, message) = Describe(ifs.Statement, model);
                    yield return Create(method, ConditionKinds.If, ifs.Condition.Compact(), action, message, ifs);
                    break;

                case SwitchStatementSyntax sw:
                    var sections = sw.Sections.Select(s =>
                    {
                        var (sectionAction, sectionMessage) = Describe(s.Statements, model);
                        return $"{SyntaxConditions.SectionCondition(sw.Expression, s)} → {sectionAction}" + (sectionMessage is null ? "" : $" (\"{sectionMessage}\")");
                    });
                    yield return Create(method, ConditionKinds.Switch, sw.Expression.Compact(), string.Join("; ", sections), null, sw);
                    break;

                case SwitchExpressionSyntax se:
                    var arms = se.Arms.Select(a => $"{a.Pattern.Compact()}{(a.WhenClause is null ? "" : " " + a.WhenClause.Compact())} → {a.Expression.Compact()}");
                    yield return Create(method, ConditionKinds.Switch, se.GoverningExpression.Compact(), string.Join("; ", arms), null, se);
                    break;

                // valor ?? throw new X("...")
                case ThrowExpressionSyntax te when te.Parent is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression):
                    yield return Create(method, ConditionKinds.Guard, $"{b.Left.Compact()} == null", ThrowAction(te.Expression, model), MessageOf(te.Expression, model), StatementOf(te));
                    break;

                // cond ? valor : throw new X("...")
                case ThrowExpressionSyntax te when te.Parent is ConditionalExpressionSyntax c:
                    var expression = te == c.WhenFalse ? SyntaxConditions.Negate(c.Condition) : c.Condition.Compact();
                    yield return Create(method, ConditionKinds.Guard, expression, ThrowAction(te.Expression, model), MessageOf(te.Expression, model), StatementOf(te));
                    break;

                // throw fora de if/switch: sempre lança quando o método é executado.
                case ThrowStatementSyntax ts when ts.Expression is not null && SyntaxConditions.GetEnclosingCondition(ts, method.Declaration) is null:
                    yield return Create(method, ConditionKinds.Throw, "sempre", ThrowAction(ts.Expression, model), MessageOf(ts.Expression, model), ts);
                    break;

                // ArgumentNullException.ThrowIfNull(x), Guard.Against.Null(x)...
                case InvocationExpressionSyntax inv when IsGuardCall(inv, model):
                    yield return Create(method, ConditionKinds.Guard, inv.Compact(), "throw", MessageOf(inv, model), StatementOf(inv));
                    break;
            }
        }
    }

    private static bool IsGuardCall(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        var type = method.ContainingType.Name;
        return method.Name.StartsWith("ThrowIf", StringComparison.Ordinal)
            || type is "Guard" or "Ensure" or "Check" or "GuardClauseExtensions";
    }

    /// <summary>Atributos [Required], [MaxLength]... nos parâmetros e nas propriedades dos DTOs recebidos.</summary>
    private static IEnumerable<ConditionInfo> DataAnnotationValidations(CallGraph graph)
    {
        var entry = graph.EntryPoint;

        foreach (var parameter in entry.Symbol.Parameters)
        {
            if (parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax parameterSyntax)
                foreach (var attribute in parameterSyntax.AllAttributes().Where(a => DataAnnotations.Contains(a.ShortName())))
                    yield return Validation(graph, $"{parameter.Name}: [{attribute.Compact()}]", attribute, entry.DisplayName);

            if (parameter.Type is not INamedTypeSymbol type || !type.HasSource()) continue;

            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var declaration = reference.GetSyntax();

                // record Dto([Required] string Nome)
                if (declaration is TypeDeclarationSyntax { ParameterList: { } primary })
                    foreach (var p in primary.Parameters)
                        foreach (var attribute in p.AllAttributes().Where(a => DataAnnotations.Contains(a.ShortName())))
                            yield return Validation(graph, $"{type.Name}.{p.Identifier.Text}: [{attribute.Compact()}]", attribute, type.Name);

                foreach (var property in declaration.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                    foreach (var attribute in property.AllAttributes().Where(a => DataAnnotations.Contains(a.ShortName())))
                        yield return Validation(graph, $"{type.Name}.{property.Identifier.Text}: [{attribute.Compact()}]", attribute, type.Name);
            }
        }
    }

    /// <summary>RuleFor(...) de validators AbstractValidator&lt;T&gt; para os tipos recebidos pelo endpoint.</summary>
    private static async Task<IEnumerable<ConditionInfo>> FluentValidationRulesAsync(
        CallGraph graph, IReadOnlyList<INamedTypeSymbol> parameterTypes, CancellationToken cancellationToken)
    {
        var results = new List<ConditionInfo>();
        if (parameterTypes.Count == 0) return results;

        var targetKeys = parameterTypes.Select(t => t.Key()).ToHashSet();

        foreach (var project in graph.Solution.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                if (!root.DescendantNodes().OfType<SimpleNameSyntax>().Any(n => n.Identifier.Text == "AbstractValidator")) continue;

                var model = compilation.GetSemanticModel(tree);
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(cls, cancellationToken) is not INamedTypeSymbol validator) continue;
                    var validated = ValidatedType(validator);
                    if (validated is null || !targetKeys.Contains(validated.Key())) continue;

                    foreach (var statement in cls.DescendantNodes().OfType<ExpressionStatementSyntax>())
                    {
                        if (!statement.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "RuleFor")) continue;

                        var message = statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "WithMessage" })
                            .Select(i => MessageOf(i, model))
                            .FirstOrDefault(m => m is not null);

                        results.Add(new ConditionInfo
                        {
                            Kind = ConditionKinds.Validation,
                            Expression = statement.Expression.Compact(),
                            Action = "falha de validação (FluentValidation)",
                            Message = message,
                            Method = validator.Name,
                            SourceFile = graph.Solution.RelativePath(tree.FilePath),
                            SourceLine = statement.StartLine(),
                            Code = statement.Snippet(),
                        });
                    }
                }
            }
        }

        return results;
    }

    private static INamedTypeSymbol? ValidatedType(INamedTypeSymbol validator)
    {
        for (var t = validator.BaseType; t is not null; t = t.BaseType)
            if (t is { Name: "AbstractValidator", TypeArguments.Length: 1 })
                return t.TypeArguments[0] as INamedTypeSymbol;
        return null;
    }

    private static ConditionInfo Validation(CallGraph graph, string expression, AttributeSyntax attribute, string owner) => new()
    {
        Kind = ConditionKinds.Validation,
        Expression = expression,
        Action = "falha de validação (400 Bad Request)",
        Message = attribute.StringArgument(null, "ErrorMessage"),
        Method = owner,
        SourceFile = graph.Solution.RelativePath(attribute.SyntaxTree.FilePath),
        SourceLine = attribute.StartLine(),
        Code = attribute.Parent?.Parent?.Snippet(3),
    };

    /// <summary>Resume o que o bloco faz: throw, return ou bloco condicional.</summary>
    private static (string Action, string? Message) Describe(StatementSyntax statement, SemanticModel model) =>
        // Não usar [statement]: viraria uma SyntaxList nova, fora da árvore original.
        Describe(statement is BlockSyntax block ? block.Statements : Enumerable.Repeat(statement, 1), model);

    private static (string Action, string? Message) Describe(IEnumerable<StatementSyntax> statements, SemanticModel model)
    {
        var list = statements.ToList();

        if (list.OfType<ThrowStatementSyntax>().FirstOrDefault() is { Expression: { } thrown })
            return (ThrowAction(thrown, model), MessageOf(thrown, model));

        if (list.OfType<ReturnStatementSyntax>().FirstOrDefault() is { } ret)
            return (ret.Expression is null ? "return" : $"return {ReturnSummary(ret.Expression)}", ret.Expression is null ? null : MessageOf(ret.Expression, model));

        // NotifyError("chave", "mensagem") / AddNotification(...): regra que devolve erro de validação.
        var notification = list.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression is AwaitExpressionSyntax a ? a.Expression : s.Expression)
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => InvokedName(i) is { } n
                && (n.StartsWith("Notify", StringComparison.Ordinal) || n.StartsWith("Notific", StringComparison.Ordinal)
                    || n is "AddError" or "AdicionarErro" or "AddNotification" or "AddModelError"));
        if (notification is not null)
            return ("notifica erro de validação", MessageOf(notification.ArgumentList.Arguments.LastOrDefault()?.Expression ?? notification, model));

        var assignments = list.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression)
            .OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.Compact())
            .ToList();
        if (assignments.Count > 0)
            return ($"altera {string.Join(", ", assignments)}", null);

        var calls = list.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression is AwaitExpressionSyntax a ? a.Expression : s.Expression)
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression is MemberAccessExpressionSyntax m ? $"{m.Expression.Compact().TrimStart('_')}.{m.Name.Identifier.Text}" : InvokedName(i))
            .OfType<string>()
            .ToList();
        if (calls.Count > 0)
            return ($"chama {string.Join(", ", calls)}", null);

        return ("executa bloco condicional", null);
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    private static string ThrowAction(ExpressionSyntax thrown, SemanticModel model)
    {
        var type = thrown is BaseObjectCreationExpressionSyntax ? model.GetTypeInfo(thrown).Type?.Name : null;
        return type is null ? $"throw {thrown.Compact()}" : $"throw {type}";
    }

    private static string ReturnSummary(ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax inv => inv.Expression is MemberAccessExpressionSyntax ma ? ma.Name.Identifier.Text : inv.Expression.Compact(),
        BaseObjectCreationExpressionSyntax creation => creation.Compact(),
        _ => expression.Compact(),
    };

    /// <summary>Primeira string literal, interpolada ou constante encontrada na expressão.</summary>
    private static string? MessageOf(SyntaxNode expression, SemanticModel model)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return literal.Token.ValueText;
                case InterpolatedStringExpressionSyntax interpolated:
                    return string.Concat(interpolated.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : c.ToString()));
                case MemberAccessExpressionSyntax member when model.GetConstantValue(member).Value is string constant:
                    return constant;
            }
        }

        return null;
    }

    private static SyntaxNode StatementOf(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() ?? node;

    private static ConditionInfo Create(AnalyzedMethod method, string kind, string expression, string action, string? message, SyntaxNode code) => new()
    {
        Kind = kind,
        Expression = expression,
        Action = action,
        Message = message,
        Method = method.DisplayName,
        SourceFile = method.SourceFile,
        SourceLine = code.StartLine(),
        Code = code.Snippet(),
    };
}
