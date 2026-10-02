using System.Globalization;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

public interface IScenarioGenerator
{
    Task<ScenarioMatrix> GenerateAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fase 4 (tecnologias-fases-geracao-payloads.md): gera a matriz de cenários do endpoint de forma determinística.
/// <list type="bullet">
/// <item>Tabela de decisão: caminho feliz, uma linha por regra que bloqueia (com as regras anteriores passando),
/// variações dos ramos que mudam os efeitos e as saídas antecipadas.</item>
/// <item>Partição de equivalência: classes inválidas de cada validação (ausente, vazio, só espaços, formato, fora do enum).</item>
/// <item>Valores-limite: os dois lados de cada limite (MaxLength, Range, comparações do código com constantes).</item>
/// </list>
/// Cada cenário é resolvido pelo solver (o Z3 só nas restrições complexas) e o resultado esperado vem da simulação
/// do fluxo com os valores encontrados: a primeira regra que dispara, ou o sucesso com os efeitos que acontecem.
/// </summary>
public class ScenarioGenerator : IScenarioGenerator
{
    public const int MaxScenarios = 80;

    public async Task<ScenarioMatrix> GenerateAsync(CallGraph graph, EndpointAnalysisContext context, CancellationToken cancellationToken = default)
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
            return new Run(context, input, vars, resolver, validations, flow, solution).Execute();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ScenarioMatrix { Notes = [$"Não foi possível gerar os cenários: {ex.Message}"] };
        }
    }

    private sealed record Candidate(string Kind, string Technique, string Title, List<Pred> Constraints, ScenarioFocus? Focus = null, bool HidePreconditions = false);

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
        List<string> Notes);

    private sealed class Run
    {
        private readonly EndpointAnalysisContext _context;
        private readonly InputModel _input;
        private readonly VarTable _vars;
        private readonly SymbolicResolver _resolver;
        private readonly List<ValidationRule> _validations;
        private readonly FlowModel _flow;
        private readonly ExceptionStatusMap _solution;
        private readonly ConstraintSolver _solver = new();
        private readonly PayloadBuilder _payload;
        private readonly Pred _validationsOk;
        private readonly List<CodeRule> _rules;
        private readonly List<EarlyExit> _exits;
        private readonly Dictionary<string, Assignment?> _solved = [];
        private readonly List<string> _notes = [];

        public Run(EndpointAnalysisContext context, InputModel input, VarTable vars, SymbolicResolver resolver,
            List<ValidationRule> validations, FlowModel flow, ExceptionStatusMap solution)
        {
            _context = context;
            _input = input;
            _vars = vars;
            _resolver = resolver;
            _validations = validations;
            _flow = flow;
            _solution = solution;
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
            var candidates = new List<Candidate>();
            var happy = HappyConstraints(out var branches, out var avoidance);

            if (Solve(happy) is not null)
            {
                candidates.Add(new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable, "Caminho feliz: todos os dados válidos e nenhuma regra bloqueia", happy));
                candidates.AddRange(Variants(happy, branches));
                candidates.AddRange(EarlyExits(happy, avoidance));
            }
            candidates.AddRange(ValidationCandidates(happy, branches));
            candidates.AddRange(RuleCandidates(branches));
            candidates.AddRange(BoundaryCandidates(happy));

            var conditions = DecisionConditions(branches);
            var scenarios = new List<Scenario>();
            var seen = new HashSet<string>();
            var effectSignatures = new HashSet<string>();
            foreach (var candidate in candidates)
            {
                if (scenarios.Count >= MaxScenarios)
                {
                    _notes.Add($"Limite de {MaxScenarios} cenários atingido: os demais não foram gerados.");
                    break;
                }
                if (Solve(candidate.Constraints) is not { } assignment) continue;
                var outcome = Simulate(assignment);
                var scenario = ToScenario(candidate, assignment, outcome, conditions);

                // Variação que não muda nada em relação ao que já existe não é um cenário novo.
                var effects = $"{outcome.Success}|{outcome.Status}|{string.Join(";", scenario.Expected.Effects.Select(e => e.Description + string.Join(",", e.Properties ?? [])))}";
                if (candidate.Kind == ScenarioKinds.Success && candidate.Focus is not null && effectSignatures.Contains(effects)) continue;
                if (!seen.Add(Signature(scenario))) continue;
                if (candidate.Kind == ScenarioKinds.Success) effectSignatures.Add(effects);
                scenarios.Add(scenario);
            }

            if (scenarios.Count < MaxScenarios && TypeMismatch(scenarios.FirstOrDefault(s => s.Kind == ScenarioKinds.Success), conditions) is { } mismatch)
                scenarios.Add(mismatch);

            var order = new[] { ScenarioKinds.Success, ScenarioKinds.Validation, ScenarioKinds.Rule, ScenarioKinds.Boundary };
            scenarios = scenarios.OrderBy(s => Array.IndexOf(order, s.Kind)).ToList();
            for (var i = 0; i < scenarios.Count; i++) scenarios[i].Id = $"CEN-{i + 1:00}";

            if (_solver.Z3Error is { } z3) _notes.Add($"Z3 indisponível ({z3}): restrições com mais de uma variável ficaram sem cenário.");
            if (!_solution.FluentAutoValidation && _validations.Any(v => v.Origin == "FluentValidation"))
                _notes.Add("Validação automática do FluentValidation (AddFluentValidationAutoValidation) não encontrada: confira se o validator é executado antes da action.");
            _notes.AddRange(_flow.Notes);

            return new ScenarioMatrix
            {
                Inputs = Inputs(),
                Conditions = conditions.Select(c => c.Condition).ToList(),
                Scenarios = scenarios,
                Notes = _notes.Distinct().ToList(),
                EnumsAsStrings = _solution.EnumsAsStrings,
            };
        }

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

        private IEnumerable<Candidate> Variants(List<Pred> happy, List<Pred> branches)
        {
            foreach (var branch in branches)
            {
                var flipped = Pred.Not(branch);
                var constraints = happy.Where(c => c != branch).Append(flipped).ToList();
                var atom = branch is NotPred n ? n.Inner : branch;
                var value = flipped is not NotPred;
                yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable,
                    $"Sucesso com {atom.Text} {(value ? "verdadeiro" : "falso")}", constraints, new ScenarioFocus { Expression = atom.Text, Value = value });
            }
        }

        private IEnumerable<Candidate> EarlyExits(List<Pred> happy, Dictionary<EarlyExit, Pred> avoidance)
        {
            foreach (var exit in _exits)
            {
                var constraints = happy.Where(c => !avoidance.TryGetValue(exit, out var a) || c != a).ToList();
                constraints.Add(exit.Site.Path);
                constraints.Add(exit.Trigger);
                yield return new Candidate(ScenarioKinds.Success, ScenarioTechniques.DecisionTable,
                    $"Encerra antes, sem erro, quando {exit.TriggerCode}", constraints, new ScenarioFocus { Expression = exit.TriggerCode, Value = true });
            }
        }

        /// <summary>Partições inválidas de cada validação, com as outras validações passando (uma falha por cenário).</summary>
        private IEnumerable<Candidate> ValidationCandidates(List<Pred> happy, List<Pred> branches)
        {
            var inputOnly = branches.Where(b => Evaluator.VarsOf(b).All(v => v.Origin == VarOrigin.Input)).ToList();
            foreach (var rule in _validations)
            {
                foreach (var probe in rule.Probes().Where(p => !p.Valid))
                {
                    var others = _validations.Where(v => v != rule).ToList();
                    var constraints = new List<Pred> { probe.Constraint };
                    // Outras regras do mesmo campo podem falhar juntas ("" viola [Required] e [StringLength(7, MinimumLength = 7)]).
                    if (Solve([.. constraints, .. others.Select(o => Pred.Not(o.Violation))]) is not null)
                        constraints.AddRange(others.Select(o => Pred.Not(o.Violation)));
                    else
                        constraints.AddRange(others.Where(o => o.Field != rule.Field).Select(o => Pred.Not(o.Violation)));
                    foreach (var branch in inputOnly) Extend(constraints, branch);

                    yield return new Candidate(ScenarioKinds.Validation, probe.Technique, probe.Label, constraints,
                        new ScenarioFocus { Expression = rule.ViolationCode, Value = true, Detail = probe.Label }, HidePreconditions: true);
                }
            }
        }

        /// <summary>
        /// Partição inválida de tipo: texto num campo numérico/data/bool/enum do body. O model binding falha e o
        /// ModelState fica inválido (400 automático com [ApiController], ou o if (!ModelState.IsValid) do controller).
        /// </summary>
        private Scenario? TypeMismatch(Scenario? happy, List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> conditions)
        {
            if (happy?.Request.Body is not System.Text.Json.Nodes.JsonObject body) return null;
            var modelState = _flow.Rules.FirstOrDefault(r => r.IsModelStateCheck);
            if (!_flow.ApiController && modelState is null) return null;

            var field = _input.Roots.Where(r => r.Location == InputLocations.Body).SelectMany(r => r.Children)
                .FirstOrDefault(f => f.Kind is VarKind.Int or VarKind.Decimal or VarKind.Date or VarKind.Bool or VarKind.Guid || (f.Kind == VarKind.Enum && !_solution.EnumsAsStrings));
            if (field is null || !body.ContainsKey(field.Name)) return null;

            var mutated = (System.Text.Json.Nodes.JsonObject)body.DeepClone();
            mutated[field.Name] = "abc";
            var (status, source) = ValidationStatus();
            return new Scenario
            {
                Kind = ScenarioKinds.Validation,
                Technique = ScenarioTechniques.EquivalencePartitioning,
                Title = $"{field.Name} com tipo inválido (texto em vez de {field.TypeDisplay})",
                Focus = new ScenarioFocus { Expression = $"{field.Path} com tipo inválido", Value = true, Detail = $"{field.Name} = \"abc\"" },
                Request = new ScenarioRequest { Method = happy.Request.Method, Url = happy.Request.Url, Route = happy.Request.Route, Query = happy.Request.Query, Headers = happy.Request.Headers, Body = mutated },
                Expected = new ScenarioExpectation
                {
                    Outcome = "erro",
                    HttpStatus = _flow.ApiController ? 400 : modelState?.Status ?? status,
                    StatusSource = _flow.ApiController ? "validacao" : source,
                    Messages = [$"The JSON value could not be converted to {field.Type.ToDisplayString()}. Path: $.{field.Name}"],
                    Rule = "model binding: tipo do JSON incompatível",
                },
                Decisions = conditions.ToDictionary(c => c.Condition.Id, _ => (bool?)null),
                Notes = ["Mensagem do System.Text.Json (pode variar com a versão e a configuração do JSON)."],
            };
        }

        /// <summary>Uma linha por regra: o gatilho verdadeiro, com o trecho alcançado e as regras anteriores passando.</summary>
        private IEnumerable<Candidate> RuleCandidates(List<Pred> branches)
        {
            foreach (var rule in _rules)
            {
                var constraints = new List<Pred> { _validationsOk, rule.Site.Path, rule.Trigger };
                if (Solve(constraints) is null)
                {
                    // !ModelState.IsValid sem validações declaradas: só com payload malformado (cenário de tipo inválido).
                    if (!rule.IsModelStateCheck)
                        _notes.Add($"Regra \"{rule.Message ?? rule.TriggerCode}\" ({rule.Site.Source}) não tem cenário: a condição não pode ser verdadeira nesse ponto do fluxo.");
                    continue;
                }
                var feasible = true;
                foreach (var previous in _rules.TakeWhile(r => r != rule))
                {
                    if (!feasible) break;
                    feasible = Extend(constraints, Pred.Not(previous.Trigger), Pred.Not(Pred.And(previous.Site.Path, previous.Trigger))) is not null;
                }
                foreach (var exit in _exits.Where(e => FlowModel.Blocks(e, rule.Site.Order)))
                {
                    if (!feasible) break;
                    feasible = Extend(constraints, Pred.Not(exit.Trigger), Pred.Not(Pred.And(exit.Site.Path, exit.Trigger))) is not null;
                }
                if (!feasible)
                {
                    _notes.Add($"Regra \"{rule.Message ?? rule.TriggerCode}\" ({rule.Site.Source}) não tem cenário: não é alcançável sem disparar uma regra anterior.");
                    continue;
                }
                foreach (var branch in branches) Extend(constraints, branch);

                yield return new Candidate(ScenarioKinds.Rule, ScenarioTechniques.DecisionTable,
                    rule.Message is { } message ? $"Regra: {message}" : $"Regra: {rule.TriggerCode}",
                    constraints, new ScenarioFocus { Expression = rule.TriggerCode, Value = true });
            }
        }

        /// <summary>Limites válidos: das validações (MaxLength, Range...) e das comparações do código com constantes.</summary>
        private IEnumerable<Candidate> BoundaryCandidates(List<Pred> happy)
        {
            foreach (var rule in _validations)
                foreach (var probe in rule.Probes().Where(p => p.Valid))
                    yield return new Candidate(ScenarioKinds.Boundary, probe.Technique, probe.Label, [.. happy, probe.Constraint],
                        new ScenarioFocus { Expression = rule.ViolationCode, Value = false, Detail = probe.Label });

            foreach (var rule in _rules)
            {
                var literal = rule.Trigger;
                var positive = literal is not NotPred;
                if ((literal is NotPred n ? n.Inner : literal) is not CmpPred { IsComplex: false } cmp) continue;
                if (BoundaryValue(cmp, positive) is not { } boundary) continue;
                var name = boundary.Var.Var.Field is { } field ? FieldName(field) : boundary.Var.Var.Display;
                var label = $"{name} = {boundary.Label} (limite de {rule.TriggerCode})";
                yield return new Candidate(ScenarioKinds.Boundary, ScenarioTechniques.BoundaryValue, label,
                    [.. happy, new CmpPred(boundary.Var, "==", boundary.Value, $"{boundary.Var.Var.Display} == {boundary.Label}")],
                    new ScenarioFocus { Expression = rule.TriggerCode, Value = false, Detail = label });
            }
        }

        private sealed record Boundary(VarTerm Var, Term Value, string Label);

        /// <summary>Primeiro valor do lado em que a regra não dispara: x &gt; 200 → 200; x &lt; hoje → hoje.</summary>
        private static Boundary? BoundaryValue(CmpPred cmp, bool triggerPositive)
        {
            var (variable, op, constant) = (cmp.Left, cmp.Right) switch
            {
                (VarTerm v, ConstTerm or NowTerm) => (v, cmp.Op, cmp.Right),
                (ConstTerm or NowTerm, VarTerm v) => (v, cmp.Op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => cmp.Op }, cmp.Left),
                _ => (null, null, null),
            };
            if (variable is null || variable.Var.Origin != VarOrigin.Input) return null;
            if (!variable.Var.IsNumeric && variable.Measure != Measure.Length) return null;

            // Lado válido: a negação do gatilho.
            var valid = triggerPositive ? op switch { ">" => "<=", ">=" => "<", "<" => ">=", "<=" => ">", _ => null } : op is "==" or "!=" ? null : op;
            if (valid is null) return null;

            var step = variable.Var.IsIntegral(variable.Measure) ? 1m : 0.01m;
            var delta = valid switch { ">" => step, "<" => -step, _ => 0m };
            switch (constant)
            {
                case ConstTerm { Number: { } n }:
                {
                    var value = n + delta;
                    var label = variable.Measure == Measure.Length ? $"{value} caractere(s)" : value.ToString(CultureInfo.InvariantCulture);
                    return new Boundary(variable, ConstTerm.Of(value), label);
                }
                case NowTerm now when variable.Var.Kind == VarKind.Date:
                {
                    var offset = now.OffsetDays + (int)Math.Sign(delta);
                    // DateTime.Now tem hora: o limite seguro é o dia seguinte/anterior.
                    if (now.Exact && delta == 0) offset += valid == ">=" ? 1 : -1;
                    return new Boundary(variable, new NowTerm(offset, false), Values.RelativeDay(offset));
                }
            }
            return null;
        }

        // ---- Solver ----

        private Assignment? Solve(IEnumerable<Pred> constraints)
        {
            var list = constraints.Select(Expand).ToList();
            var key = string.Join("|", list.Select(p => p.Key));
            if (_solved.TryGetValue(key, out var cached)) return cached;
            return _solved[key] = _solver.Solve(list);
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
                    $"{first.FieldName}: {first.Text}", first.Source, null, [], true, notes);
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
                        rule.TriggerCode, rule.Site.Source, order, exits, false, notes);
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
                    _ => c.ToString(),
                }));
            }
            return rule.Message;
        }

        private List<ScenarioEffect> Effects(Outcome outcome, Assignment a, out bool persisted)
        {
            var happened = _flow.Effects.Where(e =>
                    (outcome.StopOrder is null || FlowModel.Compare(e.Site.Order, outcome.StopOrder) < 0)
                    && !outcome.Exits.Any(x => FlowModel.Blocks(x, e.Site.Order)))
                .Select(e => (Effect: e, Value: outcome.ValidationFailed ? false : Eval(e.Site.Path, a)))
                .Where(e => e.Value != false)
                .ToList();

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

        private List<(DecisionCondition Condition, Func<Assignment, Outcome, bool?> Value)> DecisionConditions(List<Pred> branches)
        {
            var list = new List<(DecisionCondition, Func<Assignment, Outcome, bool?>)>();
            string NextId() => $"D{list.Count + 1}";

            foreach (var v in _validations)
                list.Add((new DecisionCondition { Id = NextId(), Kind = "validacao", Expression = v.ViolationCode, Label = v.MessageOrDefault, Source = v.Source },
                    (a, _) => Eval(v.Violation, a)));

            foreach (var rule in _rules)
                list.Add((new DecisionCondition
                {
                    Id = NextId(), Kind = "regra", Expression = rule.TriggerCode, Label = rule.Message,
                    RegistryId = RegistryId(rule.TriggerCode), Source = rule.Site.Source,
                }, (a, o) => Reached(rule.Site, a, o) ? Eval(rule.Trigger, a) : null));

            foreach (var branch in branches)
            {
                var atom = branch is NotPred n ? n.Inner : branch;
                var site = _flow.Effects.FirstOrDefault(e => Evaluator.Conjuncts(e.Site.Path).Any(c => c.Key == branch.Key))?.Site;
                list.Add((new DecisionCondition { Id = NextId(), Kind = "ramo", Expression = atom.Text, RegistryId = RegistryId(atom.Text), Source = site?.Source },
                    (a, o) => o.ValidationFailed ? null : Eval(atom, a)));
            }

            foreach (var exit in _exits)
                list.Add((new DecisionCondition { Id = NextId(), Kind = "ramo", Expression = exit.TriggerCode, Label = "encerra antes, sem erro", RegistryId = RegistryId(exit.TriggerCode), Source = exit.Site.Source },
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

        private static string FieldName(InputField field) => field.Location == InputLocations.Body ? ValidationRule.JsonPath(field) is { Length: > 0 } path ? path : field.Name : field.Name;

        private static string Signature(Scenario s) =>
            $"{s.Request.Url}|{s.Request.Body?.ToJsonString()}|{string.Join(";", s.Request.Headers ?? [])}|{string.Join(";", s.Preconditions.Select(p => p.Description))}|{s.Expected.HttpStatus}|{string.Join(";", s.Expected.Messages)}";
    }
}
