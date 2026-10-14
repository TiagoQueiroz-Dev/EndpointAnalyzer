using System.Globalization;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Fase 4: obrigações de cobertura e seleção mínima justificável de cenários.
/// <list type="number">
/// <item>Pelo menos um teste por folha viável.</item>
/// <item>Classes de equivalência com semântica distinta (null, só espaços, formato, fora do enum...): um representante por classe.</item>
/// <item>Limites que testam lados distintos de uma regra (6/7/8 para exatamente 7 caracteres).</item>
/// <item>Ramos que mudam os efeitos (verdadeiro e falso) e laços com zero, uma e várias iterações (configurável).</item>
/// </list>
/// A seleção é gulosa e determinística (ordem fixa das obrigações): antes de gerar um cenário para uma obrigação, os já
/// escolhidos são conferidos; só se nenhum a cobre um novo é gerado. Nenhum cenário nasce só por existir um Probe.
/// </summary>
internal static class CoveragePlanner
{
    public static List<CoverageTarget> Targets(FlowContext ctx, List<TerminalOutcome> leaves)
    {
        var targets = new List<CoverageTarget>();
        var principal = leaves.First(l => l is { Kind: TerminalKinds.Success, Flipped: null });
        var validations = leaves.Where(l => l.Kind == TerminalKinds.Validation).ToList();

        targets.Add(new CoverageTarget
        {
            Key = principal.Key, Kind = CoverageTargetKinds.Outcome, Label = principal.Label, Anchor = principal, Leaf = principal,
        });

        // Ramos que mudam os efeitos: o valor do caminho principal e o oposto (uma folha própria quando muda o desfecho).
        foreach (var branch in ctx.Branches)
        {
            var atom = branch is NotPred n ? n.Inner : branch;
            var value = branch is not NotPred;
            targets.Add(new CoverageTarget
            {
                Key = $"{principal.Key}|ramo:{ScenarioKey.Code(atom.Text)}={(value ? "true" : "false")}",
                Kind = CoverageTargetKinds.Branch,
                Label = $"Ramo {atom.Text} {(value ? "verdadeiro" : "falso")}",
                Anchor = principal,
                Predicate = branch,
            });
            var flip = leaves.First(l => l.Flipped?.Key == Pred.Not(branch).Key);
            targets.Add(new CoverageTarget { Key = flip.Key, Kind = CoverageTargetKinds.Branch, Label = $"Ramo {atom.Text} {(value ? "falso" : "verdadeiro")}", Anchor = flip, Leaf = flip });
        }

        foreach (var (literal, collection) in LoopLiterals(ctx))
            foreach (var n in ctx.LoopIterations.Distinct().Order())
            {
                var count = new CmpPred(new VarTerm(collection, Measure.Length), "==", ConstTerm.Of(n), $"{collection.Display}.Count == {n}");
                targets.Add(new CoverageTarget
                {
                    Key = $"{principal.Key}|laco:{ScenarioKey.Code(literal.Text)}={n}",
                    Kind = CoverageTargetKinds.Loop,
                    Label = $"{Name(collection)} com {n} item(ns): {Iterations(n)} do laço",
                    Anchor = principal,
                    Predicate = collection.Nullable ? Pred.And(Pred.Not(new NullPred(collection, $"{collection.Display} == null")), count) : count,
                    Iterations = n,
                });
            }

        foreach (var exit in leaves.Where(l => l.Kind == TerminalKinds.Exit))
            targets.Add(new CoverageTarget { Key = exit.Key, Kind = CoverageTargetKinds.Outcome, Label = exit.Label, Anchor = exit, Leaf = exit });

        // Validações: as classes primeiro (cada uma já atinge a folha); a obrigação do desfecho só gera cenário se nenhuma classe o fez.
        foreach (var leaf in validations)
        {
            foreach (var probe in Representatives(leaf.Validation!))
                targets.Add(new CoverageTarget
                {
                    Key = $"{leaf.Key}|classe:{probe.Class}",
                    Kind = probe.Technique == ScenarioTechniques.BoundaryValue ? CoverageTargetKinds.Boundary : CoverageTargetKinds.Class,
                    Label = probe.Label,
                    Anchor = leaf,
                    Leaf = leaf,
                    Predicate = probe.Constraint,
                    Probe = probe,
                });
            targets.Add(new CoverageTarget { Key = leaf.Key, Kind = CoverageTargetKinds.Outcome, Label = leaf.Label, Anchor = leaf, Leaf = leaf });
        }

        foreach (var binding in leaves.Where(l => l.Kind == TerminalKinds.Binding))
            targets.Add(new CoverageTarget { Key = binding.Key, Kind = CoverageTargetKinds.Class, Label = binding.Label, Anchor = binding, Leaf = binding, Field = binding.BindingField });

        foreach (var field in ValidationRuleExtractor.RequiredWithoutEffect(ctx.Input))
        {
            var v = ctx.Vars.Input(field);
            if (v.Kind is not (VarKind.Int or VarKind.Decimal or VarKind.Enum or VarKind.Guid)
                || SymbolicResolver.DefaultOf(field.Type) is not { Number: { } number } value) continue;
            var shown = v.Kind == VarKind.Guid ? value.Display : number.ToString(CultureInfo.InvariantCulture);
            targets.Add(new CoverageTarget
            {
                Key = $"{CoverageTargetKinds.Default}|{field.Path}",
                Kind = CoverageTargetKinds.Default,
                Label = $"{Name(v)} ausente ou {shown}: [Required] não tem efeito em {field.TypeDisplay} não anulável",
                Anchor = principal,
                Predicate = new CmpPred(new VarTerm(v), "==", value, $"{field.Path} == {shown}"),
                Field = field,
            });
        }

        foreach (var leaf in leaves.Where(l => l.Kind == TerminalKinds.Rule))
        {
            targets.Add(new CoverageTarget { Key = leaf.Key, Kind = CoverageTargetKinds.Outcome, Label = $"Regra: {leaf.Label}", Anchor = leaf, Leaf = leaf });
            foreach (var rule in leaf.Rules.Take(1))
                if (RuleBoundary(rule, valid: false) is { } invalid)
                    targets.Add(new CoverageTarget
                    {
                        Key = $"{leaf.Key}|limite:dispara", Kind = CoverageTargetKinds.Boundary,
                        Label = $"{invalid.Name} = {invalid.Label} (limite que dispara {rule.TriggerCode})",
                        Anchor = leaf, Leaf = leaf, Predicate = invalid.Predicate, Rule = rule,
                    });
        }

        // Limites válidos: qualquer resposta de sucesso serve (o caminho principal costuma cobrir um deles).
        foreach (var leaf in validations)
            foreach (var probe in leaf.Validation!.Probes().Where(p => p.Valid))
                targets.Add(new CoverageTarget
                {
                    Key = $"{leaf.Key}|limite:{probe.Class}", Kind = CoverageTargetKinds.Boundary, Label = probe.Label,
                    Anchor = leaf, Predicate = probe.Constraint, Probe = probe,
                });
        foreach (var leaf in leaves.Where(l => l.Kind == TerminalKinds.Rule))
            foreach (var rule in leaf.Rules.Take(1))
                if (RuleBoundary(rule, valid: true) is { } valid)
                    targets.Add(new CoverageTarget
                    {
                        Key = $"{leaf.Key}|limite:passa", Kind = CoverageTargetKinds.Boundary,
                        Label = $"{valid.Name} = {valid.Label} (limite de {rule.TriggerCode})",
                        Anchor = leaf, Predicate = valid.Predicate, Rule = rule,
                    });

        for (var i = 0; i < targets.Count; i++) targets[i].Id = $"O{i + 1}";
        return targets;
    }

    /// <summary>
    /// Classes inválidas que exercitam mecanismos distintos. "" e "   " violam a mesma checagem (IsNullOrWhiteSpace):
    /// só espaços representa as duas, porque também distingue IsNullOrWhiteSpace de IsNullOrEmpty.
    /// </summary>
    public static IEnumerable<Probe> Representatives(ValidationRule rule)
    {
        var invalid = rule.Probes().Where(p => !p.Valid).ToList();
        if (invalid.Any(p => p.Class == "espacos")) invalid.RemoveAll(p => p.Class == "vazio");
        return invalid;
    }

    /// <summary>
    /// Seleção gulosa: para cada obrigação, na ordem, um cenário já escolhido que a cubra basta; senão, gera um (que
    /// precisa atingir a folha-alvo). No limite de cenários, o restante fica explícito como não coberto.
    /// </summary>
    public static bool Select<T>(List<CoverageTarget> targets, List<T> selected, int max, Func<T, CoverageTarget, bool> covers,
        Func<CoverageTarget, (T? Item, string? Status, string? Reason)> generate) where T : class
    {
        var truncated = false;
        foreach (var target in targets)
        {
            // Já decidida antes da seleção (inviável, equivalente ou pendente por limitação do modelo).
            if (target.Status is CoverageStatuses.Infeasible or CoverageStatuses.Equivalent or CoverageStatuses.Pending) continue;
            if (selected.Any(s => covers(s, target)))
            {
                target.Status = CoverageStatuses.Covered;
                continue;
            }
            if (selected.Count >= max)
            {
                truncated = true;
                target.Status = CoverageStatuses.Truncated;
                target.Reason = $"limite de {max} cenários atingido";
                continue;
            }
            var (item, status, reason) = generate(target);
            if (item is null)
            {
                target.Status = status ?? (target.Anchor.Feasibility == Feasibility.Unknown ? CoverageStatuses.Pending : CoverageStatuses.Uncovered);
                target.Reason = reason;
                continue;
            }
            selected.Add(item);
            target.Selected = true;
            target.Status = CoverageStatuses.Covered;
        }
        return truncated;
    }

    // ---- Laços ----

    /// <summary>Laços sobre coleções do payload com efeitos no corpo: foreach (var x in request.Itens) → request.Itens.Count &gt; 0.</summary>
    private static IEnumerable<(Pred Literal, Var Collection)> LoopLiterals(FlowContext ctx) => ctx.Flow.Effects
        .SelectMany(e => Evaluator.Conjuncts(e.Site.Path))
        .Select(c => (Literal: c, Cmp: c as CmpPred))
        .Where(x => x.Cmp is { Left: VarTerm { Measure: Measure.Length, Var: { Kind: VarKind.Collection, Origin: VarOrigin.Input } }, Op: ">", Right: ConstTerm { Number: 0 } })
        .DistinctBy(x => x.Literal.Key)
        .Select(x => (x.Literal, ((VarTerm)x.Cmp!.Left).Var));

    private static string Iterations(int n) => n switch { 0 => "nenhuma iteração", 1 => "uma iteração", _ => $"{n} iterações" };

    // ---- Limites das regras do código ----

    public sealed record RuleLimit(Pred Predicate, string Name, string Label);

    /// <summary>
    /// Primeiro valor de um lado do limite de uma comparação do código com constante: x &gt; 200 → 200 (passa) e 201
    /// (dispara); x &lt; hoje → hoje (passa) e ontem (dispara).
    /// </summary>
    public static RuleLimit? RuleBoundary(CodeRule rule, bool valid)
    {
        var literal = rule.Trigger;
        var positive = literal is not NotPred;
        if ((literal is NotPred n ? n.Inner : literal) is not CmpPred { IsComplex: false } cmp) return null;

        var (variable, op, constant) = (cmp.Left, cmp.Right) switch
        {
            (VarTerm v, ConstTerm or NowTerm) => (v, cmp.Op, cmp.Right),
            (ConstTerm or NowTerm, VarTerm v) => (v, cmp.Op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => cmp.Op }, cmp.Left),
            _ => (null, null, null),
        };
        if (variable is null || variable.Var.Origin != VarOrigin.Input) return null;
        if (!variable.Var.IsNumeric && variable.Measure != Measure.Length) return null;
        if (op is "==" or "!=") return null;

        // Lado em que a regra dispara e o oposto.
        var fires = positive ? op : Negate(op);
        var side = valid ? Negate(fires) : fires;
        var step = variable.Var.IsIntegral(variable.Measure) ? 1m : 0.01m;
        var delta = side switch { ">" => step, "<" => -step, _ => 0m };
        var name = variable.Var.Field is { } field ? FieldName(field) : variable.Var.Display;

        switch (constant)
        {
            case ConstTerm { Number: { } number }:
            {
                var value = number + delta;
                var label = variable.Measure == Measure.Length ? $"{value} caractere(s)" : value.ToString(CultureInfo.InvariantCulture);
                return new RuleLimit(new CmpPred(variable, "==", ConstTerm.Of(value), $"{variable.Var.Display} == {label}"), name, label);
            }
            case NowTerm now when variable.Var.Kind == VarKind.Date:
            {
                var offset = now.OffsetDays + (int)Math.Sign(delta);
                // DateTime.Now tem hora: o limite seguro é o dia seguinte/anterior.
                if (now.Exact && delta == 0) offset += side == ">=" ? 1 : -1;
                var label = Values.RelativeDay(offset);
                return new RuleLimit(new CmpPred(variable, "==", new NowTerm(offset, false), $"{variable.Var.Display} == {label}"), name, label);
            }
        }
        return null;
    }

    private static string Negate(string op) => op switch { ">" => "<=", ">=" => "<", "<" => ">=", "<=" => ">", _ => op };

    public static string FieldName(InputField field) =>
        field.Location == InputLocations.Body ? ValidationRule.JsonPath(field) is { Length: > 0 } path ? path : field.Name : field.Name;

    private static string Name(Var v) => v.Field is { } field ? FieldName(field) : v.Display;
}
