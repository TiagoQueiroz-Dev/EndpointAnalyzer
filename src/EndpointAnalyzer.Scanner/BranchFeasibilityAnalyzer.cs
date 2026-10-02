using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Trechos do método que não podem executar no contexto conhecido.
/// </summary>
public sealed class Reachability
{
    public static readonly Reachability All = new([]);

    private readonly List<(TextSpan Span, string Reason)> _unreachable;

    public Reachability(List<(TextSpan Span, string Reason)> unreachable) => _unreachable = unreachable;

    public int PrunedBranches => _unreachable.Count;

    public IReadOnlyList<string> Reasons => _unreachable.Select(u => u.Reason).ToList();

    public bool IsReachable(SyntaxNode node) => !_unreachable.Any(u => u.Span.Contains(node.Span));
}

/// <summary>
/// Elimina branches impossíveis com os tipos e valores propagados (§8):
/// type checks impossíveis (pEvent é DomainNotification → case ProgramacaoTransporteEvent nunca casa),
/// null checks conhecidos, enums e booleans conhecidos, pattern matching incompatível.
/// </summary>
public static class BranchFeasibilityAnalyzer
{
    public static Reachability Analyze(SyntaxNode declaration, SemanticModel model, AnalysisExecutionContext context)
    {
        if (context.Arguments.Count == 0 && context.Values.Count == 0 && context.Generics.IsEmpty)
            return Reachability.All;

        var unreachable = new List<(TextSpan, string)>();
        var evaluator = new Evaluator(model, context, declaration);

        foreach (var node in declaration.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax ifs:
                    var result = evaluator.Evaluate(ifs.Condition);
                    if (result == false)
                        unreachable.Add((ifs.Statement.Span, $"if ({ifs.Condition.Compact()}) é sempre falso"));
                    else if (result == true && ifs.Else is not null)
                        unreachable.Add((ifs.Else.Span, $"else de if ({ifs.Condition.Compact()}) nunca executa"));
                    break;

                case ConditionalExpressionSyntax c:
                    var conditional = evaluator.Evaluate(c.Condition);
                    if (conditional == false) unreachable.Add((c.WhenTrue.Span, $"{c.Condition.Compact()} é sempre falso"));
                    if (conditional == true) unreachable.Add((c.WhenFalse.Span, $"{c.Condition.Compact()} é sempre verdadeiro"));
                    break;

                case SwitchStatementSyntax sw:
                    AnalyzeSwitch(sw, evaluator, unreachable);
                    break;

                case SwitchExpressionSyntax se:
                    var matched = false;
                    foreach (var arm in se.Arms)
                    {
                        var armResult = matched ? false : evaluator.Matches(se.GoverningExpression, arm.Pattern, arm.WhenClause);
                        if (armResult == false)
                            unreachable.Add((arm.Expression.Span, $"{se.GoverningExpression.Compact()} nunca é {arm.Pattern.Compact()}"));
                        if (armResult == true) matched = true;
                    }
                    break;
            }
        }

        return new Reachability(unreachable);
    }

    private static void AnalyzeSwitch(SwitchStatementSyntax sw, Evaluator evaluator, List<(TextSpan, string)> unreachable)
    {
        var matched = false;
        SwitchSectionSyntax? defaultSection = null;

        foreach (var section in sw.Sections)
        {
            bool? sectionResult = false;
            foreach (var label in section.Labels)
            {
                bool? labelResult = label switch
                {
                    DefaultSwitchLabelSyntax => null,
                    CaseSwitchLabelSyntax c => matched ? false : evaluator.Equal(sw.Expression, c.Value),
                    CasePatternSwitchLabelSyntax p => matched ? false : evaluator.Matches(sw.Expression, p.Pattern, p.WhenClause),
                    _ => null,
                };
                if (label is DefaultSwitchLabelSyntax) defaultSection = section;
                sectionResult = Or(sectionResult, labelResult);
            }

            if (section != defaultSection && sectionResult == false)
                unreachable.Add((section.Span, $"{sw.Expression.Compact()} nunca casa com {string.Join(", ", section.Labels.Select(l => l.Compact()))}"));
            if (sectionResult == true) matched = true;
        }

        if (matched && defaultSection is not null)
            unreachable.Add((defaultSection.Span, $"default de switch ({sw.Expression.Compact()}) nunca executa"));
    }

    private static bool? Or(bool? a, bool? b) => a == true || b == true ? true : a == false && b == false ? false : null;

    private sealed class Evaluator(SemanticModel model, AnalysisExecutionContext context, SyntaxNode scope)
    {
        /// <summary>true/false quando o valor é conhecido; null quando depende de dados em tempo de execução.</summary>
        public bool? Evaluate(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax p:
                    return Evaluate(p.Expression);

                case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                    return Evaluate(u.Operand) is { } value ? !value : null;

                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression):
                    var leftAnd = Evaluate(b.Left);
                    var rightAnd = Evaluate(b.Right);
                    return leftAnd == false || rightAnd == false ? false : leftAnd == true && rightAnd == true ? true : null;

                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
                    var leftOr = Evaluate(b.Left);
                    var rightOr = Evaluate(b.Right);
                    return leftOr == true || rightOr == true ? true : leftOr == false && rightOr == false ? false : null;

                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression):
                    return Equal(b.Left, b.Right);

                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression):
                    return Equal(b.Left, b.Right) is { } equal ? !equal : null;

                // x is T  /  x as T != null
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression) && model.GetTypeInfo(b.Right).Type is { } isType:
                    return IsOfType(b.Left, isType);

                case IsPatternExpressionSyntax ip:
                    return Matches(ip.Expression, ip.Pattern, null);
            }

            return context.KnownValueOf(expression, model)?.Value is bool constant ? constant : null;
        }

        public bool? Equal(ExpressionSyntax left, ExpressionSyntax right)
        {
            if (IsNull(right)) return NullCheck(left);
            if (IsNull(left)) return NullCheck(right);

            var a = context.KnownValueOf(left, model);
            var b = context.KnownValueOf(right, model);
            return a is not null && b is not null ? Equals(a.Value, b.Value) : null;
        }

        public bool? Matches(ExpressionSyntax value, PatternSyntax pattern, WhenClauseSyntax? when)
        {
            var result = MatchesPattern(value, pattern);
            // Com cláusula when só dá para afirmar o "não casa".
            return when is not null && result == true ? null : result;
        }

        private bool? MatchesPattern(ExpressionSyntax value, PatternSyntax pattern)
        {
            switch (pattern)
            {
                case DiscardPatternSyntax or VarPatternSyntax:
                    return true;

                case ConstantPatternSyntax { Expression: var constant }:
                    return IsNull(constant) ? NullCheck(value) : Equal(value, constant);

                case DeclarationPatternSyntax d when model.GetTypeInfo(d.Type).Type is { } declared:
                    return IsOfType(value, declared);

                case TypePatternSyntax t when model.GetTypeInfo(t.Type).Type is { } typed:
                    return IsOfType(value, typed);

                case RecursivePatternSyntax { Type: { } recursiveType } r when model.GetTypeInfo(recursiveType).Type is { } recursive:
                    var typeMatch = IsOfType(value, recursive);
                    return typeMatch == false ? false : r.PropertyPatternClause is null && r.PositionalPatternClause is null ? typeMatch : null;

                case UnaryPatternSyntax u when u.IsKind(SyntaxKind.NotPattern):
                    return MatchesPattern(value, u.Pattern) is { } inner ? !inner : null;

                case BinaryPatternSyntax b when b.IsKind(SyntaxKind.OrPattern):
                    return Or(MatchesPattern(value, b.Left), MatchesPattern(value, b.Right));

                case BinaryPatternSyntax b when b.IsKind(SyntaxKind.AndPattern):
                    var l = MatchesPattern(value, b.Left);
                    var r2 = MatchesPattern(value, b.Right);
                    return l == false || r2 == false ? false : l == true && r2 == true ? true : null;

                case ParenthesizedPatternSyntax p:
                    return MatchesPattern(value, p.Pattern);

                default:
                    return null;
            }
        }

        /// <summary>
        /// "valor is T" com o tipo conhecido do valor:
        /// subtipo → verdadeiro (se não nulo); classes sem relação de herança → falso;
        /// tipo exato (new X()) que não é T → falso.
        /// </summary>
        private bool? IsOfType(ExpressionSyntax value, ITypeSymbol target)
        {
            var known = context.KnownTypeOf(value, model, scope);
            if (known is null) return null;
            target = context.Resolve(target) ?? target;

            if (IsSubtype(known.Type, target)) return known.NonNull ? true : null;
            if (known.Exact) return false;

            var bothClasses = known.Type.TypeKind == TypeKind.Class && target.TypeKind == TypeKind.Class;
            if (bothClasses && !IsSubtype(target, known.Type)) return false;
            if (known.Type.IsSealed && target.TypeKind == TypeKind.Interface) return false;
            return null;
        }

        private bool? NullCheck(ExpressionSyntax value) =>
            context.KnownTypeOf(value, model, scope) is { NonNull: true } ? false
            : context.KnownValueOf(value, model) is { } known ? known.Value is null
            : null;

        private static bool IsNull(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression);

        private static bool IsSubtype(ITypeSymbol type, ITypeSymbol target)
        {
            var key = target.OriginalDefinition.Key();
            for (var t = type; t is not null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t, target) || t.OriginalDefinition.Key() == key) return true;
            return type.AllInterfaces.Any(i => i.OriginalDefinition.Key() == key);
        }
    }
}
