using System.Globalization;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

public interface IScenarioGenerator
{
    Task<ScenarioMatrix> GenerateAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mesma matriz de <see cref="GenerateAsync"/>, mantendo as restrições de cada cenário para a validação em runtime
    /// materializar o payload com dados reais (solver + simulação do fluxo).
    /// </summary>
    Task<ScenarioModel> GenerateModelAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default);
}

/// <summary>Limites da geração de cenários.</summary>
public sealed class ScenarioGenerationOptions
{
    /// <summary>Máximo de cenários por endpoint: as obrigações que ficarem de fora aparecem como não cobertas (limite).</summary>
    public int MaxScenarios { get; set; } = ScenarioGenerator.MaxScenarios;

    /// <summary>Iterações cobertas nos laços sobre coleções do payload (zero, uma e várias). Não é enumeração exaustiva.</summary>
    public IReadOnlyList<int> LoopIterations { get; set; } = [0, 1, 2];
}

/// <summary>
/// Fase 4 (tecnologias-fases-geracao-payloads.md): gera a matriz de cenários a partir dos desfechos alcançáveis do fluxo
/// (EndpointAnalyzer_Arvore_Geracao_Cenarios.md), e não de candidatos de teste:
/// <list type="number">
/// <item>Árvore lógica da execução: entrada HTTP, validações antes da action, decisões, efeitos e folhas (<see cref="FlowTreeBuilder"/>).</item>
/// <item>Folhas distintas pelo ponto semântico do código (<see cref="TerminalOutcomeCollector"/>).</item>
/// <item>Viabilidade de cada folha: SAT, UNSAT ou UNKNOWN (<see cref="PathFeasibilityService"/>).</item>
/// <item>Obrigações de cobertura (desfecho, classe, limite, ramo, laço) e seleção gulosa de cenários (<see cref="CoveragePlanner"/>).</item>
/// </list>
/// Cada cenário é resolvido pelo solver (o Z3 só nas restrições complexas), o resultado esperado vem da simulação do fluxo
/// com os valores encontrados e só conta como cobertura se a simulação atinge a folha-alvo.
/// </summary>
public partial class ScenarioGenerator(ScenarioGenerationOptions? options = null) : IScenarioGenerator
{
    public const int MaxScenarios = 80;

    private readonly ScenarioGenerationOptions _options = options ?? new ScenarioGenerationOptions();

    public async Task<ScenarioMatrix> GenerateAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        (await GenerateModelAsync(graph, context, cancellationToken)).Matrix;

    public async Task<ScenarioModel> GenerateModelAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Values.Today = DateTime.Today;
            var input = InputModel.Build(graph.EntryPoint.Symbol, graph.Endpoint.Route, graph.Endpoint.HttpMethod);
            var vars = new VarTable(input);
            var resolver = new SymbolicResolver(graph, input, vars);
            var solution = await ExceptionStatusMap.BuildAsync(graph.Solution, cancellationToken);
            var validations = await ValidationRuleExtractor.ExtractAsync(graph, input, resolver, cancellationToken);
            var flow = FlowModel.Build(graph, context, resolver, solution);
            var run = new Run(context, input, vars, resolver, validations, flow, solution, $"{graph.Endpoint.HttpMethod} {graph.Endpoint.Route}", _options);
            return new ScenarioModel(run.Execute(), run);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScenarioModel(new ScenarioMatrix { Notes = [$"Não foi possível gerar os cenários: {ex.Message}"] }, null);
        }
    }

    private sealed record Candidate(string Kind, string Technique, string Title, List<Pred> Constraints, ScenarioFocus? Focus = null, bool HidePreconditions = false,
        string? Note = null);

    private sealed record Outcome(
        bool Success,
        int? Status,
        string StatusSource,
        List<string> Messages,
        string? Exception,
        string? Rule,
        SourceReference? Source,
        IReadOnlyList<int>? StopOrder,
        List<EarlyExit> Exits,
        bool ValidationFailed,
        List<string> Notes)
    {
        /// <summary>Validações violadas (o ASP.NET Core devolve todas juntas).</summary>
        public IReadOnlyList<ValidationRule> Violated { get; init; } = [];

        /// <summary>Regra que encerrou o fluxo.</summary>
        public CodeRule? Fired { get; init; }
    }

    internal sealed partial class Run
    {
        private readonly EndpointAnalysisContext _context;
        private readonly InputModel _input;
        private readonly VarTable _vars;
        private readonly SymbolicResolver _resolver;
        private readonly List<ValidationRule> _validations;
        private readonly FlowModel _flow;
        private readonly ExceptionStatusMap _solution;
        private readonly string _endpoint;
        private readonly ScenarioGenerationOptions _options;
        private readonly ConstraintSolver _solver = new();
        private readonly PayloadBuilder _payload;
        private readonly Pred _validationsOk;
        private readonly List<CodeRule> _rules;
        private readonly List<EarlyExit> _exits;
        private readonly Dictionary<string, SolveResult> _solved = [];
        private readonly List<string> _notes = [];

        /// <summary>Restrições, valores e simulação de cada cenário gerado (por id), para a materialização em runtime.</summary>
        private readonly Dictionary<string, Entry> _entries = [];

        // ---- Árvore e cobertura (preenchidas por Execute) ----
        private List<TerminalOutcome> _leaves = [];
        private TerminalOutcome? _principal;
        private readonly Dictionary<ValidationRule, TerminalOutcome> _byValidation = [];
        private readonly Dictionary<CodeRule, TerminalOutcome> _byRule = [];
        private readonly Dictionary<EarlyExit, TerminalOutcome> _byExit = [];
        private readonly Dictionary<CoverageTarget, bool> _isolable = [];
        private PathFeasibilityService? _feasibility;
        private Entry? _principalEntry;

        /// <summary>Payload gerado para a variação de ramo equivalente ao caminho principal: vira a linha da folha dela.</summary>
        private readonly Dictionary<TerminalOutcome, Entry> _equivalentEntries = [];

        private sealed record Entry(Scenario Scenario, List<Pred> Constraints, bool HidePreconditions, Assignment Assignment, Outcome Outcome,
            InputField? MismatchField, CoverageTarget? Target = null);

        /// <summary>Restrições do caminho feliz (escolhas gulosas legíveis), ramos ativados e como cada saída foi evitada.</summary>
        private sealed record Plan(List<Pred> Happy, List<Pred> Branches, Dictionary<EarlyExit, Pred> Avoidance,
            List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> Conditions);

        public Run(EndpointAnalysisContext context, InputModel input, VarTable vars, SymbolicResolver resolver,
            List<ValidationRule> validations, FlowModel flow, ExceptionStatusMap solution, string endpoint, ScenarioGenerationOptions options)
        {
            _context = context;
            _input = input;
            _vars = vars;
            _resolver = resolver;
            _validations = validations;
            _flow = flow;
            _solution = solution;
            _endpoint = endpoint;
            _options = options;
            _payload = new PayloadBuilder(input, vars, solution.EnumsAsStrings);
            _validationsOk = Pred.And(validations.Select(v => Pred.Not(v.Violation)));
            // if (!ModelState.IsValid) return ...: com validações, os cenários de validação já cobrem.
            _rules = flow.Rules.Where(r => !(r.IsModelStateCheck && validations.Count > 0)).ToList();
            // Saída antecipada só importa quando impede algum efeito ou regra (return false num helper não muda nada).
            _exits = flow.Exits.Where(x => x.InEntry || flow.Effects.Any(e => FlowModel.Blocks(x, e.Site.Order)) || _rules.Any(r => FlowModel.Blocks(x, r.Site.Order))).ToList();
        }

        // ---- Geração ----

        public ScenarioMatrix Execute()
        {
            // Fase 1 e 2: fluxo, árvore lógica e folhas.
            var happy = HappyConstraints(out var branches, out var avoidance);
            var context = new FlowContext(_endpoint, _input, _vars, _flow, _validations, _validationsOk, _rules, _exits, branches, MismatchField(), _options.LoopIterations);
            _leaves = TerminalOutcomeCollector.Collect(context, Label);
            var tree = FlowTreeBuilder.Build(context, _leaves);
            _principal = _leaves.First(l => l is { Kind: TerminalKinds.Success, Flipped: null });
            foreach (var leaf in _leaves)
            {
                if (leaf.Validation is { } validation) _byValidation[validation] = leaf;
                foreach (var rule in leaf.Rules) _byRule[rule] = leaf;
                foreach (var exit in leaf.Exits) _byExit[exit] = leaf;
            }

            // Fase 3: viabilidade de cada folha (SAT, UNSAT ou UNKNOWN).
            _feasibility = new PathFeasibilityService(context, Check);
            _feasibility.EvaluateAll(_leaves);
            ModelStateLeaves();

            // Fase 4: obrigações e seleção gulosa.
            var conditions = DecisionConditions(branches);
            var plan = new Plan(happy, branches, avoidance, conditions);
            var targets = CoveragePlanner.Targets(context, _leaves);
            foreach (var target in targets)
                if (target.Anchor.Feasibility == Feasibility.Unsat || target.Leaf?.Feasibility == Feasibility.Unsat)
                {
                    target.Status = CoverageStatuses.Infeasible;
                    target.Reason = (target.Leaf ?? target.Anchor).Reason;
                }
                else if ((target.Leaf ?? target.Anchor).EquivalentTo is not null)
                {
                    target.Status = CoverageStatuses.Equivalent;
                    target.Reason = (target.Leaf ?? target.Anchor).Reason;
                }
                else if (target.Predicate is { } predicate && Check([predicate]).Status == Feasibility.Unsat)
                {
                    // null numa referência não anulável é possível no JSON: limitação do modelo, não prova de impossibilidade.
                    var unmodeled = PathFeasibilityService.UnmodeledNull(predicate);
                    target.Status = unmodeled ? CoverageStatuses.Pending : CoverageStatuses.Infeasible;
                    target.Reason = unmodeled
                        ? "null num campo de tipo não anulável: o model binding aceita null no JSON, mas o gerador de payload não produz esse valor"
                        : "a classe ou o limite não é possível com o tipo do campo";
                }

            var selected = new List<Entry>();
            var truncated = CoveragePlanner.Select(targets, selected, _options.MaxScenarios, Covers, t => Generate(t, plan));

            // Fase 5: uma linha por folha. A folha que nenhum cenário atinge (equivalente a outra, inviável, não decidida
            // ou cortada pelo limite) entra mesmo assim: com o payload da folha equivalente, gerada agora ou sem payload.
            var rows = selected.Select(e => new Row(e.Scenario, e, TerminalOf(e), e.Target!.Key)).ToList();
            foreach (var leaf in _leaves.Where(l => rows.All(r => r.Leaf != l)).ToList())
                rows.Add(LeafRow(leaf, targets, plan, rows));

            // Identidade e ordem de exibição.
            var order = new[] { ScenarioKinds.Success, ScenarioKinds.Validation, ScenarioKinds.Rule, ScenarioKinds.Boundary };
            rows = rows.OrderBy(r => Array.IndexOf(order, r.Scenario.Kind)).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var (scenario, entry, leaf, key) = rows[i];
                scenario.Id = $"CEN-{i + 1:00}";
                scenario.Key = ScenarioKey.Of(_endpoint, key);
                scenario.Terminal = leaf.Key;
                scenario.Feasibility = leaf.Feasibility switch { Feasibility.Sat => PathFeasibilities.Sat, Feasibility.Unsat => PathFeasibilities.Unsat, _ => PathFeasibilities.Unknown };
                if (entry is not null) _entries[scenario.Id] = entry;
            }
            foreach (var row in rows)
                if (row.Leaf.EquivalentTo is { } other && rows.FirstOrDefault(r => r.Leaf == other) is { } same)
                {
                    row.Scenario.EquivalentTo = same.Scenario.Id;
                    row.Scenario.Notes = [.. row.Scenario.Notes ?? [], $"Mesmo resultado de {same.Scenario.Id}: {row.Leaf.Reason ?? "mesma resposta e mesmos efeitos"}."];
                }

            var scenarios = rows.Select(r => r.Scenario).ToList();
            CodeConditionNotes();
            if (truncated)
                _notes.Add($"Limite de {_options.MaxScenarios} cenários atingido: {targets.Count(t => t.Status == CoverageStatuses.Truncated)} variação(ões) (classe, limite, ramo ou laço) ficaram sem cenário. Toda folha do fluxo continua com linha na matriz.");
            if (_options.LoopIterations.Count > 0 && targets.Any(t => t.Kind == CoverageTargetKinds.Loop))
                _notes.Add($"Laços cobertos com {string.Join(", ", _options.LoopIterations.Distinct().Order())} iteração(ões): outras quantidades não são enumeradas.");

            if (_solver.Z3Error is { } z3) _notes.Add($"Z3 indisponível ({z3}): restrições com mais de uma variável ficaram sem cenário.");
            if (!_solution.FluentAutoValidation && _validations.Any(v => v.Origin == "FluentValidation"))
                _notes.Add("Validação automática do FluentValidation (AddFluentValidationAutoValidation) não encontrada: confira se o validator é executado antes da action.");
            _notes.AddRange(tree.Gaps);
            _notes.AddRange(_flow.Notes);

            return new ScenarioMatrix
            {
                Inputs = Inputs(),
                Conditions = conditions.Select(c => c.Condition).ToList(),
                Scenarios = scenarios,
                Notes = _notes.Distinct().ToList(),
                EnumsAsStrings = _solution.EnumsAsStrings,
                Flow = tree.ToInfo(),
            };
        }

        /// <summary>Linha da matriz: o cenário, as restrições (nulas quando não há payload), a folha que atinge e a chave da identidade.</summary>
        private sealed record Row(Scenario Scenario, Entry? Entry, TerminalOutcome Leaf, string Key);

        /// <summary>Folha que o cenário atinge.</summary>
        private TerminalOutcome TerminalOf(Entry entry) => entry.MismatchField is not null ? entry.Target!.Leaf! : LeafOf(entry.Outcome, entry.Assignment);

        /// <summary>
        /// Linha da folha que nenhum cenário selecionado atinge:
        /// <list type="number">
        /// <item>cortada pelo limite de cenários: gerada agora (o limite vale para as variações, nunca para as folhas);</item>
        /// <item>equivalente a outra folha: o payload que a atinge (o da variação de ramo, ou o da folha equivalente);</item>
        /// <item>senão, sem payload, com o motivo (caminho contraditório, condição não decidida, combinação não encontrada).</item>
        /// </list>
        /// </summary>
        private Row LeafRow(TerminalOutcome leaf, List<CoverageTarget> targets, Plan plan, List<Row> rows)
        {
            var own = targets.Where(t => t.Leaf == leaf).OrderBy(t => t.Kind == CoverageTargetKinds.Outcome ? 0 : 1).FirstOrDefault();
            if (own is { Status: CoverageStatuses.Truncated })
            {
                var (item, status, reason) = Generate(own, plan);
                if (item is not null && TerminalOf(item) == leaf)
                {
                    own.Status = CoverageStatuses.Covered;
                    return new Row(item.Scenario, item, leaf, own.Key);
                }
                own.Status = status ?? (leaf.Feasibility == Feasibility.Unknown ? CoverageStatuses.Pending : CoverageStatuses.Uncovered);
                own.Reason = reason;
            }

            if (leaf.EquivalentTo is { } other
                && (_equivalentEntries.GetValueOrDefault(leaf) ?? rows.FirstOrDefault(r => r.Leaf == other && r.Entry is not null)?.Entry) is { } source)
            {
                var scenario = System.Text.Json.JsonSerializer.Deserialize<Scenario>(System.Text.Json.JsonSerializer.Serialize(source.Scenario))!;
                (scenario.Kind, scenario.Title, scenario.Focus) = (KindOf(leaf), TitleOf(leaf), FocusOf(leaf));
                return new Row(scenario, source with { Scenario = scenario, Target = own }, leaf, leaf.Key);
            }

            return new Row(WithoutPayload(leaf, own, plan), null, leaf, leaf.Key);
        }

        /// <summary>Linha sem payload: o desfecho como o código o declara (status, mensagem, regra), sem simulação.</summary>
        private Scenario WithoutPayload(TerminalOutcome leaf, CoverageTarget? own, Plan plan)
        {
            var rule = leaf.Rules.FirstOrDefault();
            var exit = leaf.Exits.FirstOrDefault();
            var validation = leaf.Validation;
            var space = _endpoint.IndexOf(' ');
            var reason = leaf.Feasibility == Feasibility.Sat ? own?.Reason ?? leaf.Reason : leaf.Reason ?? own?.Reason;
            return new Scenario
            {
                Kind = KindOf(leaf),
                Technique = leaf.Kind is TerminalKinds.Validation or TerminalKinds.Binding ? ScenarioTechniques.EquivalencePartitioning : ScenarioTechniques.DecisionTable,
                Title = TitleOf(leaf),
                Focus = FocusOf(leaf),
                Request = new ScenarioRequest { Method = _endpoint[..space], Url = _endpoint[(space + 1)..] },
                Expected = new ScenarioExpectation
                {
                    Outcome = leaf.Success ? "sucesso" : "erro",
                    HttpStatus = leaf.Status,
                    StatusSource = leaf.Kind switch
                    {
                        TerminalKinds.Validation or TerminalKinds.Binding => ValidationStatus().Source,
                        TerminalKinds.Rule => rule!.StatusSource,
                        TerminalKinds.Exit when exit!.Status is not null => "código",
                        _ => _flow.SuccessStatusSource,
                    },
                    Messages = validation is not null ? [validation.MessageOrDefault] : rule?.Message is not null ? [Label(rule)] : [],
                    Exception = rule?.Exception,
                    Rule = validation is not null ? $"{validation.FieldName}: {validation.Text}"
                        : leaf.Kind == TerminalKinds.Binding ? "model binding: tipo do JSON incompatível"
                        : rule?.TriggerCode,
                    Source = leaf.Source,
                },
                Decisions = plan.Conditions.ToDictionary(c => c.Condition.Id, _ => (bool?)null),
                Solver = "nenhum",
                NoPayload = reason ?? "nenhum payload gerado atinge este desfecho",
            };
        }

        private static string KindOf(TerminalOutcome leaf) => leaf.Kind switch
        {
            TerminalKinds.Validation or TerminalKinds.Binding => ScenarioKinds.Validation,
            TerminalKinds.Rule => ScenarioKinds.Rule,
            _ => ScenarioKinds.Success,
        };

        private string TitleOf(TerminalOutcome leaf) => leaf switch
        {
            { Kind: TerminalKinds.Rule } => $"Regra: {Label(leaf.Rules[0])}",
            { Validation: { } v } => $"{v.FieldName}: {v.Text} violada",
            { Flipped: { } flipped } => $"Sucesso com {(flipped is NotPred n ? n.Inner : flipped).Text} {(flipped is NotPred ? "falso" : "verdadeiro")}",
            { Kind: TerminalKinds.Success } => "Caminho feliz: todos os dados válidos e nenhuma regra bloqueia",
            _ => leaf.Label,
        };

        private static ScenarioFocus? FocusOf(TerminalOutcome leaf) => leaf switch
        {
            { Kind: TerminalKinds.Rule } => new ScenarioFocus { Expression = leaf.Rules[0].TriggerCode, Value = true },
            { Validation: { } v } => new ScenarioFocus { Expression = v.ViolationCode, Value = true },
            { BindingField: { } field } => new ScenarioFocus { Expression = $"{field.Path} com tipo inválido", Value = true },
            { Kind: TerminalKinds.Exit } => new ScenarioFocus { Expression = leaf.Exits[0].TriggerCode, Value = true },
            { Flipped: { } flipped } => new ScenarioFocus { Expression = (flipped is NotPred n ? n.Inner : flipped).Text, Value = flipped is not NotPred },
            _ => null,
        };

        /// <summary>
        /// if (!ModelState.IsValid) return ... sem validações declaradas: o ModelState só fica inválido com o tipo do JSON
        /// incompatível, que é a folha do model binding.
        /// </summary>
        private void ModelStateLeaves()
        {
            var binding = _leaves.FirstOrDefault(l => l.Kind == TerminalKinds.Binding);
            foreach (var leaf in _leaves.Where(l => l.Kind == TerminalKinds.Rule && l.Rules.All(r => r.IsModelStateCheck)))
            {
                leaf.EquivalentTo = binding;
                leaf.Feasibility = binding is null ? Feasibility.Unsat : Feasibility.Sat;
                leaf.Reason = binding is null
                    ? "ModelState só fica inválido com payload malformado e não há campo para o cenário de tipo inválido"
                    : "ModelState só fica inválido com payload malformado: coberto pelo cenário de tipo inválido";
            }
        }

        // ---- Cobertura ----

        /// <summary>Folha que a simulação atinge: validação, regra, retorno antecipado, variação de ramo ou o caminho principal.</summary>
        private TerminalOutcome LeafOf(Outcome o, Assignment a)
        {
            if (o.ValidationFailed && o.Violated.Count > 0) return _byValidation[o.Violated[0]];
            if (o.Fired is { } rule && _byRule.TryGetValue(rule, out var ruleLeaf)) return ruleLeaf;
            foreach (var exit in o.Exits)
                if (_byExit.TryGetValue(exit, out var exitLeaf) && exitLeaf.EquivalentTo is null) return exitLeaf;
            foreach (var flip in _leaves.Where(l => l is { Flipped: not null, EquivalentTo: null }))
                if (Eval(flip.Flipped!, a) == true) return flip;
            return _principal!;
        }

        /// <summary>O cenário exercita a obrigação: atinge a folha-alvo (ou um sucesso) com a classe/limite/ramo verdadeiro.</summary>
        private bool Covers(Entry entry, CoverageTarget target)
        {
            if (entry.MismatchField is not null || target.Leaf?.Kind == TerminalKinds.Binding)
                return entry.MismatchField is not null && entry.MismatchField == target.Leaf?.BindingField;

            var (o, a) = (entry.Outcome, entry.Assignment);
            var holds = target.Predicate is null || Eval(target.Predicate, a) == true;
            if (!holds) return false;
            if (target.Kind == CoverageTargetKinds.Default) return !o.ValidationFailed;
            if (target.Leaf is null) return o.Success && LeafOf(o, a).Kind == TerminalKinds.Success;
            if (target.Leaf.Validation is { } validation)
                // Só a validação-alvo falha, ou não há como isolá-la (os erros do mesmo campo vêm juntos).
                return o.Violated.Contains(validation) && (o.Violated.Count == 1 || !Isolable(target));
            return LeafOf(o, a) == target.Leaf;
        }

        /// <summary>Existe valor que viola só a validação-alvo (com a classe pedida) e mantém as outras passando.</summary>
        private bool Isolable(CoverageTarget target)
        {
            if (_isolable.TryGetValue(target, out var cached)) return cached;
            var validation = target.Leaf!.Validation!;
            List<Pred> constraints = [target.Predicate ?? validation.Violation, .. _validations.Where(v => v != validation).Select(v => Pred.Not(v.Violation))];
            return _isolable[target] = Check(constraints).Status != Feasibility.Unsat;
        }

        // ---- Cenário de cada obrigação ----

        /// <summary>
        /// Gera o cenário da obrigação: tenta as restrições legíveis (escolhas gulosas do caminho feliz) e, se não dão, as
        /// exatas do caminho. Só aceita o cenário cuja simulação atinge a folha-alvo com a classe/limite pedido.
        /// </summary>
        private (Entry? Item, string? Status, string? Reason) Generate(CoverageTarget target, Plan plan)
        {
            if (target.Leaf?.Kind == TerminalKinds.Binding) return Mismatch(target, plan);

            string? miss = null;
            var tried = new HashSet<string>();
            foreach (var candidate in Candidates(target, plan))
            {
                if (!tried.Add(string.Join("|", candidate.Constraints.Select(c => c.Key)))) continue;
                if (Solve(candidate.Constraints) is not { } assignment) continue;
                var outcome = Simulate(assignment);
                var scenario = ToScenario(candidate, assignment, outcome, plan.Conditions);
                if (candidate.Note is { } note) scenario.Notes = [.. scenario.Notes ?? [], note];
                var entry = new Entry(scenario, candidate.Constraints, candidate.HidePreconditions, assignment, outcome, null, target);

                // Variação de ramo com a mesma resposta e os mesmos efeitos do caminho principal: não é outro desfecho.
                if (target.Leaf is { Flipped: not null } flip && _principalEntry is { } principal && SameResponse(entry, principal))
                {
                    flip.EquivalentTo = _principal;
                    flip.Reason = "mesma resposta e mesmos efeitos do caminho principal";
                    _equivalentEntries[flip] = entry;
                    return (null, CoverageStatuses.Equivalent, flip.Reason);
                }
                if (!Covers(entry, target))
                {
                    miss ??= $"o payload gerado atinge \"{LeafOf(outcome, assignment).Label}\" em vez de \"{target.Leaf?.Label ?? "uma resposta de sucesso"}\"";
                    continue;
                }
                if (target.Leaf == _principal && target.Kind == CoverageTargetKinds.Outcome) _principalEntry = entry;
                return (entry, null, null);
            }
            return (null, null, miss ?? (target.Anchor.Feasibility == Feasibility.Unknown ? target.Anchor.Reason : "nenhuma combinação de valores satisfaz as restrições do cenário"));
        }

        private IEnumerable<Candidate> Candidates(CoverageTarget target, Plan plan)
        {
            var happy = plan.Happy;
            var principal = _feasibility!.Principal();
            var leaf = target.Leaf;

            switch (target.Kind)
            {
                case CoverageTargetKinds.Outcome when leaf == _principal:
                {
                    const string title = "Caminho feliz: todos os dados válidos e nenhuma regra bloqueia";
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, title, happy);
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, title, WithBranches(principal, plan.Branches));
                    break;
                }

                case CoverageTargetKinds.Branch:
                {
                    var literal = leaf?.Flipped ?? target.Predicate!;
                    var atom = literal is NotPred n ? n.Inner : literal;
                    var value = literal is not NotPred;
                    var title = $"Sucesso com {atom.Text} {(value ? "verdadeiro" : "falso")}";
                    var focus = new ScenarioFocus { Expression = atom.Text, Value = value };
                    var opposite = Pred.Not(literal).Key;
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, title, [.. happy.Where(c => c.Key != opposite), literal], focus);
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, title,
                        WithBranches([.. principal, literal], plan.Branches.Where(b => b.Key != opposite && b.Key != literal.Key)), focus);
                    break;
                }

                case CoverageTargetKinds.Loop:
                {
                    var focus = new ScenarioFocus { Expression = target.Predicate!.Text, Value = true, Detail = target.Label };
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.EquivalencePartitioning, target.Label, [.. happy, target.Predicate], focus);
                    yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.EquivalencePartitioning, target.Label,
                        WithBranches([.. principal, target.Predicate], plan.Branches), focus);
                    break;
                }

                case CoverageTargetKinds.Outcome when leaf!.Kind == TerminalKinds.Exit:
                    foreach (var exit in leaf.Exits)
                    {
                        var focus = new ScenarioFocus { Expression = exit.TriggerCode, Value = true };
                        var constraints = happy.Where(c => !plan.Avoidance.TryGetValue(exit, out var a) || c != a).ToList();
                        constraints.Add(exit.Site.Path);
                        constraints.Add(exit.Trigger);
                        yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, leaf.Label, constraints, focus);
                        yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, leaf.Label,
                            WithBranches(_feasibility.Exit(exit), plan.Branches), focus);
                    }
                    break;

                case CoverageTargetKinds.Outcome or CoverageTargetKinds.Class or CoverageTargetKinds.Boundary when leaf?.Validation is { } rule:
                {
                    var probe = target.Probe;
                    var label = probe?.Label ?? $"{rule.FieldName}: {rule.Text} violada";
                    var constraints = ValidationConstraints(rule, probe?.Constraint ?? rule.Violation, plan.Branches);
                    yield return new Candidate(ScenarioKinds.Validation, probe?.Technique ?? ScenarioTechniques.EquivalencePartitioning, label, constraints,
                        new ScenarioFocus { Expression = rule.ViolationCode, Value = true, Detail = label }, HidePreconditions: true);
                    break;
                }

                case CoverageTargetKinds.Default:
                {
                    var field = target.Field!;
                    var code = target.Predicate!.Text;
                    var shown = code[(field.Path.Length + " == ".Length)..];
                    var name = CoveragePlanner.FieldName(field);
                    var constraints = new List<Pred> { _validationsOk, target.Predicate };
                    foreach (var constraint in happy) Extend(constraints, constraint);
                    yield return new Candidate(ScenarioKinds.Validation, ScenarioTechniques.EquivalencePartitioning, target.Label, constraints,
                        new ScenarioFocus { Expression = code, Value = true, Detail = $"{name} = {shown}" },
                        Note: $"Omitir {name} no JSON tem o mesmo efeito: o campo chega com {shown} e o [Required] não falha.");
                    break;
                }

                case CoverageTargetKinds.Outcome when leaf!.Kind == TerminalKinds.Rule:
                    foreach (var rule in leaf.Rules)
                    {
                        var focus = new ScenarioFocus { Expression = rule.TriggerCode, Value = true };
                        var title = $"Regra: {Label(rule)}";
                        if (RuleConstraints(rule, plan.Branches) is { } constraints)
                            yield return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.DecisionTable, title, constraints, focus);
                        yield return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.DecisionTable, title, WithBranches(_feasibility.Rule(rule), plan.Branches), focus);
                    }
                    break;

                case CoverageTargetKinds.Boundary when leaf?.Kind == TerminalKinds.Rule:
                {
                    var rule = target.Rule!;
                    var focus = new ScenarioFocus { Expression = rule.TriggerCode, Value = true, Detail = target.Label };
                    if (RuleConstraints(rule, []) is { } constraints)
                        yield return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.BoundaryValue, target.Label, [.. constraints, target.Predicate!], focus);
                    yield return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.BoundaryValue, target.Label,
                        WithBranches([.. _feasibility.Rule(rule), target.Predicate!], plan.Branches), focus);
                    break;
                }

                case CoverageTargetKinds.Boundary:
                {
                    // Limite válido (validação ou regra do código): a operação ainda deve ser aceita.
                    var expression = target.Probe is not null ? target.Anchor.Validation!.ViolationCode : target.Rule!.TriggerCode;
                    var technique = target.Probe?.Technique ?? ScenarioTechniques.BoundaryValue;
                    var focus = new ScenarioFocus { Expression = expression, Value = false, Detail = target.Label };
                    yield return new Candidate(ScenarioKinds.Boundary, technique, target.Label, [.. happy, target.Predicate!], focus);
                    yield return new Candidate(ScenarioKinds.Boundary, technique, target.Label, WithBranches([.. principal, target.Predicate!], plan.Branches), focus);
                    break;
                }
            }
        }

        /// <summary>Restrições exatas acrescidas dos ramos que continuam satisfazíveis (mais efeitos cobertos).</summary>
        private List<Pred> WithBranches(List<Pred> constraints, IEnumerable<Pred> branches)
        {
            var result = new List<Pred>(constraints);
            foreach (var branch in branches) Extend(result, branch);
            return result;
        }

        /// <summary>Partição inválida de uma validação, com as outras validações passando (uma falha por cenário quando possível).</summary>
        private List<Pred> ValidationConstraints(ValidationRule rule, Pred violation, List<Pred> branches)
        {
            var others = _validations.Where(v => v != rule).ToList();
            var constraints = new List<Pred> { violation };
            // Outras regras do mesmo campo podem falhar juntas ("" viola [Required] e [StringLength(7, MinimumLength = 7)]).
            if (Solve([.. constraints, .. others.Select(o => Pred.Not(o.Violation))]) is not null)
                constraints.AddRange(others.Select(o => Pred.Not(o.Violation)));
            else
                constraints.AddRange(others.Where(o => o.Field != rule.Field).Select(o => Pred.Not(o.Violation)));
            foreach (var branch in branches.Where(b => Evaluator.VarsOf(b).All(v => v.Origin == VarOrigin.Input))) Extend(constraints, branch);
            return constraints;
        }

        /// <summary>A regra disparando, com o trecho alcançado e as regras/saídas anteriores passando (escolhas legíveis primeiro).</summary>
        private List<Pred>? RuleConstraints(CodeRule rule, List<Pred> branches)
        {
            var constraints = new List<Pred> { _validationsOk, rule.Site.Path, rule.Trigger };
            if (Solve(constraints) is null) return null;
            foreach (var previous in _rules.TakeWhile(r => r != rule))
                if (Extend(constraints, Pred.Not(previous.Trigger), Pred.Not(Pred.And(previous.Site.Path, previous.Trigger))) is null) return null;
            foreach (var exit in _exits.Where(e => FlowModel.Blocks(e, rule.Site.Order)))
                if (Extend(constraints, Pred.Not(exit.Trigger), Pred.Not(Pred.And(exit.Site.Path, exit.Trigger))) is null) return null;
            foreach (var branch in branches) Extend(constraints, branch);
            return constraints;
        }

        /// <summary>
        /// Mesma resposta e mesmo conjunto de efeitos: status, sucesso/erro, persistência e os trechos com efeito que
        /// executam (não só a descrição agregada: o INSERT detectado na criação aparece mesmo quando o Add do ramo não executa).
        /// </summary>
        private bool SameResponse(Entry a, Entry b) =>
            a.Outcome.Success == b.Outcome.Success && a.Outcome.Status == b.Outcome.Status
            && a.Outcome.Rule == b.Outcome.Rule && a.Scenario.Expected.Persisted == b.Scenario.Expected.Persisted
            && EffectSignature(a.Outcome, a.Assignment) == EffectSignature(b.Outcome, b.Assignment)
            && Happened(a.Outcome, a.Assignment).Select(e => e.Effect).ToHashSet().SetEquals(Happened(b.Outcome, b.Assignment).Select(e => e.Effect));

        /// <summary>
        /// Caminho feliz: validações passam; para cada regra, na ordem, o gatilho é falso (ou o trecho não é alcançado);
        /// saídas antecipadas evitadas; ramos opcionais ativados quando possível (mais efeitos cobertos).
        /// </summary>
        private List<Pred> HappyConstraints(out List<Pred> branches, out Dictionary<EarlyExit, Pred> avoidance)
        {
            var happy = new List<Pred> { _validationsOk };
            foreach (var rule in _rules)
            {
                if (Extend(happy, Pred.Not(rule.Trigger), Pred.Not(Pred.And(rule.Site.Path, rule.Trigger))) is null)
                {
                    _notes.Add($"Nenhum caminho de sucesso: a regra \"{rule.Message ?? rule.TriggerCode}\" sempre bloqueia com as regras anteriores satisfeitas.");
                    break;
                }
            }

            avoidance = [];
            foreach (var exit in _exits)
                if (Extend(happy, Pred.Not(exit.Trigger), Pred.Not(Pred.And(exit.Site.Path, exit.Trigger))) is { } chosen)
                    avoidance[exit] = chosen;

            branches = [];
            foreach (var literal in BranchLiterals())
                if (Extend(happy, literal) is not null) branches.Add(literal);
            return happy;
        }

        /// <summary>Acrescenta a primeira alternativa que mantém as restrições satisfazíveis.</summary>
        private Pred? Extend(List<Pred> constraints, params Pred[] alternatives)
        {
            foreach (var alternative in alternatives)
                if (Solve([.. constraints, alternative]) is not null)
                {
                    constraints.Add(alternative);
                    return alternative;
                }
            return null;
        }

        /// <summary>Condições dos trechos com efeitos (if, ternário, ?. ...) que não são gatilho de regra: os ramos da tabela de decisão.</summary>
        private List<Pred> BranchLiterals()
        {
            var ruleAtoms = _rules.SelectMany(r => Evaluator.Atoms(r.Trigger)).Concat(_exits.SelectMany(x => Evaluator.Atoms(x.Trigger)))
                .Select(a => a.Key).ToHashSet();
            var literals = new List<Pred>();
            foreach (var effect in _flow.Effects.OrderBy(e => e.Site.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare)))
                foreach (var conjunct in Evaluator.Conjuncts(effect.Site.Path))
                {
                    var atom = conjunct is NotPred n ? n.Inner : conjunct;
                    if (atom is AndPred or OrPred or ConstPred or OpaquePred or ModelValidPred) continue;
                    if (ruleAtoms.Contains(atom.Key) || literals.Any(l => (l is NotPred ln ? ln.Inner : l).Key == atom.Key)) continue;
                    literals.Add(conjunct);
                }
            return literals;
        }

        /// <summary>Campo do body que recebe texto no cenário de tipo inválido (model binding).</summary>
        private InputField? MismatchField()
        {
            if (!_flow.ApiController && !_flow.Rules.Any(r => r.IsModelStateCheck)) return null;
            return _input.Roots.Where(r => r.Location == InputLocations.Body).SelectMany(r => r.Children)
                .FirstOrDefault(f => f.Kind is VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Bool or VarKind.Guid || (f.Kind == VarKind.Enum && !_solution.EnumsAsStrings));
        }

        /// <summary>
        /// Partição inválida de tipo: texto num campo numérico/data/bool/enum do body. O model binding falha e o
        /// ModelState fica inválido (400 automático com [ApiController], ou o if (!ModelState.IsValid) do controller).
        /// </summary>
        private (Entry? Item, string? Status, string? Reason) Mismatch(CoverageTarget target, Plan plan)
        {
            var field = target.Leaf!.BindingField!;
            if (_principalEntry is not { Scenario.Request.Body: System.Text.Json.Nodes.JsonObject body } happy)
                return (null, null, "precisa do payload do caminho principal para trocar o tipo do campo");
            if (!body.ContainsKey(field.Name)) return (null, null, $"{field.Name} não aparece no payload do caminho principal");

            var modelState = _flow.Rules.FirstOrDefault(r => r.IsModelStateCheck);
            var mutated = (System.Text.Json.Nodes.JsonObject)body.DeepClone();
            mutated[field.Name] = "abc";
            var (status, source) = ValidationStatus();
            var request = happy.Scenario.Request;
            var scenario = new Scenario
            {
                Kind = ScenarioKinds.Validation,
                Technique = ScenarioTechniques.EquivalencePartitioning,
                Title = $"{field.Name} com tipo inválido (texto em vez de {field.TypeDisplay})",
                Focus = new ScenarioFocus { Expression = $"{field.Path} com tipo inválido", Value = true, Detail = $"{field.Name} = \"abc\"" },
                Request = new ScenarioRequest { Method = request.Method, Url = request.Url, Route = request.Route, Query = request.Query, Headers = request.Headers, Body = mutated },
                Expected = new ScenarioExpectation
                {
                    Outcome = "erro",
                    HttpStatus = _flow.ApiController ? 400 : modelState?.Status ?? status,
                    StatusSource = _flow.ApiController ? "validacao" : source,
                    Messages = [$"The JSON value could not be converted to {field.Type.ToDisplayString()}. Path: $.{field.Name}"],
                    Rule = "model binding: tipo do JSON incompatível",
                },
                Decisions = plan.Conditions.ToDictionary(c => c.Condition.Id, _ => (bool?)null),
                Notes = ["Mensagem do System.Text.Json (pode variar com a versão e a configuração do JSON)."],
            };
            return (happy with { Scenario = scenario, HidePreconditions = true, MismatchField = field, Target = target }, null, null);
        }

        /// <summary>Mensagem da regra com os trechos constantes resolvidos ($"{pPropriedade} não existe" → "EnderecoId não existe"), ou o gatilho.</summary>
        private string Label(CodeRule rule)
        {
            if (rule.MessageSyntax is not { } interpolated) return rule.Message ?? rule.TriggerCode;
            var scope = _resolver.ScopeOf(rule.Site.Method);
            return string.Concat(interpolated.Contents.Select(c => c switch
            {
                InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                InterpolationSyntax hole when _resolver.Term(hole.Expression, scope) is ConstTerm { Text: { } constant } => constant,
                _ => c.ToString(),
            }));
        }

        /// <summary>
        /// Confere a matriz com as regras do relatório (if/throw/guarda dos métodos do fluxo): as regras e saídas do fluxo
        /// já têm linha (uma por folha); sobra avisar a regra que encerra o fluxo e não foi traduzida em gatilho.
        /// </summary>
        private void CodeConditionNotes()
        {
            foreach (var condition in _context.Conditions.Where(c => c.Kind != ConditionKinds.Validation))
            {
                bool At(Site site) => site.Method.SourceFile == condition.SourceFile && site.DeclaredLine == condition.SourceLine;
                var where = $"{condition.SourceFile}:{condition.SourceLine}";
                if (_notes.Any(n => n.Contains(where, StringComparison.Ordinal))) continue;
                if (_flow.Rules.Any(r => At(r.Site)) || _flow.Exits.Any(x => At(x.Site)) || _flow.Delegated.Any(d => At(d.Site))) continue;

                // Regra do relatório que encerra o fluxo e não foi traduzida em gatilho.
                if (condition.Action.StartsWith("throw", StringComparison.Ordinal) || condition.Action.StartsWith("notifica", StringComparison.Ordinal))
                    _notes.Add($"Regra \"{condition.Expression}\" ({where}) não foi traduzida em cenário: {condition.Action}.");
            }
        }

        // ---- Solver ----

        private Assignment? Solve(IEnumerable<Pred> constraints) => Check(constraints).Assignment;

        /// <summary>SAT/UNSAT/UNKNOWN das restrições (com ModelState expandido), com cache.</summary>
        private SolveResult Check(IEnumerable<Pred> constraints)
        {
            var list = constraints.Select(Expand).ToList();
            var key = string.Join("|", list.Select(p => p.Key));
            if (_solved.TryGetValue(key, out var cached)) return cached;
            return _solved[key] = _solver.Check(list);
        }

        /// <summary>ModelState.IsValid = todas as validações de entrada passam.</summary>
        private Pred Expand(Pred p) => p switch
        {
            ModelValidPred => _validationsOk,
            NotPred n => Pred.Not(Expand(n.Inner)),
            AndPred a => Pred.And(a.Items.Select(Expand)),
            OrPred o => Pred.Or(o.Items.Select(Expand)),
            _ => p,
        };

        private bool? Eval(Pred p, Assignment a) => Evaluator.Eval(Expand(p), a);

        // ---- Simulação: o que acontece com estes valores ----

        private Outcome Simulate(Assignment a)
        {
            var notes = new List<string>();

            var violated = _validations.Where(v => Eval(v.Violation, a) == true).ToList();
            if (violated.Count > 0)
            {
                var (status, source) = ValidationStatus();
                if (violated.Any(v => v.Origin == "FluentValidation") && !_solution.FluentAutoValidation)
                    notes.Add("FluentValidation sem validação automática registrada: confira se o validator é executado.");
                if (violated.Any(v => v.Message is null))
                    notes.Add("Mensagem padrão do ASP.NET Core/FluentValidation (pode estar traduzida ou customizada).");
                var first = violated[0];
                return new Outcome(false, status, source, violated.Select(v => v.MessageOrDefault).Distinct().ToList(), null,
                    $"{first.FieldName}: {first.Text}", first.Source, null, [], true, notes) { Violated = violated };
            }

            var events = _rules.Select(r => (Order: r.Site.Order, Rule: (CodeRule?)r, Exit: (EarlyExit?)null))
                .Concat(_exits.Select(e => (Order: e.Site.Order, Rule: (CodeRule?)null, Exit: (EarlyExit?)e)))
                .OrderBy(e => e.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare))
                .ToList();
            var exits = new List<EarlyExit>();

            foreach (var (order, rule, exit) in events)
            {
                if (exits.Any(x => FlowModel.Blocks(x, order))) continue;
                var reached = Eval(rule?.Site.Path ?? exit!.Site.Path, a);
                var fires = Eval(rule?.Trigger ?? exit!.Trigger, a);
                if (reached == false || fires == false) continue;
                if (reached is null || fires is null)
                {
                    notes.Add($"Não foi possível avaliar \"{rule?.TriggerCode ?? exit!.TriggerCode}\": considerado falso.");
                    continue;
                }

                if (rule is not null)
                {
                    if (rule.StatusNote is { } note) notes.Add(note);
                    return new Outcome(false, rule.Status, rule.StatusSource, Message(rule, a) is { } message ? [message] : [], rule.Exception,
                        rule.TriggerCode, rule.Site.Source, order, exits, false, notes) { Fired = rule };
                }

                exits.Add(exit!);
                if (exit!.InEntry)
                    return new Outcome(true, exit.Status ?? _flow.SuccessStatus, exit.Status is null ? _flow.SuccessStatusSource : "código", [], null, null, null, order, exits, false, notes);
            }

            return new Outcome(true, _flow.SuccessStatus, _flow.SuccessStatusSource, [], null, null, null, null, exits, false, notes);
        }

        private (int Status, string Source) ValidationStatus()
        {
            if (_flow.ApiController) return (400, "validacao");
            return _flow.Rules.FirstOrDefault(r => r.IsModelStateCheck) is { Status: { } status } ? (status, "código") : (400, "inferido");
        }

        /// <summary>Mensagem da regra com os valores do cenário nos trechos interpolados.</summary>
        private string? Message(CodeRule rule, Assignment a)
        {
            if (rule.MessageSyntax is { } interpolated)
            {
                var scope = _resolver.ScopeOf(rule.Site.Method);
                return string.Concat(interpolated.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                    InterpolationSyntax hole when _resolver.Term(hole.Expression, scope) is VarTerm { Var.Origin: VarOrigin.Input } v
                        => _payload.Raw(v.Var, a.Get(v.Var)) ?? "null",
                    // $"{pPropriedade} não existe" com Existe(id, nameof(request.EnderecoId), ...): o texto da chamada.
                    InterpolationSyntax hole when _resolver.Term(hole.Expression, scope) is ConstTerm { Text: { } text } => text,
                    _ => c.ToString(),
                }));
            }
            return rule.Message;
        }

        /// <summary>Trechos com efeito que executam (true) ou podem executar (null) com estes valores.</summary>
        private List<(EffectSite Effect, bool? Value)> Happened(Outcome outcome, Assignment a) => _flow.Effects.Where(e =>
                (outcome.StopOrder is null || FlowModel.Compare(e.Site.Order, outcome.StopOrder) < 0)
                && !outcome.Exits.Any(x => FlowModel.Blocks(x, e.Site.Order)))
            .Select(e => (Effect: e, Value: outcome.ValidationFailed ? false : Eval(e.Site.Path, a)))
            .Where(e => e.Value != false)
            .ToList();

        private List<ScenarioEffect> Effects(Outcome outcome, Assignment a, out bool persisted)
        {
            var happened = Happened(outcome, a);

            persisted = happened.Any(e => e.Effect.Kind == SinkKinds.SaveChanges && e.Value == true);
            var result = new List<ScenarioEffect>();

            foreach (var group in happened.Where(e => e.Effect.Change is not null).GroupBy(e => e.Effect.Change!))
            {
                // UPDATE detectado pelas atribuições: acontece se algum campo mudou, mesmo que o primeiro não.
                if (!outcome.Success && !persisted) continue;
                result.Add(new ScenarioEffect
                {
                    Kind = group.Key.Operation,
                    Target = group.Key.Entity,
                    Description = $"{group.Key.Entity} {group.Key.Operation}",
                    Properties = group.Where(e => e.Effect.Property is not null).Select(e => $"{e.Effect.Property!.Property} = {e.Effect.Property.Value}").Distinct().ToList() is { Count: > 0 } p ? p : null,
                    Uncertain = group.Any(e => e.Value is null && e.Effect.Property is null),
                });
            }

            foreach (var e in happened.Where(e => e.Effect.Change is null).DistinctBy(e => e.Effect.Description))
                result.Add(new ScenarioEffect { Kind = e.Effect.Kind, Target = e.Effect.Target, Description = e.Effect.Description, Uncertain = e.Value is null });

            return result;
        }

        // ---- Saída ----

        private Scenario ToScenario(Candidate candidate, Assignment a, Outcome outcome, List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> conditions)
        {
            var effects = Effects(outcome, a, out var persisted);
            var notes = new List<string>(outcome.Notes);
            var preconditions = candidate.HidePreconditions || outcome.ValidationFailed ? [] : Preconditions(ReadByPath(outcome, a), a, notes);

            return new Scenario
            {
                Kind = candidate.Kind,
                Technique = candidate.Technique,
                Title = candidate.Title,
                Focus = candidate.Focus,
                Request = _payload.Build(a),
                Preconditions = preconditions,
                Expected = new ScenarioExpectation
                {
                    Outcome = outcome.Success ? "sucesso" : "erro",
                    HttpStatus = outcome.Status,
                    StatusSource = outcome.StatusSource,
                    Messages = outcome.Messages,
                    Exception = outcome.Exception,
                    Rule = outcome.Rule,
                    Source = outcome.Source,
                    Effects = effects,
                    Persisted = persisted,
                },
                Decisions = conditions.ToDictionary(c => c.Condition.Id, c => c.Value(a, outcome)),
                Solver = a.UsedZ3 ? "z3" : "direto",
                Notes = notes.Count == 0 ? null : notes.Distinct().ToList(),
            };
        }

        /// <summary>Estado necessário: consultas e valores de contexto citados nas restrições do cenário, com os valores escolhidos.</summary>
        private List<ScenarioPrecondition> Preconditions(List<Pred> constraints, Assignment a, List<string> notes)
        {
            var expanded = constraints.Select(Expand).ToList();
            var vars = expanded.SelectMany(Evaluator.VarsOf).Where(v => v.Origin != VarOrigin.Input).ToList();
            var result = new List<ScenarioPrecondition>();

            foreach (var group in vars.SelectMany(v => Lineage(v)).Distinct().GroupBy(v => v.Root!.Key))
            {
                var root = group.First().Root!;
                var rootVar = _vars.All.FirstOrDefault(v => v.Root?.Key == root.Key && v.Member is null);
                var expression = root.Render(a);
                var kind = root.Origin == VarOrigin.Context ? "contexto" : "estado";
                string description;

                if (rootVar is null || rootVar.Kind != VarKind.Object)
                {
                    var value = rootVar is null ? null : Values.Format(rootVar, a.Get(rootVar));
                    description = rootVar?.Kind == VarKind.Bool ? $"{expression} retorna {value}" : $"{expression} = {value}";
                }
                else if (a.Get(rootVar).IsNull)
                    description = root.Origin == VarOrigin.Context ? $"{expression} é null" : $"Não existe {root.TypeName.TrimEnd('?')} para {expression}";
                else
                {
                    var members = group.Where(v => v.Member is not null && !group.Any(o => o.Parent == v))
                        .Select(v => $"{v.Member} = {Values.Format(v, a.Get(v))}").ToList();
                    description = root.Origin == VarOrigin.Context
                        ? $"{expression}{(members.Count > 0 ? ": " + string.Join(", ", members) : "")}"
                        : $"Existe {root.TypeName.TrimEnd('?')} retornado por {expression}{(members.Count > 0 ? " com " + string.Join(", ", members) : "")}";
                }

                result.Add(new ScenarioPrecondition { Kind = kind, Description = description, Expression = expression, Source = root.Source });
            }

            foreach (var opaque in expanded.SelectMany(Evaluator.Atoms).OfType<OpaquePred>().DistinctBy(o => o.Key))
            {
                if (!a.Assumptions.TryGetValue(opaque.Key, out var value)) continue;
                // Sem exceção é o funcionamento normal: só o cenário da exceção precisa prepará-la.
                if (!value && opaque.Text.StartsWith("exceção capturada", StringComparison.Ordinal)) continue;
                result.Add(new ScenarioPrecondition
                {
                    Kind = "suposicao",
                    Description = $"Garantir que {opaque.Text} seja {(value ? "verdadeiro" : "falso")} (condição não traduzida automaticamente)",
                    Expression = opaque.Text,
                });
                notes.Add($"Condição não resolvida: {opaque.Text}");
            }
            return result;
        }

        /// <summary>
        /// Condições que o fluxo realmente avalia neste cenário: gatilhos e caminhos das regras e saídas alcançadas e os
        /// caminhos dos efeitos que acontecem. O estado citado nelas é o que precisa ser preparado para o teste.
        /// </summary>
        private List<Pred> ReadByPath(Outcome outcome, Assignment a)
        {
            var preds = new List<Pred>();
            foreach (var rule in _rules.Where(r => Reached(r.Site, a, outcome)))
                preds.AddRange([rule.Site.Path, rule.Trigger]);
            foreach (var exit in _exits.Where(e => Reached(e.Site, a, outcome)))
                preds.AddRange([exit.Site.Path, exit.Trigger]);
            foreach (var effect in _flow.Effects.Where(e => Reached(e.Site, a, outcome)))
                preds.Add(effect.Site.Path);
            return preds;
        }

        private static IEnumerable<Var> Lineage(Var v)
        {
            for (var x = v; x is not null; x = x.Parent) yield return x;
        }

        private static bool IsPresenceRule(string kind) => kind is "required" or "notNull" or "notEmpty";

        private List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> DecisionConditions(List<Pred> branches)
        {
            var list = new List<(DecisionCondition, Func<Assignment, Outcome, bool?>)>();
            string NextId() => $"D{list.Count + 1}";

            foreach (var v in _validations)
                list.Add((new DecisionCondition
                {
                    Id = NextId(), Kind = "validacao", Expression = v.ViolationCode, Label = v.MessageOrDefault, Source = v.Source,
                    Validation = new DecisionValidation
                    {
                        Field = v.Field.Member,
                        Owner = v.Field.Parent?.Type.Name,
                        Kind = v.Kind,
                        Min = v.MinText,
                        Max = v.MaxText,
                        Collection = v.Var.Kind == VarKind.Collection,
                        // As demais regras passam com null: só valem quando o campo é informado, se nada o torna obrigatório.
                        Optional = !IsPresenceRule(v.Kind) && !_validations.Any(o => o.Field == v.Field && IsPresenceRule(o.Kind)),
                        When = v.When is null ? null : v.WhenCode ?? v.When.Text,
                        Origin = v.Origin,
                    },
                }, (a, _) => Eval(v.Violation, a)));

            foreach (var rule in _rules)
                list.Add((new DecisionCondition
                {
                    Id = NextId(), Kind = "regra", Expression = rule.TriggerCode, Label = rule.Message is null ? null : Label(rule),
                    RegistryId = RegistryId(rule.TriggerCode), Source = rule.Site.Source,
                    Rule = new DecisionRule { Kind = rule.Kind, Exception = rule.Exception, HttpStatus = rule.Status },
                }, (a, o) => Reached(rule.Site, a, o) ? Eval(rule.Trigger, a) : null));

            foreach (var branch in branches)
            {
                var atom = branch is NotPred n ? n.Inner : branch;
                var site = _flow.Effects.FirstOrDefault(e => Evaluator.Conjuncts(e.Site.Path).Any(c => c.Key == branch.Key))?.Site;
                list.Add((new DecisionCondition { Id = NextId(), Kind = "ramo", Expression = atom.Text, RegistryId = RegistryId(atom.Text), Source = site?.Source },
                    (a, o) => o.ValidationFailed ? null : Eval(atom, a)));
            }

            foreach (var exit in _exits)
                list.Add((new DecisionCondition { Id = NextId(), Kind = "ramo", Expression = exit.TriggerCode, Label = exit.InEntry ? $"responde {exit.Status ?? _flow.SuccessStatus}" : "encerra antes, sem erro", RegistryId = RegistryId(exit.TriggerCode), Source = exit.Site.Source },
                    (a, o) => Reached(exit.Site, a, o) ? Eval(exit.Trigger, a) : null));

            return list;
        }

        /// <summary>O trecho foi avaliado neste cenário: validações passaram, nenhuma regra anterior parou o fluxo e o caminho leva até ele.</summary>
        private bool Reached(Site site, Assignment a, Outcome o) =>
            !o.ValidationFailed
            && (o.StopOrder is null || FlowModel.Compare(site.Order, o.StopOrder) <= 0)
            && !o.Exits.Any(x => x.Site != site && FlowModel.Blocks(x, site.Order))
            && Eval(site.Path, a) == true;

        private string? RegistryId(string code)
        {
            string? negated = null;
            try { negated = SyntaxConditions.Negate(SyntaxFactory.ParseExpression(code)); } catch { /* texto que não é expressão */ }
            return _context.ConditionRegistry.FirstOrDefault(r => r.Value == code || r.Value == negated || r.Value == $"({code})").Key;
        }

        private List<ScenarioInput> Inputs()
        {
            var result = new List<ScenarioInput>();
            foreach (var field in _input.All)
            {
                if (field.Parent is null && field.Location == InputLocations.Body && field.Kind is VarKind.Object or VarKind.Collection) continue;
                var v = _vars.Input(field);
                var rules = _validations.Where(r => r.Field == field).ToList();
                var code = _rules.Where(r => Evaluator.VarsOf(r.Trigger).Contains(v)).Select(r => $"regra: {r.TriggerCode}");
                result.Add(new ScenarioInput
                {
                    Path = field.Path,
                    Name = field.Location == InputLocations.Body ? FieldName(field) : field.Name,
                    Location = field.Location,
                    Type = field.TypeDisplay,
                    Nullable = field.Nullable,
                    Required = rules.Any(r => r.Kind is "required" or "notNull" or "notEmpty" && r.When is null),
                    Constraints = rules.Select(r => r.When is null ? r.Text : $"{r.Text} quando {r.WhenCode ?? r.When.Text}").Concat(code).Distinct().ToList(),
                    EnumValues = field.EnumMembers?.Select(m => $"{m.Name} = {m.Value}").ToList(),
                });
            }
            return result;
        }

        private static string FieldName(InputField field) => CoveragePlanner.FieldName(field);
    }
}
