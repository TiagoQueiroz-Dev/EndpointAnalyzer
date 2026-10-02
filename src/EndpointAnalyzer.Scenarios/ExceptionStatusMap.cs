using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Status HTTP de cada exceção no tratamento global (middleware, filtro, UseExceptionHandler): procura no código
/// <c>NaoEncontradoException =&gt; StatusCodes.Status404NotFound</c>, <c>case X:</c>, <c>catch (X)</c> e <c>if (ex is X)</c>
/// junto de um status (StatusCodes.StatusNNN, HttpStatusCode.X ou número atribuído a StatusCode).
/// </summary>
public sealed class ExceptionStatusMap
{
    public sealed record Mapping(int Status, string Source);

    private readonly Dictionary<string, Mapping> _map = [];

    /// <summary>Enums no JSON como texto: JsonStringEnumConverter registrado.</summary>
    public bool EnumsAsStrings { get; private set; }

    /// <summary>Validação automática do FluentValidation registrada (AddFluentValidationAutoValidation).</summary>
    public bool FluentAutoValidation { get; private set; }

    public static async Task<ExceptionStatusMap> BuildAsync(LoadedSolution loaded, CancellationToken cancellationToken)
    {
        var map = new ExceptionStatusMap();
        foreach (var project in loaded.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (tree.FilePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                var root = await tree.GetRootAsync(cancellationToken);
                var text = root.ToString();
                if (text.Contains("JsonStringEnumConverter", StringComparison.Ordinal) || text.Contains("StringEnumConverter", StringComparison.Ordinal))
                    map.EnumsAsStrings = true;
                if (text.Contains("AddFluentValidationAutoValidation", StringComparison.Ordinal) || text.Contains("AddFluentValidation(", StringComparison.Ordinal)
                    || text.Contains("FluentValidationAutoValidation", StringComparison.Ordinal))
                    map.FluentAutoValidation = true;
                if (!text.Contains("Exception", StringComparison.Ordinal) || !(text.Contains("StatusCode", StringComparison.Ordinal) || text.Contains("HttpStatusCode", StringComparison.Ordinal)))
                    continue;

                var model = compilation.GetSemanticModel(tree);
                string Where(SyntaxNode n) => $"{loaded.RelativePath(tree.FilePath)}:{n.StartLine()}";

                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case SwitchExpressionArmSyntax arm when ExceptionType(arm.Pattern, model) is { } type && Status(arm.Expression, model) is { } status:
                            map.Add(type, status, Where(arm));
                            break;
                        case SwitchSectionSyntax section:
                            foreach (var label in section.Labels.OfType<CasePatternSwitchLabelSyntax>())
                                if (ExceptionType(label.Pattern, model) is { } caseType && section.Statements.Select(s => Status(s, model)).FirstOrDefault(s => s is not null) is { } caseStatus)
                                    map.Add(caseType, caseStatus, Where(section));
                            break;
                        case CatchClauseSyntax { Declaration: { } declaration } catchClause
                            when model.GetTypeInfo(declaration.Type).Type is INamedTypeSymbol caught && IsException(caught)
                                && Status(catchClause.Block, model) is { } catchStatus:
                            map.Add(caught, catchStatus, Where(catchClause));
                            break;
                        case IfStatementSyntax { Condition: IsPatternExpressionSyntax isPattern } ifs
                            when ExceptionType(isPattern.Pattern, model) is { } ifType && Status(ifs.Statement, model) is { } ifStatus:
                            map.Add(ifType, ifStatus, Where(ifs));
                            break;
                    }
                }
            }
        }
        return map;
    }

    private void Add(INamedTypeSymbol type, int status, string source)
    {
        // O primeiro mapeamento encontrado vale (o mais específico costuma vir antes no switch).
        _map.TryAdd(type.OriginalDefinition.ToDisplayString(), new Mapping(status, source));
    }

    /// <summary>Mapeamento da exceção ou da classe base mais próxima (sem chegar em System.Exception).</summary>
    public Mapping? Find(ITypeSymbol type)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System") break;
            if (_map.TryGetValue(t.OriginalDefinition.ToDisplayString(), out var mapping)) return mapping;
        }
        return null;
    }

    private static INamedTypeSymbol? ExceptionType(PatternSyntax pattern, SemanticModel model)
    {
        var typeSyntax = pattern switch
        {
            DeclarationPatternSyntax d => d.Type,
            TypePatternSyntax t => t.Type,
            RecursivePatternSyntax { Type: { } r } => r,
            ConstantPatternSyntax { Expression: TypeSyntax c } => c,
            ConstantPatternSyntax { Expression: IdentifierNameSyntax or QualifiedNameSyntax or MemberAccessExpressionSyntax } c => SyntaxFactory.ParseTypeName(c.Expression.ToString()),
            _ => null,
        };
        if (typeSyntax is null) return null;
        var type = typeSyntax.SyntaxTree == model.SyntaxTree ? model.GetTypeInfo(typeSyntax).Type ?? model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol : null;
        if (type is null && pattern is ConstantPatternSyntax constant)
            type = model.GetSymbolInfo(constant.Expression).Symbol as ITypeSymbol;
        return type is INamedTypeSymbol named && IsException(named) && named.Name != "Exception" ? named : null;
    }

    private static bool IsException(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System") return true;
        return false;
    }

    /// <summary>Primeiro status HTTP do trecho: StatusCodes.StatusNNN, HttpStatusCode.X ou número atribuído a StatusCode.</summary>
    private static int? Status(SyntaxNode node, SemanticModel model)
    {
        foreach (var e in node.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            switch (e)
            {
                case MemberAccessExpressionSyntax ma when ma.Expression.ToString().EndsWith("StatusCodes", StringComparison.Ordinal)
                    && model.GetConstantValue(ma) is { HasValue: true, Value: int code } && code is >= 100 and < 600:
                    return code;
                case MemberAccessExpressionSyntax ma when ma.Expression.ToString().EndsWith("HttpStatusCode", StringComparison.Ordinal)
                    && model.GetConstantValue(ma) is { HasValue: true, Value: { } value } && Convert.ToInt32(value) is >= 100 and < 600 and var http:
                    return http;
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) && literal.Token.Value is int number
                    && number is >= 100 and < 600 && StatusContext(literal):
                    return number;
            }
        }
        return null;
    }

    /// <summary>Número num lugar de status: StatusCode = 404, StatusCode(404), statusCode: 404.</summary>
    private static bool StatusContext(SyntaxNode literal)
    {
        for (var p = literal.Parent; p is not null && p is not StatementSyntax; p = p.Parent)
        {
            switch (p)
            {
                case AssignmentExpressionSyntax a when a.Left.ToString().EndsWith("StatusCode", StringComparison.Ordinal): return true;
                case ArgumentSyntax { NameColon.Name.Identifier.Text: "statusCode" }: return true;
                case InvocationExpressionSyntax inv when inv.Expression.ToString().EndsWith("StatusCode", StringComparison.Ordinal): return true;
                case SwitchExpressionArmSyntax: return true;
            }
        }
        return false;
    }
}
