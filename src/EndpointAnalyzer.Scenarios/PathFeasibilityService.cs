using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Fase 3: viabilidade de cada folha. As restrições do caminho são exatas, sem escolhas gulosas:
/// <code>condições do caminho + guardas anteriores que passam (¬(alcançada ∧ gatilho)) + condição que dispara a folha</code>
/// e o resultado é trivalente: SAT (com os valores que provam), UNSAT (contradição na semântica suportada) ou UNKNOWN
/// (limite ou falha do solver). Uma folha cuja própria condição é opaca (chamada não resolvida, exceção capturada) fica
/// UNKNOWN mesmo com valores: o solver só pode supor o resultado, não prová-lo.
/// </summary>
internal sealed class PathFeasibilityService(FlowContext ctx, Func<IEnumerable<Pred>, SolveResult> check)
{
    public static Pred NotFiring(CodeRule rule) => Pred.Not(Pred.And(rule.Site.Path, rule.Trigger));

    public static Pred NotFiring(EarlyExit exit) => Pred.Not(Pred.And(exit.Site.Path, exit.Trigger));

    private List<Pred>? _principal;
    private readonly HashSet<EarlyExit> _unavoidable = [];

    /// <summary>
    /// Caminho principal: validações passam, nenhuma regra dispara e as saídas antecipadas são evitadas quando possível
    /// (na ordem do fluxo). A que não pode ser evitada — if (ModelState.IsValid) return ApiResponse(...) — é a própria
    /// resposta final, não um desfecho à parte.
    /// </summary>
    public List<Pred> Principal()
    {
        if (_principal is not null) return [.. _principal];
        var constraints = new List<Pred> { ctx.ValidationsOk };
        constraints.AddRange(ctx.Rules.Select(NotFiring));
        foreach (var exit in ctx.Exits)
        {
            if (check([.. constraints, NotFiring(exit)]).Status != Feasibility.Unsat) constraints.Add(NotFiring(exit));
            else if (check(constraints).Status == Feasibility.Sat) _unavoidable.Add(exit);
        }
        _principal = constraints;
        return [.. constraints];
    }

    /// <summary>A regra dispara com o trecho alcançado: validações e regras anteriores passam e nenhuma saída antes dela acontece.</summary>
    public List<Pred> Rule(CodeRule rule) =>
    [
        ctx.ValidationsOk, rule.Site.Path, rule.Trigger,
        .. ctx.Rules.TakeWhile(r => r != rule).Select(NotFiring),
        .. ctx.Exits.Where(x => FlowModel.Blocks(x, rule.Site.Order)).Select(NotFiring),
    ];

    public List<Pred> Exit(EarlyExit exit) =>
    [
        ctx.ValidationsOk, exit.Site.Path, exit.Trigger,
        .. ctx.Rules.Where(r => FlowModel.Compare(r.Site.Order, exit.Site.Order) < 0).Select(NotFiring),
        .. ctx.Exits.Where(x => x != exit && FlowModel.Blocks(x, exit.Site.Order)).Select(NotFiring),
    ];

    /// <summary>Só esta validação falha (isolada) ou, sem isolar, as dos outros campos passam.</summary>
    public List<Pred> Validation(ValidationRule rule, bool isolated) =>
    [
        rule.Violation,
        .. ctx.Validations.Where(v => v != rule && (isolated || v.Field != rule.Field)).Select(v => Pred.Not(v.Violation)),
    ];

    public void EvaluateAll(List<TerminalOutcome> leaves)
    {
        var principal = leaves.First(l => l is { Kind: TerminalKinds.Success, Flipped: null });
        Principal();
        foreach (var leaf in leaves)
        {
            Evaluate(leaf);
            if (leaf.Kind == TerminalKinds.Exit && leaf.Feasibility != Feasibility.Unsat && leaf.Exits.All(_unavoidable.Contains))
            {
                leaf.EquivalentTo = principal;
                leaf.Reason = "sempre acontece quando nenhuma regra bloqueia: é a própria resposta final do caminho principal";
            }
        }
    }

    public PathCandidate Evaluate(TerminalOutcome leaf)
    {
        var candidate = leaf.Kind switch
        {
            TerminalKinds.Validation => ValidationPath(leaf, leaf.Validation!),
            TerminalKinds.Binding => new PathCandidate { Leaf = leaf, Constraints = [], Result = SolveResult.Sat(new Assignment()) },
            TerminalKinds.Rule => Alternatives(leaf, leaf.Rules.Select(r => (Rule(r), r.Trigger, Base: (List<Pred>)[ctx.ValidationsOk, r.Site.Path, r.Trigger]))),
            TerminalKinds.Exit => Alternatives(leaf, leaf.Exits.Select(x => (Exit(x), x.Trigger, Base: (List<Pred>)[ctx.ValidationsOk, x.Site.Path, x.Trigger]))),
            _ when leaf.Flipped is { } flipped => Path(leaf, [.. Principal(), flipped], flipped),
            _ => Path(leaf, Principal(), null),
        };

        var result = candidate.Result;
        leaf.Witness = result.Assignment;
        leaf.Feasibility = result.Status;
        leaf.Reason ??= result.Reason;
        if (result.Status == Feasibility.Sat && candidate.Target is { } target
            && Evaluator.Atoms(target).OfType<OpaquePred>().Select(o => o.Text).Distinct().ToList() is { Count: > 0 } opaque)
        {
            // Há valores, mas eles dependem de supor o resultado de uma condição que não foi traduzida.
            leaf.Feasibility = Feasibility.Unknown;
            leaf.Reason = $"depende de condição não traduzida: {string.Join("; ", opaque)} (o cenário supõe o valor, sem prova)";
        }
        if (result.Status == Feasibility.Unsat) leaf.Reason ??= UnsatReason(leaf);
        return candidate;
    }

    private PathCandidate Path(TerminalOutcome leaf, List<Pred> constraints, Pred? target) =>
        new() { Leaf = leaf, Constraints = constraints, Target = target, Result = check(constraints) };

    private PathCandidate ValidationPath(TerminalOutcome leaf, ValidationRule rule)
    {
        var isolated = Validation(rule, true);
        var result = check(isolated);
        if (result.Status != Feasibility.Unsat) return new PathCandidate { Leaf = leaf, Constraints = isolated, Target = rule.Violation, Result = result };

        // ASP.NET Core agrega os erros: a validação pode só falhar junto com outra do mesmo campo ("" e [StringLength(7, MinimumLength = 7)]).
        var together = Validation(rule, false);
        var aggregated = check(together);
        if (aggregated.Status == Feasibility.Sat)
        {
            leaf.Aggregated = true;
            leaf.Reason = "só falha junto com outra validação do mesmo campo (os erros são devolvidos juntos)";
        }
        else if (aggregated.Status == Feasibility.Unsat && UnmodeledNull(rule.Violation))
            // null numa referência não anulável: o JSON pode trazer null, mas o modelo de valores não representa isso.
            return new PathCandidate
            {
                Leaf = leaf, Constraints = together, Target = rule.Violation,
                Result = SolveResult.Unknown("só falha com null num campo de tipo não anulável: o model binding aceita null no JSON, mas o gerador de payload não produz esse valor"),
            };
        else if (aggregated.Status == Feasibility.Unsat)
            leaf.Reason = "nenhum valor viola esta validação com as dos outros campos passando";
        return new PathCandidate { Leaf = leaf, Constraints = together, Target = rule.Violation, Result = aggregated };
    }

    /// <summary>A violação depende de null numa variável não anulável (o solver nunca a deixa null).</summary>
    public static bool UnmodeledNull(Pred violation) => Evaluator.Atoms(violation).OfType<NullPred>().Any(n => !n.Var.Nullable);

    /// <summary>Folha com mais de um caminho (mesmo trecho alcançado por chamadas diferentes): viável se algum caminho for.</summary>
    private PathCandidate Alternatives(TerminalOutcome leaf, IEnumerable<(List<Pred> Constraints, Pred Target, List<Pred> Base)> paths)
    {
        PathCandidate? unknown = null;
        PathCandidate? unsat = null;
        var reachable = false;
        foreach (var (constraints, target, @base) in paths)
        {
            var candidate = new PathCandidate { Leaf = leaf, Constraints = constraints, Target = target, Result = check(constraints) };
            if (candidate.Result.Status == Feasibility.Sat) return candidate;
            if (candidate.Result.Status == Feasibility.Unknown) unknown ??= candidate;
            else
            {
                unsat ??= candidate;
                reachable |= check(@base).Status != Feasibility.Unsat;
            }
        }
        if (unknown is not null) return unknown;
        leaf.Reason = reachable
            ? "não é alcançável sem disparar uma regra (ou saída antecipada) anterior"
            : "a condição não pode ser verdadeira nesse ponto do fluxo";
        return unsat!;
    }

    private string UnsatReason(TerminalOutcome leaf)
    {
        if (leaf.Kind != TerminalKinds.Success) return "caminho contraditório";
        if (leaf.Flipped is { } flipped) return $"no caminho de sucesso, {flipped.Text} não pode acontecer";
        if (check([ctx.ValidationsOk]).Status == Feasibility.Unsat) return "as validações de entrada não podem passar juntas";
        var constraints = new List<Pred> { ctx.ValidationsOk };
        foreach (var rule in ctx.Rules)
        {
            constraints.Add(NotFiring(rule));
            if (check(constraints).Status == Feasibility.Unsat)
                return $"a regra \"{rule.Message ?? rule.TriggerCode}\" sempre bloqueia com as regras anteriores satisfeitas";
        }
        return "sempre há uma saída antecipada ou regra que encerra antes da resposta final";
    }
}
