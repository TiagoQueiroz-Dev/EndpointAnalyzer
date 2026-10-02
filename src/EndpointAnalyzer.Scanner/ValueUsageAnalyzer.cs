using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

public static class UsageKinds
{
    public const string Condition = "condição";
    public const string Return = "retorno";
    public const string Throw = "exceção";
    public const string Assignment = "atribuição";
    public const string Argument = "argumento";
    public const string Iteration = "iteração";
    public const string Command = "comando";
    public const string Discarded = "descartado";

    /// <summary>Usos que fazem o resultado influenciar uma decisão, o retorno ou um efeito.</summary>
    public static readonly HashSet<string> Influential = [Condition, Return, Throw, Assignment, Argument, Iteration];
}

/// <summary>
/// Data flow simplificado (§10): para onde vai o valor produzido por uma chamada.
/// Ex.: var xUnidadeEhTransportadora = _unidadeService.EhUnidadeTransportadora(...); if (!xUnidadeEhTransportadora) throw ...
/// → o resultado participa de uma condição, então a consulta sustenta a regra.
/// </summary>
public static class ValueUsageAnalyzer
{
    public static IReadOnlyList<string> Analyze(SyntaxNode call, SemanticModel model, SyntaxNode scope)
    {
        var usages = new HashSet<string>();
        Classify(call, model, scope, usages, depth: 0);
        return usages.Count == 0 ? [UsageKinds.Discarded] : usages.ToList();
    }

    private static void Classify(SyntaxNode value, SemanticModel model, SyntaxNode scope, HashSet<string> usages, int depth)
    {
        var child = value;
        for (var current = value.Parent; current is not null && child != scope; child = current, current = current.Parent)
        {
            switch (current)
            {
                case ArgumentSyntax:
                    usages.Add(UsageKinds.Argument);
                    break;

                case IfStatementSyntax ifs when child == ifs.Condition:
                case WhileStatementSyntax w when child == w.Condition:
                case DoStatementSyntax d when child == d.Condition:
                case ConditionalExpressionSyntax c when child == c.Condition:
                case SwitchStatementSyntax sw when child == sw.Expression:
                case SwitchExpressionSyntax se when child == se.GoverningExpression:
                case WhenClauseSyntax:
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) && child == b.Left:
                    usages.Add(UsageKinds.Condition);
                    return;

                case ForEachStatementSyntax f when child == f.Expression:
                    usages.Add(UsageKinds.Iteration);
                    return;

                case ReturnStatementSyntax:
                case ArrowExpressionClauseSyntax when current.Parent is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax:
                    usages.Add(UsageKinds.Return);
                    return;

                case ThrowStatementSyntax or ThrowExpressionSyntax:
                    usages.Add(UsageKinds.Throw);
                    return;

                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }:
                    if (model.GetDeclaredSymbol(declarator) is ILocalSymbol local)
                        FollowLocal(local, model, scope, usages, depth);
                    return;

                case AssignmentExpressionSyntax assignment when child == assignment.Right:
                    if (model.GetSymbolInfo(assignment.Left).Symbol is ILocalSymbol assignedLocal)
                        FollowLocal(assignedLocal, model, scope, usages, depth);
                    else
                        usages.Add(UsageKinds.Assignment);
                    return;

                case InitializerExpressionSyntax:
                    usages.Add(UsageKinds.Assignment);
                    return;

                case ExpressionStatementSyntax:
                    if (usages.Count == 0) usages.Add(UsageKinds.Command);
                    return;

                case StatementSyntax:
                    return;
            }
        }
    }

    /// <summary>Segue as leituras da variável local que recebeu o valor.</summary>
    private static void FollowLocal(ILocalSymbol local, SemanticModel model, SyntaxNode scope, HashSet<string> usages, int depth)
    {
        if (depth >= 3) return;

        var references = scope.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.Text == local.Name && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(i).Symbol, local))
            .Where(i => !(i.Parent is AssignmentExpressionSyntax a && a.Left == i))
            .ToList();

        if (references.Count == 0)
        {
            usages.Add(UsageKinds.Discarded);
            return;
        }

        foreach (var reference in references)
            Classify(reference, model, scope, usages, depth + 1);
    }
}
