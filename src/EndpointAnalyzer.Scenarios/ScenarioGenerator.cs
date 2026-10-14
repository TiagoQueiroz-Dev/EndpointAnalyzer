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
    /// <summary>
    /// Máximo de caminhos de execução explorados por endpoint. No limite, os desfechos sem caminho encontrado ficam
    /// UNKNOWN (nunca UNSAT) e a matriz avisa.
    /// </summary>
    public int MaxPaths { get; set; } = ScenarioGenerator.MaxPaths;
}

/// <summary>
/// Fase 4 (tecnologias-fases-geracao-payloads.md): gera a matriz de cenários a partir dos desfechos reais do endpoint
/// (EndpointAnalyzer_Arvore_Geracao_Cenarios.md), e não de condições ou obrigações de cobertura:
/// <list type="number">
/// <item>Fluxo de controle interprocedural (<see cref="FlowModel"/>): regras, returns (do endpoint e de métodos internos)
/// e efeitos na ordem do código, com o valor devolvido pelos métodos chamados propagado às condições do chamador
/// (<see cref="SymbolicResolver"/>).</item>
/// <item>Pontos reais de encerramento: validação de entrada, tipo do JSON, regra que encerra com erro, return do
/// endpoint e resposta final (<see cref="TerminalOutcomeCollector.Points"/>).</item>
/// <item>Caminhos de execução até eles, com o solver podando os contraditórios (<see cref="ExecutionPathExplorer"/>).</item>
/// <item>Desfechos distintos: ponto de encerramento + efeitos executados; viabilidade SAT, UNSAT ou UNKNOWN
/// (<see cref="TerminalOutcomeCollector.Group"/>).</item>
/// <item>Um cenário por desfecho: o payload resolve as restrições de um caminho até ele e só vale se a simulação do
/// fluxo com esses valores chega ao mesmo desfecho.</item>
/// </list>
/// Classes de equivalência, limites, ramos que não mudam o desfecho e iterações de laço não geram cenários: não são
/// desfechos. O desfecho sem entrada que o alcance entra na matriz sem payload, com o motivo.
/// </summary>
public partial class ScenarioGenerator(ScenarioGenerationOptions? options = null) : IScenarioGenerator
{
    public const int MaxPaths = 512;

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

    private sealed record Candidate(string Kind, string Technique, string Title, ScenarioFocus? Focus = null, bool HidePreconditions = false);

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
        private const string HappyTitle = "Caminho feliz: todos os dados válidos e nenhuma regra bloqueia";

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
        private readonly InputField? _mismatch;
        private readonly List<CodeRule> _rules;
        private readonly List<EarlyExit> _exits;
        private readonly Dictionary<string, SolveResult> _solved = [];
        private readonly List<string> _notes = [];

        /// <summary>Restrições, valores e simulação de cada cenário gerado (por id), para a materialização em runtime.</summary>
        private readonly Dictionary<string, Entry> _entries = [];

        // ---- Pontos de encerramento e desfechos (preenchidos por Execute) ----
        private List<TerminalPoint> _points = [];
        private List<TerminalOutcome> _outcomes = [];
        private TerminalOutcome? _principal;
        private Entry? _principalEntry;
        private readonly Dictionary<ValidationRule, TerminalPoint> _byValidation = [];
        private readonly Dictionary<CodeRule, TerminalPoint> _byRule = [];
        private readonly Dictionary<EarlyExit, TerminalPoint> _byExit = [];

        private sealed record Entry(Scenario Scenario, List<Pred> Constraints, bool HidePreconditions, Assignment Assignment, Outcome Outcome,
            InputField? MismatchField, TerminalOutcome? Leaf = null);

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
            _mismatch = MismatchField();
            // if (!ModelState.IsValid) return ...: o encerramento das validações e do tipo inválido no JSON, não um desfecho à parte.
            _rules = flow.Rules.Where(r => !(r.IsModelStateCheck && (validations.Count > 0 || _mismatch is not null))).ToList();
            // Return interno só importa quando impede algum efeito ou regra (return false num helper não muda nada).
            _exits = flow.Exits.Where(x => x.InEntry || flow.Effects.Any(e => FlowModel.Blocks(x, e.Site.Order)) || _rules.Any(r => FlowModel.Blocks(x, r.Site.Order))).ToList();
        }

        // ---- Geração ----

        public ScenarioMatrix Execute()
        {
            // Fase 1 e 2: fluxo de controle, pontos reais de encerramento e os caminhos até eles.
            var context = new FlowContext(_endpoint, _input, _vars, _flow, _validations, _validationsOk, _rules, _exits, _mismatch, Math.Max(1, _options.MaxPaths));
            _points = TerminalOutcomeCollector.Points(context, Label);
            foreach (var point in _points)
            {
                if (point.Validation is { } validation) _byValidation[validation] = point;
                foreach (var rule in point.Rules) _byRule[rule] = point;
                if (point.Exit is { } exit) _byExit[exit] = point;
            }
            var explorer = new ExecutionPathExplorer(context, _points, Check);
            explorer.Explore();

            // Fase 3: desfechos distintos (ponto + efeitos) e a viabilidade de cada um.
            _outcomes = TerminalOutcomeCollector.Group(_points, explorer.Paths);
            foreach (var outcome in _outcomes) Evaluate(outcome, explorer);
            var tree = FlowTreeBuilder.Build(context, _outcomes);
            _principal = _outcomes.FirstOrDefault(o => o is { Kind: TerminalKinds.Success, IsVariant: false, Paths.Count: > 0 })
                ?? _outcomes.FirstOrDefault(o => o is { Kind: TerminalKinds.Exit, IsVariant: false, Paths.Count: > 0 });

            // Fase 4: um cenário por desfecho. O principal primeiro: o tipo inválido no JSON parte do payload dele.
            var conditions = DecisionConditions(BranchLiterals(explorer.Paths));
            var rows = new List<Row>();
            foreach (var outcome in _outcomes.OrderBy(o => o == _principal ? 0 : 1)) rows.Add(RowOf(outcome, conditions));

            // Identidade e ordem de exibição.
            var order = new[] { ScenarioKinds.Success, ScenarioKinds.Validation, ScenarioKinds.Rule };
            rows = rows.OrderBy(r => r.Leaf == _principal ? -1 : Array.IndexOf(order, r.Scenario.Kind)).ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var (scenario, entry, leaf) = rows[i];
                scenario.Id = $"CEN-{i + 1:00}";
                scenario.Key = ScenarioKey.Of(_endpoint, leaf.Key);
                scenario.Terminal = leaf.Key;
                scenario.Feasibility = leaf.Feasibility switch { Feasibility.Sat => PathFeasibilities.Sat, Feasibility.Unsat => PathFeasibilities.Unsat, _ => PathFeasibilities.Unknown };
                if (entry is not null) _entries[scenario.Id] = entry;
            }

            CodeConditionNotes();
            if (!rows.Any(r => r.Leaf.Success && r.Entry is not null) && _outcomes.FirstOrDefault(o => o.Kind == TerminalKinds.Success) is { Reason: { } blocked })
                _notes.Add($"Nenhum caminho de sucesso: {blocked}.");
            if (explorer.Truncated)
                _notes.Add($"Limite de {context.MaxPaths} caminhos de execução atingido: desfechos sem caminho encontrado ficaram como não decididos (UNKNOWN), não como inalcançáveis.");
            // Classe de entrada sem desfecho próprio: avisada, não vira cenário.
            foreach (var field in ValidationRuleExtractor.RequiredWithoutEffect(_input))
                if (field.Kind is VarKind.Int or VarKind.Decimal or VarKind.Enum or VarKind.Guid && SymbolicResolver.DefaultOf(field.Type) is { Number: { } number } value)
                    _notes.Add($"{FieldName(field)}: [Required] não tem efeito em {field.TypeDisplay} não anulável (ausente no JSON, chega como "
                        + $"{(field.Kind == VarKind.Guid ? value.Display : number.ToString(CultureInfo.InvariantCulture))} e segue o caminho principal); não é um desfecho próprio.");
            if (_solver.Z3Error is { } z3) _notes.Add($"Z3 indisponível ({z3}): restrições com mais de uma variável ficaram sem cenário.");
            if (!_solution.FluentAutoValidation && _validations.Any(v => v.Origin == "FluentValidation"))
                _notes.Add("Validação automática do FluentValidation (AddFluentValidationAutoValidation) não encontrada: confira se o validator é executado antes da action.");
            _notes.AddRange(tree.Gaps);
            _notes.AddRange(_flow.Notes);

            return new ScenarioMatrix
            {
                Inputs = Inputs(),
                Conditions = conditions.Select(c => c.Condition).ToList(),
                Scenarios = rows.Select(r => r.Scenario).ToList(),
                Notes = _notes.Distinct().ToList(),
                EnumsAsStrings = _solution.EnumsAsStrings,
                Flow = tree.ToInfo(),
            };
        }

        /// <summary>Linha da matriz: o cenário, as restrições (nulas quando não há payload) e o desfecho que ele atinge.</summary>
        private sealed record Row(Scenario Scenario, Entry? Entry, TerminalOutcome Leaf);

        /// <summary>
        /// SAT: algum caminho é satisfazível (UNKNOWN se o desfecho depende de supor uma condição não traduzida); UNKNOWN:
        /// o solver não decidiu; UNSAT: nenhum caminho chega ao ponto (com o motivo).
        /// </summary>
        private void Evaluate(TerminalOutcome outcome, ExecutionPathExplorer explorer)
        {
            var sat = outcome.Paths.FirstOrDefault(p => p.Result.Status == Feasibility.Sat);
            if (sat is not null)
            {
                outcome.Paths.Remove(sat);
                outcome.Paths.Insert(0, sat);
                outcome.Feasibility = Feasibility.Sat;
                outcome.Aggregated = sat.Aggregated;
                outcome.Reason = sat.Aggregated ? sat.Reason : null;
                var opaque = sat.Decisive.SelectMany(d => Evaluator.Atoms(Expand(d.Pred))).OfType<OpaquePred>().Select(o => o.Text).Distinct().ToList();
                if (opaque.Count > 0)
                {
                    // Há valores, mas eles dependem de supor o resultado de uma condição que não foi traduzida.
                    outcome.Feasibility = Feasibility.Unknown;
                    outcome.Reason = $"depende de condição não traduzida: {string.Join("; ", opaque)} (o cenário supõe o valor, sem prova)";
                }
                return;
            }
            if (outcome.Paths.FirstOrDefault(p => p.Result.Status == Feasibility.Unknown) is { } unknown)
            {
                outcome.Feasibility = Feasibility.Unknown;
                outcome.Reason = unknown.Reason ?? unknown.Result.Reason ?? "o solver não decidiu o caminho";
                return;
            }
            outcome.Feasibility = explorer.Truncated ? Feasibility.Unknown : Feasibility.Unsat;
            outcome.Reason = outcome.Paths.FirstOrDefault()?.Reason ?? explorer.UnreachableReason(outcome.Point);
            outcome.Paths.Clear();
        }

        /// <summary>
        /// Cenário do desfecho: resolve as restrições de um caminho até ele (primeiro na forma legível, depois na exata) e
        /// só aceita o payload cuja simulação chega ao mesmo desfecho. Sem isso, a linha fica sem payload, com o motivo.
        /// </summary>
        private Row RowOf(TerminalOutcome outcome, List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> conditions)
        {
            if (outcome.Kind == TerminalKinds.Binding)
            {
                var (mismatch, reason) = Mismatch(outcome, conditions);
                return mismatch is not null ? new Row(mismatch.Scenario, mismatch, outcome) : new Row(WithoutPayload(outcome, reason, conditions), null, outcome);
            }

            string? miss = null;
            foreach (var path in outcome.Paths.Take(4))
            {
                var exact = path.Constraints.Select(c => c.Exact).ToList();
                foreach (var constraints in new[] { Readable(path), exact }.DistinctBy(c => string.Join("|", c.Select(p => p.Key))))
                {
                    if (Solve(constraints) is not { } assignment) continue;
                    var simulated = Simulate(assignment);
                    var scenario = ToScenario(CandidateOf(outcome, path, assignment), assignment, simulated, conditions);
                    var entry = new Entry(scenario, constraints, outcome.Point.Validation is not null, assignment, simulated, null, outcome);
                    if (!Reaches(entry, outcome))
                    {
                        miss ??= $"o payload gerado atinge \"{LeafOf(simulated, assignment)?.Label ?? Describe(simulated)}\" em vez de \"{outcome.Label}\"";
                        continue;
                    }
                    if (outcome == _principal) _principalEntry = entry;
                    return new Row(scenario, entry, outcome);
                }
            }
            var noPayload = miss ?? outcome.Reason ?? "nenhuma combinação de valores satisfaz as restrições do caminho";
            return new Row(WithoutPayload(outcome, noPayload, conditions), null, outcome);
        }

        /// <summary>Restrições do caminho com as formas legíveis (¬gatilho em vez de ¬(alcançada ∧ gatilho)) quando continuam satisfazíveis.</summary>
        private List<Pred> Readable(ExecutionPath path)
        {
            var exact = path.Constraints.Select(c => c.Exact).ToList();
            var chosen = new List<Pred>();
            for (var i = 0; i < path.Constraints.Count; i++)
            {
                var constraint = path.Constraints[i];
                chosen.Add(constraint.Preferred is { } preferred && Solve([.. chosen, preferred, .. exact.Skip(i + 1)]) is not null ? preferred : constraint.Exact);
            }
            return chosen;
        }

        private Candidate CandidateOf(TerminalOutcome outcome, ExecutionPath path, Assignment assignment)
        {
            var point = outcome.Point;
            switch (point.Kind)
            {
                case TerminalKinds.Validation:
                {
                    // Um cenário por validação; o título diz qual classe o valor escolhido representa ("placa com 6 caracteres").
                    var v = point.Validation!;
                    var probe = v.Probes().FirstOrDefault(p => !p.Valid && Eval(p.Constraint, assignment) == true);
                    return new Candidate(ScenarioKinds.Validation, probe?.Technique ?? ScenarioTechniques.EquivalencePartitioning, probe?.Label ?? $"{v.FieldName}: {v.Text} violada",
                        new ScenarioFocus { Expression = v.ViolationCode, Value = true, Detail = probe?.Label }, HidePreconditions: true);
                }
                case TerminalKinds.Rule:
                {
                    var rule = point.Rules[0];
                    return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.DecisionTable, $"Regra: {Label(rule)}", new ScenarioFocus { Expression = rule.TriggerCode, Value = true });
                }
            }
            if (outcome == _principal) return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, HappyTitle);

            // Sucesso que não é o principal: o que o distingue do caminho principal (return do endpoint, return interno, ramo de efeito).
            var cause = Cause(path);
            var effects = TerminalOutcomeCollector.Describe(outcome.Missing, outcome.Extra, outcome.Effects.Count);
            var title = point.Kind == TerminalKinds.Exit
                ? $"{point.Label}{(effects.Length > 0 ? $" ({effects})" : "")}"
                : $"{(point.Kind == TerminalKinds.Success && _principal?.Point != point ? "Resposta final" : "Sucesso")}{(effects.Length > 0 ? $" {effects}" : "")}{(cause.Count > 0 ? $": {string.Join(" e ", cause.Select(Describe))}" : "")}";
            var focus = point.Exit is { } exit && _principal?.Point != point
                ? new ScenarioFocus { Expression = exit.TriggerCode, Value = true }
                : cause.FirstOrDefault() is { } first ? new ScenarioFocus { Expression = first.Code, Value = first.Value }
                : new ScenarioFocus { Expression = outcome.Label, Value = true };
            return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, title, focus);
        }

        /// <summary>Decisões do caminho que o caminho principal não toma.</summary>
        private List<Decision> Cause(ExecutionPath path)
        {
            var principal = _principal?.Paths.FirstOrDefault()?.Decisive.Select(d => (d.Pred.Key, d.Value)).ToHashSet() ?? [];
            return path.Decisive.Where(d => !principal.Contains((d.Pred.Key, d.Value))).ToList();
        }

        /// <summary>"Criar retorna quando request.Rascunho", "request.Itens.Count &gt; 0 falso".</summary>
        private string Describe(Decision decision)
        {
            if (_exits.FirstOrDefault(x => !x.InEntry && x.Trigger.Key == decision.Pred.Key) is { } exit && decision.Value)
                return $"{exit.Site.Method.Node.MethodName} retorna quando {exit.TriggerCode}";
            return $"{decision.Code} {(decision.Value ? "verdadeiro" : "falso")}";
        }

        /// <summary>Condições de efeito em que os caminhos se dividem: os ramos da tabela de decisão.</summary>
        private List<Pred> BranchLiterals(IEnumerable<ExecutionPath> paths)
        {
            var triggers = _rules.Select(r => r.Trigger.Key).Concat(_exits.Select(x => x.Trigger.Key)).Concat(_validations.Select(v => v.Violation.Key)).ToHashSet();
            return paths.SelectMany(p => p.Decisive).Where(d => !triggers.Contains(d.Pred.Key)).Select(d => d.Pred).DistinctBy(p => p.Key).ToList();
        }

        /// <summary>Linha sem payload: o desfecho como o código o declara (status, mensagem, regra), sem simulação.</summary>
        private Scenario WithoutPayload(TerminalOutcome outcome, string? reason, List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> conditions)
        {
            var point = outcome.Point;
            var rule = point.Rules.FirstOrDefault();
            var validation = point.Validation;
            var space = _endpoint.IndexOf(' ');
            return new Scenario
            {
                Kind = KindOf(point),
                Technique = point.Kind is TerminalKinds.Validation or TerminalKinds.Binding ? ScenarioTechniques.EquivalencePartitioning : ScenarioTechniques.DecisionTable,
                Title = point switch
                {
                    { Kind: TerminalKinds.Rule } => $"Regra: {Label(rule!)}",
                    { Validation: { } v } => $"{v.FieldName}: {v.Text} violada",
                    { Kind: TerminalKinds.Success } when outcome == _principal => HappyTitle,
                    _ => outcome.Label,
                },
                Focus = point switch
                {
                    { Kind: TerminalKinds.Rule } => new ScenarioFocus { Expression = rule!.TriggerCode, Value = true },
                    { Validation: { } v } => new ScenarioFocus { Expression = v.ViolationCode, Value = true },
                    { BindingField: { } field } => new ScenarioFocus { Expression = $"{field.Path} com tipo inválido", Value = true },
                    { Exit: { } exit } => new ScenarioFocus { Expression = exit.TriggerCode, Value = true },
                    _ when outcome == _principal => null,
                    _ => new ScenarioFocus { Expression = outcome.Label, Value = true },
                },
                Request = new ScenarioRequest { Method = _endpoint[..space], Url = _endpoint[(space + 1)..] },
                Expected = new ScenarioExpectation
                {
                    Outcome = point.Success ? "sucesso" : "erro",
                    HttpStatus = point.Status,
                    StatusSource = point.Kind switch
                    {
                        TerminalKinds.Validation or TerminalKinds.Binding => ValidationStatus().Source,
                        TerminalKinds.Rule => rule!.StatusSource,
                        TerminalKinds.Exit when point.Exit!.Status is not null => "código",
                        _ => _flow.SuccessStatusSource,
                    },
                    Messages = validation is not null ? [validation.MessageOrDefault] : rule?.Message is not null ? [Label(rule)] : [],
                    Exception = rule?.Exception,
                    Rule = validation is not null ? $"{validation.FieldName}: {validation.Text}"
                        : point.Kind == TerminalKinds.Binding ? "model binding: tipo do JSON incompatível"
                        : rule?.TriggerCode,
                    Source = point.Source,
                },
                Decisions = conditions.ToDictionary(c => c.Condition.Id, _ => (bool?)null),
                Solver = "nenhum",
                NoPayload = reason ?? "nenhum payload gerado atinge este desfecho",
            };
        }

        private static string KindOf(TerminalPoint point) => point.Kind switch
        {
            TerminalKinds.Validation or TerminalKinds.Binding => ScenarioKinds.Validation,
            TerminalKinds.Rule => ScenarioKinds.Rule,
            _ => ScenarioKinds.Success,
        };

        // ---- Desfecho atingido ----

        /// <summary>Desfecho que a simulação atinge: o ponto de encerramento e, no sucesso, os efeitos executados.</summary>
        private TerminalOutcome? LeafOf(Outcome o, Assignment a)
        {
            TerminalPoint point;
            if (o.ValidationFailed && o.Violated.Count > 0) point = _byValidation[o.Violated[0]];
            else if (o.Fired is { } rule && _byRule.TryGetValue(rule, out var fired)) point = fired;
            else if (o.Exits.LastOrDefault(x => x.InEntry) is { } exit && _byExit.TryGetValue(exit, out var returned)) point = returned;
            else point = _points[^1];
            if (!point.Success) return _outcomes.FirstOrDefault(x => x.Point == point);

            var executed = Happened(o, a).Where(e => e.Value == true && !Evaluator.Atoms(e.Effect.Site.Path).Any(p => p is OpaquePred)).Select(e => e.Effect);
            var signature = TerminalOutcomeCollector.Signature(executed, true);
            return _outcomes.FirstOrDefault(x => x.Point == point && x.Effects.SequenceEqual(signature));
        }

        /// <summary>O cenário chega ao desfecho: a mesma folha; na validação, só ela falha (ou falha junto com as do mesmo campo, quando agregada).</summary>
        private bool Reaches(Entry entry, TerminalOutcome outcome)
        {
            if (outcome.Point.Validation is { } validation)
                return entry.Outcome.Violated.Contains(validation) && (entry.Outcome.Violated.Count == 1 || outcome.Aggregated);
            return LeafOf(entry.Outcome, entry.Assignment) == outcome;
        }

        /// <summary>Campo do body que recebe texto no cenário de tipo inválido (model binding).</summary>
        private InputField? MismatchField()
        {
            if (!_flow.ApiController && !_flow.Rules.Any(r => r.IsModelStateCheck)) return null;
            return _input.Roots.Where(r => r.Location == InputLocations.Body).SelectMany(r => r.Children)
                .FirstOrDefault(f => f.Kind is VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Bool or VarKind.Guid || (f.Kind == VarKind.Enum && !_solution.EnumsAsStrings));
        }

        /// <summary>
        /// Tipo inválido no JSON: texto num campo numérico/data/bool/enum do body. O model binding falha e o ModelState
        /// fica inválido (400 automático com [ApiController], ou o if (!ModelState.IsValid) do controller).
        /// </summary>
        private (Entry? Item, string? Reason) Mismatch(TerminalOutcome outcome, List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> conditions)
        {
            var field = outcome.Point.BindingField!;
            if (_principalEntry is not { Scenario.Request.Body: System.Text.Json.Nodes.JsonObject body } happy)
                return (null, "precisa do payload do caminho principal para trocar o tipo do campo");
            if (!body.ContainsKey(field.Name)) return (null, $"{field.Name} não aparece no payload do caminho principal");

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
                    Source = modelState?.Site.Source,
                },
                Decisions = conditions.ToDictionary(c => c.Condition.Id, _ => (bool?)null),
                Notes = ["Mensagem do System.Text.Json (pode variar com a versão e a configuração do JSON)."],
            };
            return (happy with { Scenario = scenario, HidePreconditions = true, MismatchField = field, Leaf = outcome }, null);
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
        /// Confere a matriz com as regras do relatório (if/throw/guarda dos métodos do fluxo): as regras e returns do fluxo
        /// já têm linha (uma por desfecho); sobra avisar a regra que encerra o fluxo e não foi traduzida em gatilho.
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

        private static string FieldName(InputField field) =>
            field.Location == InputLocations.Body ? ValidationRule.JsonPath(field) is { Length: > 0 } path ? path : field.Name : field.Name;
    }
}
