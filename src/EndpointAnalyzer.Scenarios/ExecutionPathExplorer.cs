using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Reconstrói o fluxo de controle do endpoint e percorre os caminhos de execução até os pontos de encerramento.
/// <para>
/// Os passos seguem a ordem interprocedural do código (<see cref="FlowModel"/>): regras, returns (do endpoint e de
/// métodos internos) e efeitos. Em cada passo o caminho se divide só quando os dois lados continuam satisfazíveis com o
/// que o caminho já assumiu (o solver poda os lados contraditórios):
/// </para>
/// <list type="bullet">
/// <item>regra: dispara (encerra no ponto da regra) ou não dispara (segue);</item>
/// <item>return do endpoint: encerra no ponto do return ou segue;</item>
/// <item>return de método interno: o resto do método não executa e o caminho segue no chamador (o valor devolvido já
/// está propagado nas condições do chamador);</item>
/// <item>efeito condicional: executa ou não (o conjunto de efeitos distingue as respostas de sucesso).</item>
/// </list>
/// Validações de entrada encerram antes da action: um caminho por validação (só ela falhando quando possível).
/// </summary>
internal sealed class ExecutionPathExplorer(FlowContext ctx, IReadOnlyList<TerminalPoint> points, Func<IEnumerable<Pred>, SolveResult> check)
{
    /// <summary>Passos visitados por endpoint (proteção contra explosão de caminhos).</summary>
    private const int MaxVisits = 20000;

    private const string EntryKey = "entrada";
    private const string GateKey = "validacao-entrada";

    public List<ExecutionPath> Paths { get; } = [];

    /// <summary>A exploração parou no limite: pontos sem caminho encontrado ficam UNKNOWN, nunca UNSAT.</summary>
    public bool Truncated { get; private set; }

    private int _visits;
    private readonly HashSet<TerminalPoint> _reached = [];

    private abstract record Step(IReadOnlyList<int> Order);

    private sealed record RuleStep(CodeRule Rule, TerminalPoint Point) : Step(Rule.Site.Order);

    private sealed record ExitStep(EarlyExit Exit, TerminalPoint? Point) : Step(Exit.Site.Order);

    private sealed record EffectStep(EffectSite Effect, string Key) : Step(Effect.Site.Order);

    /// <summary>O que um caminho já assumiu até o passo atual.</summary>
    private sealed class State
    {
        public List<PathConstraint> Constraints { get; init; } = [];
        public List<EffectSite> Effects { get; init; } = [];
        public List<EarlyExit> Exits { get; init; } = [];
        public List<PathStep> Steps { get; init; } = [];
        public List<Decision> Decisive { get; init; } = [];
        public Dictionary<string, bool> Decided { get; init; } = [];
        public required SolveResult Result { get; set; }

        /// <summary>Transição até o próximo nó: sentido e condição assumida.</summary>
        public string Sense { get; set; } = "next";
        public string? Condition { get; set; }

        public State Clone() => new()
        {
            Constraints = [.. Constraints], Effects = [.. Effects], Exits = [.. Exits], Steps = [.. Steps], Decisive = [.. Decisive],
            Decided = new Dictionary<string, bool>(Decided), Result = Result, Sense = Sense, Condition = Condition,
        };

        public void Visit(string key, FlowNodeKind kind, string label, SourceReference? source)
        {
            Steps.Add(new PathStep(key, kind, label, source, Sense, Condition));
            (Sense, Condition) = ("next", null);
        }
    }

    public void Explore()
    {
        var entry = new PathStep(EntryKey, FlowNodeKind.Entry, ctx.Endpoint, null, "next", null);
        var input = points.Where(p => p.Kind is TerminalKinds.Validation or TerminalKinds.Binding).ToList();
        var gate = input.Count == 0 ? null : new PathStep(GateKey, FlowNodeKind.Validation,
            "Validação de entrada antes da action (model binding, DataAnnotations, FluentValidation)", null, "next", null);

        foreach (var point in input)
        {
            var steps = new List<PathStep> { entry, gate!, Final(point, "true", point.Validation?.ViolationCode ?? "tipo do JSON incompatível") };
            Paths.Add(point.Validation is { } rule ? ValidationPath(point, rule, steps) : new ExecutionPath
            {
                Terminal = point, Constraints = [], Effects = [], InnerExits = [], Steps = steps, Decisive = [],
                Result = SolveResult.Sat(new Assignment()),
            });
        }

        var state = new State { Result = check([ctx.ValidationsOk]) };
        state.Steps.Add(entry);
        if (gate is not null)
        {
            state.Steps.Add(gate);
            state.Sense = "false";
        }
        if (state.Result.Status == Feasibility.Unsat) return;
        state.Constraints.Add(new PathConstraint(ctx.ValidationsOk));
        Walk(Steps(), 0, state);
    }

    /// <summary>Regras, returns e efeitos na ordem de execução.</summary>
    private List<Step> Steps()
    {
        var rulePoints = points.SelectMany(p => p.Rules.Select(r => (Rule: r, Point: p))).ToDictionary(x => x.Rule, x => x.Point);
        var exitPoints = points.Where(p => p.Exit is not null).ToDictionary(p => p.Exit!);
        var effectKeys = new Dictionary<string, int>();
        string EffectKey(EffectSite e)
        {
            var key = $"efeito|{e.Kind}|{e.Target}|{ScenarioKey.Code(e.Description)}";
            var count = effectKeys[key] = effectKeys.GetValueOrDefault(key) + 1;
            return count == 1 ? key : $"{key}#{count}";
        }

        return ctx.Rules.Select(r => (Step)new RuleStep(r, rulePoints[r]))
            .Concat(ctx.Exits.Select(x => (Step)new ExitStep(x, exitPoints.GetValueOrDefault(x))))
            .Concat(ctx.Flow.Effects.Select(e => (Step)new EffectStep(e, EffectKey(e))))
            .OrderBy(s => s.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare))
            .ToList();
    }

    private void Walk(List<Step> steps, int index, State state)
    {
        if (++_visits > MaxVisits || Paths.Count >= ctx.MaxPaths)
        {
            Truncated = true;
            return;
        }
        if (index == steps.Count)
        {
            Record(points[^1], state, state.Result, "next", null);
            return;
        }

        var step = steps[index];
        // Depois de um return interno, o resto daquele método não executa.
        if (state.Exits.Any(x => FlowModel.Blocks(x, step.Order)))
        {
            Walk(steps, index + 1, state);
            return;
        }

        switch (step)
        {
            case RuleStep { Rule: var rule, Point: var point }:
            {
                state.Visit($"{point.Key}|decisao", FlowNodeKind.Decision, $"se {rule.TriggerCode}", rule.Site.Source);
                // Um caminho por regra basta: a regra encerra sempre no mesmo ponto, com os mesmos efeitos.
                if (!_reached.Contains(point))
                {
                    var fired = check([.. Exact(state), rule.Site.Path, rule.Trigger]);
                    if (fired.Status != Feasibility.Unsat)
                    {
                        var final = state.Clone();
                        final.Constraints.AddRange([new PathConstraint(rule.Site.Path), new PathConstraint(rule.Trigger)]);
                        final.Decisive.Add(new Decision(rule.Trigger, rule.TriggerCode, true));
                        Record(point, final, fired, "true", rule.TriggerCode);
                    }
                }
                var avoid = Pred.Not(Pred.And(rule.Site.Path, rule.Trigger));
                var passed = check([.. Exact(state), avoid]);
                if (passed.Status == Feasibility.Unsat) return;
                state.Constraints.Add(new PathConstraint(avoid, Pred.Not(rule.Trigger)));
                (state.Result, state.Sense, state.Condition) = (passed, "false", null);
                Walk(steps, index + 1, state);
                return;
            }

            case ExitStep { Exit: var exit, Point: var point }:
            {
                var key = point is not null ? $"{point.Key}|decisao" : $"retorno|{exit.Site.Method.DisplayName}|{ScenarioKey.Code(exit.TriggerCode)}";
                state.Visit(key, FlowNodeKind.Decision,
                    exit.InEntry ? $"se {exit.TriggerCode}: return" : $"{exit.Site.Method.Node.MethodName} retorna quando {exit.TriggerCode}", exit.Site.Source);
                var taken = check([.. Exact(state), exit.Site.Path, exit.Trigger]);
                var avoid = Pred.Not(Pred.And(exit.Site.Path, exit.Trigger));
                var passed = check([.. Exact(state), avoid]);
                if (taken.Status != Feasibility.Unsat)
                {
                    var next = passed.Status == Feasibility.Unsat ? state : state.Clone();
                    next.Constraints.AddRange([new PathConstraint(exit.Site.Path), new PathConstraint(exit.Trigger)]);
                    next.Decisive.Add(new Decision(exit.Trigger, exit.TriggerCode, true));
                    next.Result = taken;
                    if (point is not null) Record(point, next, taken, "true", exit.TriggerCode);
                    else
                    {
                        next.Exits.Add(exit);
                        (next.Sense, next.Condition) = ("true", exit.TriggerCode);
                        Walk(steps, index + 1, next);
                    }
                }
                if (passed.Status == Feasibility.Unsat) return;
                state.Constraints.Add(new PathConstraint(avoid, Pred.Not(exit.Trigger)));
                (state.Result, state.Sense, state.Condition) = (passed, "false", null);
                Walk(steps, index + 1, state);
                return;
            }

            case EffectStep { Effect: var effect, Key: var key }:
            {
                var path = effect.Site.Path;
                // Condição não traduzida: o efeito pode ou não acontecer; não distingue desfechos.
                if (Evaluator.Atoms(path).Any(a => a is OpaquePred))
                {
                    state.Visit(key, FlowNodeKind.Effect, $"{effect.Description} (condição não traduzida)", effect.Site.Source);
                    Walk(steps, index + 1, state);
                    return;
                }
                if (state.Decided.TryGetValue(path.Key, out var known))
                {
                    if (known) Execute(state, effect, key);
                    Walk(steps, index + 1, state);
                    return;
                }

                var yes = check([.. Exact(state), path]);
                var no = check([.. Exact(state), Pred.Not(path)]);
                if (yes.Status == Feasibility.Unsat && no.Status == Feasibility.Unsat) return;
                if (no.Status == Feasibility.Unsat || yes.Status == Feasibility.Unsat)
                {
                    // Só um lado é possível: o efeito executa (ou não) sem decisão própria.
                    state.Decided[path.Key] = no.Status == Feasibility.Unsat;
                    if (no.Status == Feasibility.Unsat) Execute(state, effect, key);
                    Walk(steps, index + 1, state);
                    return;
                }

                // Os dois lados são possíveis: a decisão é a condição livre (a que o caminho ainda não fixou).
                var literal = Free(state, path) ?? path;
                var atom = literal is NotPred n ? n.Inner : literal;
                var positive = literal is not NotPred;
                var decision = $"ramo|{ScenarioKey.Code(atom.Text)}";
                state.Visit(decision, FlowNodeKind.Decision, $"ramo: {atom.Text}", effect.Site.Source);

                var with = state.Clone();
                with.Constraints.Add(new PathConstraint(literal));
                with.Decisive.Add(new Decision(atom, atom.Text, positive));
                with.Decided[path.Key] = true;
                (with.Result, with.Sense, with.Condition) = (check(Exact(with)), "true", literal.Text);
                Execute(with, effect, key);
                Walk(steps, index + 1, with);

                state.Constraints.Add(new PathConstraint(Pred.Not(literal)));
                state.Decisive.Add(new Decision(atom, atom.Text, !positive));
                state.Decided[path.Key] = false;
                (state.Result, state.Sense, state.Condition) = (check(Exact(state)), "false", Pred.Not(literal).Text);
                Walk(steps, index + 1, state);
                return;
            }
        }
    }

    private static void Execute(State state, EffectSite effect, string key)
    {
        state.Visit(key, FlowNodeKind.Effect, effect.Description, effect.Site.Source);
        state.Effects.Add(effect);
    }

    /// <summary>
    /// A parte da condição do efeito que decide se ele executa: a única que o caminho ainda não fixou ou a que, sozinha
    /// (com o que o caminho já assumiu), garante o efeito — if (request.VeiculoId != null) { ... } com a regra de dentro
    /// já evitada. Null quando nenhuma decide sozinha (a decisão é a condição inteira).
    /// </summary>
    private Pred? Free(State state, Pred path)
    {
        var exact = Exact(state);
        var free = Evaluator.Conjuncts(path).Where(c => check([.. exact, Pred.Not(c)]).Status != Feasibility.Unsat).ToList();
        if (free.Count == 1) return free[0];
        return free.FirstOrDefault(c => check([.. exact, c, Pred.Not(path)]).Status == Feasibility.Unsat);
    }

    private static List<Pred> Exact(State state) => state.Constraints.Select(c => c.Exact).ToList();

    private static PathStep Final(TerminalPoint point, string sense, string? condition) =>
        new(point.Key, FlowNodeKind.Terminal, point.Label, point.Source, sense, condition);

    private void Record(TerminalPoint point, State state, SolveResult result, string sense, string? condition)
    {
        if (result.Status == Feasibility.Sat) _reached.Add(point);
        Paths.Add(new ExecutionPath
        {
            Terminal = point,
            Constraints = [.. state.Constraints],
            Effects = [.. state.Effects],
            InnerExits = [.. state.Exits],
            Steps = [.. state.Steps, Final(point, sense, condition)],
            Decisive = [.. state.Decisive],
            Result = result,
            Reason = result.Reason,
        });
    }

    /// <summary>
    /// Só esta validação falha, com as outras passando; se não há valor assim, as dos outros campos passam (o ASP.NET
    /// Core agrega os erros do mesmo campo: "" viola [Required] e [StringLength(7, MinimumLength = 7)] juntos).
    /// </summary>
    private ExecutionPath ValidationPath(TerminalPoint point, ValidationRule rule, List<PathStep> steps)
    {
        List<Pred> Constraints(bool isolated) =>
        [
            rule.Violation,
            .. ctx.Validations.Where(v => v != rule && (isolated || v.Field != rule.Field)).Select(v => Pred.Not(v.Violation)),
        ];
        ExecutionPath Path(List<Pred> constraints, SolveResult result, bool aggregated = false, string? reason = null) => new()
        {
            Terminal = point, Constraints = constraints.Select(c => new PathConstraint(c)).ToList(), Effects = [], InnerExits = [], Steps = steps,
            Decisive = [new Decision(rule.Violation, rule.ViolationCode, true)], Result = result, Aggregated = aggregated, Reason = reason ?? result.Reason,
        };

        var isolated = Constraints(true);
        var alone = check(isolated);
        if (alone.Status != Feasibility.Unsat) return Path(isolated, alone);

        var together = Constraints(false);
        var aggregated = check(together);
        if (aggregated.Status == Feasibility.Sat)
            return Path(together, aggregated, true, "só falha junto com outra validação do mesmo campo (os erros são devolvidos juntos)");
        // null numa referência não anulável: o JSON pode trazer null, mas o modelo de valores não representa isso.
        if (aggregated.Status == Feasibility.Unsat && UnmodeledNull(rule.Violation))
            return Path(together, SolveResult.Unknown("só falha com null num campo de tipo não anulável: o model binding aceita null no JSON, mas o gerador de payload não produz esse valor"));
        return Path(together, aggregated, reason: aggregated.Status == Feasibility.Unsat ? "nenhum valor viola esta validação com as dos outros campos passando" : null);
    }

    /// <summary>A violação depende de null numa variável não anulável (o solver nunca a deixa null).</summary>
    public static bool UnmodeledNull(Pred violation) => Evaluator.Atoms(violation).OfType<NullPred>().Any(n => !n.Var.Nullable);

    /// <summary>Por que nenhum caminho chega ao ponto: contradição na própria condição ou algo anterior sempre encerra antes.</summary>
    public string UnreachableReason(TerminalPoint point)
    {
        if (Truncated) return "limite de caminhos explorados atingido antes de chegar a este ponto";
        switch (point.Kind)
        {
            case TerminalKinds.Rule when point.Rules.All(r => r.IsModelStateCheck):
                return "ModelState só fica inválido com payload malformado e não há campo para o cenário de tipo inválido";
            case TerminalKinds.Rule:
                return point.Rules.Any(r => check([ctx.ValidationsOk, r.Site.Path, r.Trigger]).Status != Feasibility.Unsat)
                    ? "não é alcançável sem disparar uma regra (ou return) anterior"
                    : "a condição não pode ser verdadeira nesse ponto do fluxo";
            case TerminalKinds.Exit:
                return check([ctx.ValidationsOk, point.Exit!.Site.Path, point.Exit.Trigger]).Status != Feasibility.Unsat
                    ? "não é alcançável sem disparar uma regra (ou return) anterior"
                    : "a condição não pode ser verdadeira nesse ponto do fluxo";
            case TerminalKinds.Success:
            {
                if (check([ctx.ValidationsOk]).Status == Feasibility.Unsat) return "as validações de entrada não podem passar juntas";
                var constraints = new List<Pred> { ctx.ValidationsOk };
                foreach (var rule in ctx.Rules)
                {
                    constraints.Add(Pred.Not(Pred.And(rule.Site.Path, rule.Trigger)));
                    if (check(constraints).Status == Feasibility.Unsat)
                        return $"a regra \"{rule.Message ?? rule.TriggerCode}\" sempre bloqueia com as regras anteriores satisfeitas";
                }
                return ctx.Exits.FirstOrDefault(x => x.InEntry && check([.. constraints, Pred.Not(Pred.And(x.Site.Path, x.Trigger))]).Status == Feasibility.Unsat) is { } always
                    ? $"o return antecipado quando {always.TriggerCode} sempre acontece antes"
                    : "sempre há um return antecipado ou regra que encerra antes da resposta final";
            }
            default:
                return "caminho contraditório";
        }
    }
}
