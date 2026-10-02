using EndpointAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Descobre as condições que precisam ser verdadeiras para um trecho de código executar:
/// if/else, switch, ternário, &&/||, ?., ?? e guardas anteriores (if (...) throw/return).
/// </summary>
public static class SyntaxConditions
{
    public static string? GetEnclosingCondition(SyntaxNode node, SyntaxNode stopAt)
    {
        var parts = new List<string>();
        var child = node;

        for (var current = node.Parent; current is not null && child != stopAt; child = current, current = current.Parent)
        {
            foreach (var (text, _) in PartsAt(current, child)) parts.Insert(0, text);
            if (current == stopAt) break;
        }

        return parts.Count == 0 ? null : string.Join(" && ", parts.Select(Wrap));
    }

    /// <summary>
    /// Estruturas de controle entre <paramref name="node"/> e <paramref name="stopAt"/>, da mais externa para a mais
    /// interna. Mesmo percurso de <see cref="GetEnclosingCondition"/>, mas preserva qual estrutura e qual ramo.
    /// </summary>
    public static List<ControlStep>? GetControlPath(SyntaxNode node, SyntaxNode stopAt)
    {
        var steps = new List<ControlStep>();
        var child = node;

        for (var current = node.Parent; current is not null && child != stopAt; child = current, current = current.Parent)
        {
            foreach (var step in StepsAt(current, child)) steps.Insert(0, step);
            if (current == stopAt) break;
        }

        return steps.Count == 0 ? null : steps;
    }

    private const string Yes = "sim", No = "não";

    private static ControlStep Step(string kind, SyntaxNode structure, string expression, string branch, string? exit = null)
    {
        var lines = structure.GetLocation().GetLineSpan();
        return new ControlStep
        {
            Kind = kind,
            Key = $"{kind}@{structure.SpanStart}:{structure.Span.Length}",
            Expression = expression,
            Branch = branch,
            Exit = exit,
            Line = lines.StartLinePosition.Line + 1,
            EndLine = lines.EndLinePosition.Line + 1,
        };
    }

    /// <summary>Como <see cref="PartsAt"/>: guardas saem da mais próxima para a mais distante.</summary>
    private static IEnumerable<ControlStep> StepsAt(SyntaxNode current, SyntaxNode child)
    {
        switch (current)
        {
            case IfStatementSyntax ifs when child == ifs.Statement:
                yield return Step(ControlKinds.If, ifs, ifs.Condition.Compact(), Yes);
                break;

            case IfStatementSyntax ifs when child == ifs.Else:
                yield return Step(ControlKinds.If, ifs, ifs.Condition.Compact(), No);
                break;

            case BlockSyntax block when child is StatementSyntax statement:
                // Guarda: o if é o mesmo (mesma chave) de uma chamada dentro dele; aqui o ramo é o "não".
                var index = block.Statements.IndexOf(statement);
                for (var i = index - 1; i >= 0; i--)
                {
                    if (block.Statements[i] is IfStatementSyntax guard && guard.Else is null && AlwaysExits(guard.Statement))
                        yield return Step(ControlKinds.If, guard, guard.Condition.Compact(), No, ExitOf(guard.Statement));
                }
                break;

            case SwitchSectionSyntax section when current.Parent is SwitchStatementSyntax sw:
                var labels = section.Labels.Select(label => label switch
                {
                    CaseSwitchLabelSyntax c => c.Value.Compact(),
                    CasePatternSwitchLabelSyntax p => p.Pattern.Compact() + (p.WhenClause is null ? "" : $" when {p.WhenClause.Condition.Compact()}"),
                    _ => "default",
                });
                yield return Step(ControlKinds.Switch, sw, sw.Expression.Compact(), string.Join(", ", labels));
                break;

            case SwitchExpressionArmSyntax arm when current.Parent is SwitchExpressionSyntax se && child != arm.Pattern:
                var armLabel = arm.Pattern is DiscardPatternSyntax ? "default" : arm.Pattern.Compact();
                if (arm.WhenClause is not null) armLabel += $" when {arm.WhenClause.Condition.Compact()}";
                yield return Step(ControlKinds.Switch, se, se.GoverningExpression.Compact(), armLabel);
                break;

            case ConditionalExpressionSyntax c when child == c.WhenTrue:
                yield return Step(ControlKinds.Ternary, c, c.Condition.Compact(), Yes);
                break;

            case ConditionalExpressionSyntax c when child == c.WhenFalse:
                yield return Step(ControlKinds.Ternary, c, c.Condition.Compact(), No);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalAndExpression):
                yield return Step(ControlKinds.And, b, b.Left.Compact(), Yes);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalOrExpression):
                yield return Step(ControlKinds.Or, b, b.Left.Compact(), No);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.CoalesceExpression):
                yield return Step(ControlKinds.Coalesce, b, $"{b.Left.Compact()} == null", Yes);
                break;

            case ConditionalAccessExpressionSyntax ca when child == ca.WhenNotNull:
                yield return Step(ControlKinds.NullCheck, ca, $"{ca.Expression.Compact()} != null", Yes);
                break;

            case CatchClauseSyntax cc when cc.Parent is TryStatementSyntax tryStatement:
                yield return Step(ControlKinds.Catch, tryStatement, "exceção lançada", cc.Declaration?.Type.Compact() ?? "Exception");
                break;

            case WhileStatementSyntax w when child == w.Statement:
                yield return Step(ControlKinds.While, w, w.Condition.Compact(), Yes);
                break;

            case DoStatementSyntax d when child == d.Statement:
                yield return Step(ControlKinds.DoWhile, d, d.Condition.Compact(), Yes);
                break;

            case ForStatementSyntax f when child == f.Statement:
                yield return Step(ControlKinds.For, f, f.Condition?.Compact() ?? "sempre", Yes);
                break;

            case ForEachStatementSyntax f when child == f.Statement:
                yield return Step(ControlKinds.ForEach, f, $"{f.Identifier.Text} em {f.Expression.Compact()}", Yes);
                break;
        }
    }

    /// <summary>Como uma guarda sai: throw, return, continue ou break (a última instrução do bloco).</summary>
    private static string? ExitOf(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax => "throw",
        ReturnStatementSyntax => "return",
        ContinueStatementSyntax => "continue",
        BreakStatementSyntax => "break",
        BlockSyntax block => block.Statements.LastOrDefault() is { } last ? ExitOf(last) : null,
        _ => null,
    };

    /// <summary>
    /// Onde nasce cada parte atômica de condição dentro de <paramref name="scope"/>, no mesmo formato do registro
    /// de condições ("x != null", "(a || b)"), com o trecho que a gerou: a condição do if, do ternário, o ?. etc.
    /// </summary>
    public static IEnumerable<(string Atom, TextSpan Origin)> Origins(SyntaxNode scope)
    {
        foreach (var child in scope.DescendantNodes())
            foreach (var (text, origin) in PartsAt(child.Parent!, child))
                foreach (var atom in SplitConjunction(Wrap(text)))
                    yield return (atom, origin);
    }

    /// <summary>
    /// Partes da condição que valem para <paramref name="child"/> por estar dentro de <paramref name="current"/>.
    /// Guardas saem da mais próxima para a mais distante (quem monta a condição insere cada uma no início).
    /// </summary>
    private static IEnumerable<(string Text, TextSpan Origin)> PartsAt(SyntaxNode current, SyntaxNode child)
    {
        switch (current)
        {
            case IfStatementSyntax ifs when child == ifs.Statement:
                yield return (ifs.Condition.Compact(), ifs.Condition.Span);
                break;

            case IfStatementSyntax ifs when child == ifs.Else:
                yield return (Negate(ifs.Condition), ifs.Condition.Span);
                break;

            case BlockSyntax block when child is StatementSyntax statement:
                // Guardas anteriores no mesmo bloco: if (x) throw/return; → aqui vale !(x).
                var index = block.Statements.IndexOf(statement);
                for (var i = index - 1; i >= 0; i--)
                {
                    if (block.Statements[i] is IfStatementSyntax guard && guard.Else is null && AlwaysExits(guard.Statement))
                        yield return (Negate(guard.Condition), guard.Condition.Span);
                }
                break;

            case SwitchSectionSyntax section when current.Parent is SwitchStatementSyntax sw:
                yield return (SectionCondition(sw.Expression, section), TextSpan.FromBounds(section.Labels.First().SpanStart, section.Labels.Last().Span.End));
                break;

            case SwitchExpressionArmSyntax arm when current.Parent is SwitchExpressionSyntax se && child != arm.Pattern:
                var armCondition = arm.Pattern is DiscardPatternSyntax ? $"{se.GoverningExpression.Compact()} (demais casos)" : $"{se.GoverningExpression.Compact()} is {arm.Pattern.Compact()}";
                if (arm.WhenClause is not null) armCondition += $" && {arm.WhenClause.Condition.Compact()}";
                yield return (armCondition, TextSpan.FromBounds(arm.Pattern.SpanStart, ((SyntaxNode?)arm.WhenClause ?? arm.Pattern).Span.End));
                break;

            case ConditionalExpressionSyntax c when child == c.WhenTrue:
                yield return (c.Condition.Compact(), c.Condition.Span);
                break;

            case ConditionalExpressionSyntax c when child == c.WhenFalse:
                yield return (Negate(c.Condition), c.Condition.Span);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalAndExpression):
                yield return (b.Left.Compact(), b.Left.Span);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalOrExpression):
                yield return (Negate(b.Left), b.Left.Span);
                break;

            case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.CoalesceExpression):
                yield return ($"{b.Left.Compact()} == null", b.Left.Span);
                break;

            case ConditionalAccessExpressionSyntax ca when child == ca.WhenNotNull:
                yield return ($"{ca.Expression.Compact()} != null", ca.Expression.Span);
                break;

            case CatchClauseSyntax cc:
                yield return ($"exceção capturada: {cc.Declaration?.Type.Compact() ?? "Exception"}",
                    TextSpan.FromBounds(cc.CatchKeyword.SpanStart, cc.Declaration?.Span.End ?? cc.CatchKeyword.Span.End));
                break;

            case WhileStatementSyntax w when child == w.Statement:
                yield return ($"enquanto {w.Condition.Compact()}", w.Condition.Span);
                break;

            case ForEachStatementSyntax f when child == f.Statement:
                yield return ($"para cada {f.Identifier.Text} em {f.Expression.Compact()}", TextSpan.FromBounds(f.OpenParenToken.SpanStart, f.CloseParenToken.Span.End));
                break;
        }
    }

    /// <summary>
    /// Junta condições (a do caminho e a local) como uma conjunção plana, sem repetir partes:
    /// "a && b" + "b && c" → "a && b && c". Só os átomos com || ganham parênteses.
    /// </summary>
    public static string? Combine(params string?[] conditions)
    {
        var parts = conditions
            .SelectMany(SplitConjunction)
            .Select(Wrap)
            .Distinct()
            .ToList();
        return parts.Count == 0 ? null : string.Join(" && ", parts);
    }

    public static string Negate(ExpressionSyntax condition)
    {
        condition = Unparenthesize(condition);
        return condition switch
        {
            PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression) => u.Operand.Compact(),
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) => $"{b.Left.Compact()} != {b.Right.Compact()}",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression) => $"{b.Left.Compact()} == {b.Right.Compact()}",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LessThanExpression) => $"{b.Left.Compact()} >= {b.Right.Compact()}",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LessThanOrEqualExpression) => $"{b.Left.Compact()} > {b.Right.Compact()}",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.GreaterThanExpression) => $"{b.Left.Compact()} <= {b.Right.Compact()}",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.GreaterThanOrEqualExpression) => $"{b.Left.Compact()} < {b.Right.Compact()}",
            IsPatternExpressionSyntax p when p.Pattern is UnaryPatternSyntax { Pattern: var inner } up && up.IsKind(SyntaxKind.NotPattern) => $"{p.Expression.Compact()} is {inner.Compact()}",
            IsPatternExpressionSyntax p => $"{p.Expression.Compact()} is not {p.Pattern.Compact()}",
            // De Morgan: !(a && b) → (!a || !b); !(a || b) → !a && !b
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => $"({Negate(b.Left)} || {Negate(b.Right)})",
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => $"{WrapOr(Negate(b.Left))} && {WrapOr(Negate(b.Right))}",
            InvocationExpressionSyntax or MemberAccessExpressionSyntax or IdentifierNameSyntax or ElementAccessExpressionSyntax
                => $"!{condition.Compact()}",
            _ => $"!({condition.Compact()})",
        };
    }

    private static string WrapOr(string condition) => Wrap(condition);

    /// <summary>O bloco sempre termina com throw/return (é uma guarda).</summary>
    public static bool AlwaysExits(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax or ReturnStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax => true,
        BlockSyntax block => block.Statements.LastOrDefault() is { } last && AlwaysExits(last),
        _ => false,
    };

    public static string SectionCondition(ExpressionSyntax governing, SwitchSectionSyntax section)
    {
        var labels = section.Labels.Select(label => label switch
        {
            CaseSwitchLabelSyntax c => $"{governing.Compact()} == {c.Value.Compact()}",
            CasePatternSwitchLabelSyntax p => $"{governing.Compact()} is {p.Pattern.Compact()}" + (p.WhenClause is null ? "" : $" && {p.WhenClause.Condition.Compact()}"),
            _ => DefaultCondition(governing, section),
        });
        return string.Join(" || ", labels);
    }

    /// <summary>default: → o valor não é nenhum dos outros cases.</summary>
    private static string DefaultCondition(ExpressionSyntax governing, SwitchSectionSyntax section)
    {
        if (section.Parent is not SwitchStatementSyntax sw) return $"{governing.Compact()} (demais casos)";

        var others = sw.Sections.Where(s => s != section)
            .SelectMany(s => s.Labels)
            .Select(label => label switch
            {
                CaseSwitchLabelSyntax c => $"{governing.Compact()} != {c.Value.Compact()}",
                CasePatternSwitchLabelSyntax { WhenClause: null } p => $"{governing.Compact()} is not {p.Pattern.Compact()}",
                _ => null,
            })
            .ToList();

        return others.Count == 0 || others.Contains(null)
            ? $"{governing.Compact()} (demais casos)"
            : string.Join(" && ", others);
    }

    /// <summary>Divide "a && (b || c) && d" nas partes de primeiro nível: [a, (b || c), d].</summary>
    public static IReadOnlyList<string> SplitConjunction(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return [];

        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < condition.Length; i++)
        {
            var c = condition[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(condition, i, " && ", 0, 4) == 0)
            {
                parts.Add(condition[start..i].Trim());
                start = i + 4;
                i += 3;
            }
        }
        parts.Add(condition[start..].Trim());
        return parts;
    }

    /// <summary>Partes comuns a todas as condições (o que vale para a operação como um todo).</summary>
    public static string? Common(IEnumerable<string?> conditions)
    {
        var lists = conditions.Select(SplitConjunction).ToList();
        if (lists.Count == 0 || lists.Any(l => l.Count == 0)) return null;

        var common = lists[0].Where(part => lists.All(l => l.Contains(part))).ToList();
        return common.Count == 0 ? null : string.Join(" && ", common);
    }

    private static ExpressionSyntax Unparenthesize(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    /// <summary>Parênteses só quando há || no primeiro nível: "a || b" → "(a || b)"; "(a || b)" fica igual.</summary>
    private static string Wrap(string condition) => HasTopLevelOr(condition) ? $"({condition})" : condition;

    private static bool HasTopLevelOr(string condition)
    {
        var depth = 0;
        for (var i = 0; i < condition.Length - 1; i++)
        {
            var c = condition[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && c == '|' && condition[i + 1] == '|') return true;
        }
        return false;
    }
}
