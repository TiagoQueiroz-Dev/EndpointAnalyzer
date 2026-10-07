using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Matriz de cenários com as restrições de cada um (RuntimePayloadMaterializer): troca as condições abstratas pelos
/// valores reais encontrados na API em execução e devolve o payload executável. Os valores passam pelo mesmo solver
/// (e Z3) e pela mesma simulação do fluxo da geração estática: se os dados reais contradizem o cenário, ou levam a
/// outro resultado, o payload é recusado antes de qualquer request (nenhum cenário é "confirmado" por acaso).
/// </summary>
public sealed class ScenarioModel
{
    private readonly ScenarioGenerator.Run? _run;

    internal ScenarioModel(ScenarioMatrix matrix, ScenarioGenerator.Run? run)
    {
        Matrix = matrix;
        _run = run;
    }

    public ScenarioMatrix Matrix { get; }

    /// <summary>Falso quando a geração falhou (a matriz só tem a observação do erro).</summary>
    public bool CanMaterialize => _run is not null;

    /// <summary>Variáveis que podem receber valores reais: campos do payload e o estado consultado pelo cenário.</summary>
    public IReadOnlyList<ScenarioVariable> Variables(string scenarioId)
    {
        if (_run is null) return [];
        lock (_run) return _run.Variables(scenarioId);
    }

    /// <summary>
    /// Variáveis (payload e estado) da condição que dispara o cenário: as restrições que o <paramref name="baseline"/>
    /// confirmado (ou, sem ele, o caminho feliz da matriz) não satisfaz.
    /// Vazio para o caminho feliz.
    /// </summary>
    public IReadOnlyList<string> FocusVariables(string scenarioId, ScenarioMaterialization? baseline = null)
    {
        if (_run is null) return [];
        lock (_run) return _run.FocusVariables(scenarioId, baseline);
    }

    /// <summary>
    /// Payload do cenário com os valores informados (<paramref name="bindings"/>), mantendo do <paramref name="baseline"/>
    /// confirmado tudo que o cenário não obriga a mudar e aplicando os <paramref name="facts"/> já observados no estado real.
    /// Os campos que nem os bindings nem o baseline definem vêm do <paramref name="payload"/> base (dados reais adquiridos),
    /// quando não contradizem o cenário; o restante fica com os valores do gerador.
    /// </summary>
    public ScenarioMaterialization Materialize(string scenarioId, IReadOnlyList<ScenarioBinding>? bindings = null,
        ScenarioMaterialization? baseline = null, IReadOnlyCollection<StateFact>? facts = null, IReadOnlyList<ScenarioBinding>? payload = null)
    {
        if (_run is null) return ScenarioMaterialization.Fail(scenarioId, "A matriz não tem o modelo de restrições (a geração falhou).");
        lock (_run)
        {
            var result = _run.Materialize(scenarioId, bindings ?? [], baseline, facts ?? [], payload ?? []);
            if (result.Success || payload is not { Count: > 0 }) return result;
            // O payload base é só preferência: se ele (e não o cenário) impede a materialização, segue sem ele.
            var without = _run.Materialize(scenarioId, bindings ?? [], baseline, facts ?? [], []);
            if (!without.Success) return result;
            without.Warnings.Add($"Payload base ignorado neste cenário: {result.Error}");
            return without;
        }
    }

    /// <summary>
    /// Estado lido pelo fluxo de um cenário confirmado em runtime: os valores que a simulação usou passam a ser fatos
    /// conhecidos (válidos até a próxima escrita bem-sucedida na API).
    /// </summary>
    public IReadOnlyList<StateFact> InferFacts(ScenarioMaterialization materialization, string source)
    {
        if (_run is null || materialization.Assignment is null) return [];
        lock (_run) return ScenarioGenerator.Run.InferFacts(materialization, source);
    }
}

/// <summary>Variável de um cenário que pode receber um valor real.</summary>
public sealed class ScenarioVariable
{
    /// <summary>Nome para referenciar a variável: "request.VeiculoId", "veiculos.ObterPorId(request.VeiculoId).Disponivel".</summary>
    public string Name { get; init; } = "";

    /// <summary>payload, estado (banco, serviço consultado) ou contexto (usuário, configuração).</summary>
    public string Origin { get; init; } = "";

    public string Type { get; init; } = "";

    public bool Nullable { get; init; }

    /// <summary>route, query, header ou body (só payload).</summary>
    public string? Location { get; init; }

    /// <summary>Nome no JSON, na rota ou na query (só payload).</summary>
    public string? JsonName { get; init; }

    /// <summary>Valor no cenário da matriz estática, em JSON.</summary>
    public string Value { get; init; } = "";

    /// <summary>A variável aparece nas restrições do cenário (o valor importa para o resultado).</summary>
    public bool Constrained { get; init; }

    public List<string>? EnumValues { get; init; }
}

/// <summary>Valor real para uma variável do cenário. <see cref="Value"/> em JSON (37, "ABC1D23", true, null, {} = existe).</summary>
public sealed record ScenarioBinding(string Variable, string Value, string? Source = null);

/// <summary>Estado real observado: o resultado de uma consulta para valores concretos ("veiculos.ObterPorId(37)" + membro).</summary>
/// <param name="Member">Membro do resultado ("Disponivel"); vazio para o próprio resultado (existe ou não).</param>
public sealed record StateFact(string Root, string Member, string Value, string Source);

/// <summary>Resultado da materialização de um cenário.</summary>
public sealed class ScenarioMaterialization
{
    public string ScenarioId { get; init; } = "";

    public bool Success { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>Falhou porque o estado real conhecido contradiz o cenário: são necessários outros dados.</summary>
    public bool NeedsData { get; init; }

    public ScenarioRequest? Request { get; init; }

    /// <summary>Resultado esperado recalculado com os valores reais (mensagens interpoladas, efeitos).</summary>
    public ScenarioExpectation? Expected { get; init; }

    public List<ScenarioPrecondition> Preconditions { get; init; } = [];

    /// <summary>Valores escolhidos pela IA e fatos do estado real aplicados.</summary>
    public List<RuntimeBinding> Bindings { get; init; } = [];

    /// <summary>Campos que mudaram em relação ao payload confirmado (baseline): "placa: \"ABC1D23\" → \"\"".</summary>
    public List<string> Changes { get; init; } = [];

    /// <summary>Estado lido pelo fluxo com valor comprovado (dado real ou fato observado).</summary>
    public List<string> VerifiedState { get; init; } = [];

    /// <summary>Estado lido pelo fluxo cujo valor é só suposição do solver.</summary>
    public List<string> UnverifiedState { get; init; } = [];

    /// <summary>Estado que diferencia este cenário do caminho feliz e não tem valor comprovado.</summary>
    public List<string> UnverifiedFocus { get; init; } = [];

    /// <summary>Condições opacas (não traduzidas) assumidas verdadeiras/falsas.</summary>
    public List<string> Assumptions { get; init; } = [];

    /// <summary>Estado real informado nos bindings, reutilizável pelos outros cenários.</summary>
    public List<StateFact> Facts { get; init; } = [];

    public List<string> Warnings { get; init; } = [];

    public string Solver { get; init; } = "direto";

    internal Assignment? Assignment { get; init; }

    internal IReadOnlyList<(Var Var, Value Value)> PathState { get; init; } = [];

    public static ScenarioMaterialization Fail(string scenarioId, string error, bool needsData = false, List<string>? warnings = null) => new()
    {
        ScenarioId = scenarioId,
        Success = false,
        Error = error,
        NeedsData = needsData,
        Warnings = warnings ?? [],
    };
}

public partial class ScenarioGenerator
{
    internal sealed partial class Run
    {
        private static readonly JsonSerializerOptions JsonText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private sealed record Pin(Var Var, Value Value, Pred? Pred, string Origin, string? Source);

        internal IReadOnlyList<ScenarioVariable> Variables(string id)
        {
            if (!_entries.TryGetValue(id, out var entry)) return [];
            var constrained = ConstrainedVars(entry);
            var result = new List<ScenarioVariable>();

            foreach (var field in _input.All)
            {
                if (field.Parent is null && field.Location == InputLocations.Body && field.Kind is VarKind.Object or VarKind.Collection) continue;
                result.Add(Describe(_vars.Input(field), entry.Assignment, constrained.Contains(_vars.Input(field))));
            }
            foreach (var v in constrained.Where(v => v.Origin != VarOrigin.Input).OrderBy(v => v.Key, StringComparer.Ordinal))
                result.Add(Describe(v, entry.Assignment, true));
            return result;
        }

        internal IReadOnlyList<string> FocusVariables(string id, ScenarioMaterialization? baseline)
        {
            if (!_entries.TryGetValue(id, out var entry)) return [];
            var happy = _entries.Values.FirstOrDefault(e => e.Scenario.Kind == ScenarioKinds.Success && e.Scenario.Focus is null);
            if (happy?.Scenario.Id == id) return [];
            // A condição que dispara o cenário: as restrições que o payload de referência (baseline confirmado ou, sem
            // ele, o caminho feliz) não satisfaz. Sem referência, as que o caminho feliz não tem.
            IEnumerable<Pred> triggers = entry.Constraints.Select(Expand);
            if ((baseline?.Assignment ?? happy?.Assignment) is { } reference)
            {
                var a = Clone(reference);
                triggers = triggers.Where(c => Evaluator.Eval(c, a) != true);
            }
            else
            {
                var happyKeys = happy?.Constraints.Select(c => Expand(c).Key).ToHashSet() ?? [];
                triggers = triggers.Where(c => !happyKeys.Contains(c.Key));
            }
            // Sem o próprio corpo (request): toda condição sobre um campo passa por ele.
            var vars = triggers.SelectMany(Evaluator.VarsOf).SelectMany(Lineage)
                .Where(v => v.Field is not { Parent: null, Location: InputLocations.Body, Kind: VarKind.Object or VarKind.Collection })
                .Select(v => v.Display);
            // Tipo inválido no JSON: o campo trocado é a própria condição.
            if (entry.MismatchField is { } field) vars = vars.Append(_vars.Input(field).Display);
            return vars.Distinct().ToList();
        }

        private HashSet<Var> ConstrainedVars(Entry entry) =>
            entry.Constraints.Select(Expand).SelectMany(Evaluator.VarsOf).SelectMany(Lineage).ToHashSet();

        private static ScenarioVariable Describe(Var v, Assignment a, bool constrained) => new()
        {
            Name = v.Display,
            Origin = v.Origin switch { VarOrigin.Input => "payload", VarOrigin.Context => "contexto", _ => "estado" },
            Type = v.Kind switch
            {
                VarKind.Object => $"{v.TypeName} (objeto: {{}} = existe, null = não existe)",
                VarKind.Collection => $"{v.TypeName} (valor = quantidade de itens)",
                VarKind.Date => $"{v.Field?.TypeDisplay ?? v.TypeName} (data ISO 8601)",
                _ => v.Field?.TypeDisplay ?? v.TypeName,
            },
            Nullable = v.Nullable,
            Location = v.Field?.Location,
            JsonName = v.Field is { } f ? (f.Location == InputLocations.Body ? ValidationRule.JsonPath(f) : f.Name) : null,
            Value = ToJson(v, a.Get(v)),
            Constrained = constrained,
            EnumValues = v.EnumMembers?.Select(m => $"{m.Name} = {m.Value}").ToList(),
        };

        internal ScenarioMaterialization Materialize(string id, IReadOnlyList<ScenarioBinding> bindings, ScenarioMaterialization? baseline,
            IReadOnlyCollection<StateFact> facts, IReadOnlyList<ScenarioBinding> payload)
        {
            if (!_entries.TryGetValue(id, out var entry)) return ScenarioMaterialization.Fail(id, $"O cenário {id} não existe na matriz.");

            var warnings = new List<string>();
            var pins = new List<Pin>();
            foreach (var binding in bindings)
            {
                if (FindVar(binding.Variable) is not { } v)
                {
                    warnings.Add($"Variável desconhecida ignorada: {binding.Variable}");
                    continue;
                }
                if (pins.Any(p => p.Var == v))
                {
                    warnings.Add($"Mais de um valor para {v.Display}: mantido o primeiro.");
                    continue;
                }
                if (!TryValue(v, binding.Value, out var value, out var error))
                    return ScenarioMaterialization.Fail(id, $"Valor inválido para {v.Display}: {error}", warnings: warnings);
                pins.Add(new Pin(v, value, PinPred(v, value), "ia", binding.Source));
            }

            if (entry.MismatchField is { } mismatchField) return MaterializeMismatch(entry, mismatchField, baseline, warnings);

            var constraints = entry.Constraints.Select(Expand).ToList();
            var required = new List<Pred>(constraints);
            required.AddRange(Preds(pins));
            if (Solve(required) is null)
            {
                var conflicts = pins.Where(p => p.Pred is not null && Solve([.. constraints, p.Pred!]) is null)
                    .Select(p => $"{p.Var.Display} = {ToJson(p.Var, p.Value)}").ToList();
                return ScenarioMaterialization.Fail(id, conflicts.Count > 0
                    ? $"Os valores contradizem as restrições do cenário: {string.Join(", ", conflicts)}."
                    : "A combinação dos valores informados contradiz as restrições do cenário.", warnings: warnings);
            }

            // Mudança mínima: do payload confirmado fica tudo que o cenário não obriga a mudar.
            if (baseline?.Assignment is { } reference)
                Prefer(reference.Values
                    .Where(kv => kv.Key.Origin == VarOrigin.Input && kv.Key.Field is not null && pins.All(p => p.Var != kv.Key))
                    .Select(kv => new Pin(kv.Key, kv.Value, PinPred(kv.Key, kv.Value), "baseline", baseline.ScenarioId))
                    .OrderBy(p => p.Var.Key, StringComparer.Ordinal)
                    .ToList(), required, pins);

            // Payload base (dados reais adquiridos) nos campos que ainda não têm valor escolhido.
            var preferred = new List<Pin>();
            foreach (var binding in payload)
            {
                if (FindVar(binding.Variable) is not { Origin: VarOrigin.Input, Field: not null } v) continue;
                if (pins.Any(p => p.Var == v) || preferred.Any(p => p.Var == v)) continue;
                if (!TryValue(v, binding.Value, out var value, out var error))
                {
                    warnings.Add($"Payload base: valor inválido para {v.Display} ({error}).");
                    continue;
                }
                preferred.Add(new Pin(v, value, PinPred(v, value), "payload", binding.Source));
            }
            Prefer(preferred, required, pins);

            var assignment = Solve(required)!;

            // Fatos do estado real (consultas já observadas para os mesmos valores do payload).
            var stateVars = ConstrainedVars(entry).Where(v => v.Origin != VarOrigin.Input && v.Root is not null).ToList();
            for (var round = 0; round < 4 && facts.Count > 0; round++)
            {
                var added = new List<Pin>();
                foreach (var v in stateVars)
                {
                    if (pins.Any(p => p.Var == v)) continue;
                    var root = v.Root!.Render(assignment);
                    var fact = facts.LastOrDefault(f => f.Root == root && f.Member == (v.Member ?? ""));
                    if (fact is null || !TryValue(v, fact.Value, out var value, out _)) continue;
                    added.Add(new Pin(v, value, PinPred(v, value), "fato", fact.Source));
                }
                if (added.Count == 0) break;

                if (Solve([.. required, .. Preds(added)]) is not { } next)
                {
                    var details = added.Select(p => $"{StateName(p.Var, assignment)} = {ToJson(p.Var, p.Value)} ({p.Source})");
                    return ScenarioMaterialization.Fail(id, $"O estado real contradiz o cenário: {string.Join("; ", details)}. São necessários outros dados.",
                        needsData: true, warnings: warnings);
                }
                required.AddRange(Preds(added));
                pins.AddRange(added);
                assignment = next;
            }

            // Valores exatos (o solver pode ter escolhido outro texto do mesmo tamanho, por exemplo).
            var a = Clone(assignment);
            foreach (var pin in pins) a.Values[pin.Var] = pin.Value;
            if (constraints.FirstOrDefault(c => Evaluator.Eval(c, a) == false) is { } violated)
                return ScenarioMaterialization.Fail(id, $"Com os valores reais a condição \"{violated.Text}\" do cenário deixa de valer.", warnings: warnings);

            var outcome = Simulate(a);
            if (!SameOutcome(outcome, a, entry.Outcome, entry.Assignment))
                return ScenarioMaterialization.Fail(id, $"Com esses valores a simulação do fluxo muda o resultado: esperado {Describe(entry.Outcome)}, obtido {Describe(outcome)}.", warnings: warnings);

            return Build(entry, a, outcome, pins, baseline, warnings);
        }

        private ScenarioMaterialization Build(Entry entry, Assignment a, Outcome outcome, List<Pin> pins, ScenarioMaterialization? baseline, List<string> warnings)
        {
            var notes = new List<string>(outcome.Notes);
            var path = ReadByPath(outcome, a);
            var effects = Effects(outcome, a, out var persisted);
            // Um membro com valor real comprova também que o objeto que o contém existe.
            var proven = pins.Where(p => p.Origin is "ia" or "fato")
                .SelectMany(p => p.Value.IsNull ? [p.Var] : Lineage(p.Var)).ToHashSet();

            var pathState = path.Select(Expand).SelectMany(Evaluator.VarsOf).SelectMany(Lineage)
                .Where(v => v.Origin != VarOrigin.Input).Distinct().OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
            var verified = pathState.Where(proven.Contains).Select(v => $"{StateName(v, a)} = {ToJson(v, a.Get(v))}").ToList();
            var unverified = pathState.Where(v => !proven.Contains(v)).Select(v => $"{StateName(v, a)} = {ToJson(v, a.Get(v))}").ToList();

            // O que diferencia o cenário do caminho feliz: restrições que o caminho feliz não tem.
            var happy = _entries.Values.FirstOrDefault(e => e.Scenario.Kind == ScenarioKinds.Success && e.Scenario.Focus is null);
            var happyKeys = happy?.Constraints.Select(c => Expand(c).Key).ToHashSet() ?? [];
            var focus = entry.Constraints.Select(Expand).Where(c => !happyKeys.Contains(c.Key)).SelectMany(Evaluator.VarsOf).SelectMany(Lineage)
                .Where(v => v.Origin != VarOrigin.Input && !proven.Contains(v)).Distinct()
                .Select(v => $"{StateName(v, a)} = {ToJson(v, a.Get(v))}").ToList();

            var changes = new List<string>();
            if (baseline?.Assignment is { } reference)
                foreach (var field in _input.All)
                {
                    var v = _vars.Input(field);
                    if (!a.Values.TryGetValue(v, out var now)) continue;
                    var before = reference.Values.TryGetValue(v, out var b) ? ToJson(v, b) : null;
                    var after = ToJson(v, now);
                    if (before is not null && before != after) changes.Add($"{v.Display}: {Short(before)} → {Short(after)}");
                }

            return new ScenarioMaterialization
            {
                ScenarioId = entry.Scenario.Id,
                Success = true,
                Request = _payload.Build(a),
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
                Preconditions = entry.HidePreconditions || outcome.ValidationFailed ? [] : Preconditions(path, a, notes),
                Bindings = pins.Where(p => p.Origin is "ia" or "fato").Select(p => new RuntimeBinding
                {
                    Variable = p.Var.Origin == VarOrigin.Input ? p.Var.Display : StateName(p.Var, a),
                    Value = ToJson(p.Var, p.Value),
                    Origin = p.Origin,
                    Source = p.Source,
                }).ToList(),
                Changes = changes,
                VerifiedState = verified,
                UnverifiedState = unverified,
                UnverifiedFocus = focus,
                Assumptions = a.Assumptions.Select(x => $"{x.Key} = {(x.Value ? "verdadeiro" : "falso")}").ToList(),
                Facts = pins.Where(p => p.Origin == "ia" && p.Var.Origin != VarOrigin.Input && p.Var.Root is not null)
                    .Select(p => new StateFact(p.Var.Root!.Render(a), p.Var.Member ?? "", ToJson(p.Var, p.Value), p.Source ?? "ia")).ToList(),
                Warnings = warnings,
                Solver = a.UsedZ3 ? "z3" : "direto",
                Assignment = a,
                PathState = pathState.Select(v => (v, a.Get(v))).ToList(),
            };
        }

        /// <summary>Tipo inválido no JSON: o payload confirmado com o campo trocado por texto.</summary>
        private static ScenarioMaterialization MaterializeMismatch(Entry entry, InputField field, ScenarioMaterialization? baseline, List<string> warnings)
        {
            if (baseline?.Request?.Body is not JsonObject body)
                return ScenarioMaterialization.Fail(entry.Scenario.Id, "Precisa do payload confirmado (baseline) para trocar o tipo do campo.", warnings: warnings);

            var mutated = (JsonObject)body.DeepClone();
            var before = mutated[field.Name]?.ToJsonString() ?? "null";
            mutated[field.Name] = "abc";
            var request = baseline.Request;
            return new ScenarioMaterialization
            {
                ScenarioId = entry.Scenario.Id,
                Success = true,
                Request = new ScenarioRequest { Method = request.Method, Url = request.Url, Route = request.Route, Query = request.Query, Headers = request.Headers, Body = mutated },
                Expected = entry.Scenario.Expected,
                Changes = [$"{field.Path}: {Short(before)} → \"abc\""],
                Warnings = warnings,
                Assignment = baseline.Assignment,
            };
        }

        /// <summary>Valores preferidos (não obrigatórios): todos juntos se possível, senão um a um, sem contradizer o cenário.</summary>
        private void Prefer(List<Pin> candidates, List<Pred> required, List<Pin> pins)
        {
            if (candidates.Count == 0) return;
            if (Solve([.. required, .. Preds(candidates)]) is not null)
            {
                required.AddRange(Preds(candidates));
                pins.AddRange(candidates);
                return;
            }
            foreach (var pin in candidates)
            {
                if (pin.Pred is not null && Solve([.. required, pin.Pred]) is null) continue;
                if (pin.Pred is not null) required.Add(pin.Pred);
                pins.Add(pin);
            }
        }

        internal static IReadOnlyList<StateFact> InferFacts(ScenarioMaterialization m, string source) => m.PathState
            .Where(s => s.Var.Root is not null)
            .Select(s => new StateFact(s.Var.Root!.Render(m.Assignment), s.Var.Member ?? "", ToJson(s.Var, s.Value), source))
            .ToList();

        private bool SameOutcome(Outcome o, Assignment a, Outcome expected, Assignment ea)
        {
            if (o.Success != expected.Success || o.Status != expected.Status || o.Rule != expected.Rule) return false;
            if (!o.Success) return true;
            return o.Exits.Count == expected.Exits.Count && EffectSignature(o, a) == EffectSignature(expected, ea);
        }

        private string EffectSignature(Outcome o, Assignment a) =>
            string.Join(";", Effects(o, a, out _).Select(e => $"{e.Description}|{string.Join(",", e.Properties ?? [])}").Order(StringComparer.Ordinal));

        private static string Describe(Outcome o) => o.Success
            ? $"sucesso (HTTP {o.Status?.ToString(CultureInfo.InvariantCulture) ?? "?"})"
            : $"HTTP {o.Status?.ToString(CultureInfo.InvariantCulture) ?? "?"} pela regra \"{o.Rule}\"";

        private static string StateName(Var v, Assignment a) =>
            v.Root is null ? v.Display : v.Root.Render(a) + (v.Member is null ? "" : "." + v.Member);

        private static string Short(string json) => json.Length > 40 ? json[..37] + "…" : json;

        private static IEnumerable<Pred> Preds(IEnumerable<Pin> pins) => pins.Where(p => p.Pred is not null).Select(p => p.Pred!);

        private static Assignment Clone(Assignment source)
        {
            var copy = new Assignment { UsedZ3 = source.UsedZ3 };
            foreach (var (v, value) in source.Values) copy.Values[v] = value;
            foreach (var (k, value) in source.Assumptions) copy.Assumptions[k] = value;
            return copy;
        }

        /// <summary>Pelo nome do código ("request.VeiculoId"), nome no JSON ("veiculoId"), chave interna ou consulta.</summary>
        private Var? FindVar(string name)
        {
            var trimmed = name.Trim();
            // Campos do payload ainda não referenciados pelas restrições também valem.
            var all = _input.All.Select(_vars.Input).Concat(_vars.All).Distinct().ToList();
            return all.FirstOrDefault(v => v.Key == trimmed)
                ?? all.FirstOrDefault(v => string.Equals(v.Display, trimmed, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => v.Field is { } f && string.Equals(f.Location == InputLocations.Body ? ValidationRule.JsonPath(f) : f.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(v => v.Origin == VarOrigin.Input && v.Display.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase));
        }

        // ---- Valores JSON ↔ variáveis do solver ----

        internal static string ToJson(Var v, Value value)
        {
            if (value.IsNull) return "null";
            return v.Kind switch
            {
                VarKind.Bool => value.Bool == false ? "false" : "true",
                VarKind.Int => ((long)(value.Number ?? 0)).ToString(CultureInfo.InvariantCulture),
                VarKind.Decimal => (value.Number ?? 0).ToString(CultureInfo.InvariantCulture),
                VarKind.Enum => v.EnumMembers?.FirstOrDefault(m => m.Value == value.Number) is { } member
                    ? JsonSerializer.Serialize(member.Name, JsonText)
                    : ((long)(value.Number ?? 0)).ToString(CultureInfo.InvariantCulture),
                VarKind.Date => JsonSerializer.Serialize(DateText(v, value.Number ?? 0), JsonText),
                VarKind.Object => "{}",
                VarKind.Collection => (value.Length ?? 0).ToString(CultureInfo.InvariantCulture),
                _ => JsonSerializer.Serialize(value.Text ?? "", JsonText),
            };
        }

        private static string DateText(Var v, decimal days)
        {
            var date = Values.Date(days);
            return v.TypeName == "DateOnly" ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        internal static bool TryValue(Var v, string json, out Value value, out string? error)
        {
            value = Value.Null;
            error = null;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                // Texto sem aspas: ABC1D23.
                node = JsonValue.Create(json);
            }

            if (node is null)
            {
                if (!v.Nullable)
                {
                    error = "o campo não aceita null";
                    return false;
                }
                return true;
            }

            var scalar = node as JsonValue;
            string? text = scalar?.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : scalar?.ToJsonString();

            switch (v.Kind)
            {
                case VarKind.Object:
                    value = new Value();
                    return true;
                case VarKind.Collection:
                    if (node is JsonArray array) value = new Value { Length = array.Count };
                    else if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0) value = new Value { Length = count };
                    else return Invalid("informe uma lista ou a quantidade de itens", out error);
                    return true;
                case VarKind.Bool:
                    if (!bool.TryParse(text, out var flag)) return Invalid("informe true ou false", out error);
                    value = new Value { Bool = flag };
                    return true;
                case VarKind.Int or VarKind.Decimal:
                    if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return Invalid("informe um número", out error);
                    if (v.Kind == VarKind.Int && number != Math.Truncate(number)) return Invalid("informe um número inteiro", out error);
                    value = new Value { Number = number };
                    return true;
                case VarKind.Enum:
                {
                    if (decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                    {
                        value = new Value { Number = raw };
                        return true;
                    }
                    var name = text?.Split('.').Last();
                    if (v.EnumMembers?.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } member)
                        return Invalid($"valor fora do enum {v.TypeName}", out error);
                    value = new Value { Number = member.Value };
                    return true;
                }
                case VarKind.Date:
                {
                    if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date)) return Invalid("informe uma data ISO 8601", out error);
                    value = new Value { Number = date.Year <= 1 ? Values.MinDateDays : (decimal)(date.Date - Values.Today.Date).TotalDays };
                    return true;
                }
                case VarKind.String:
                    value = Values.Text(text ?? "", v);
                    return true;
                default:
                    value = new Value { Text = text ?? "" };
                    return true;
            }
        }

        private static bool Invalid(string message, out string? error)
        {
            error = message;
            return false;
        }

        /// <summary>Restrição "variável = valor" para o solver (nulo quando o tipo não é modelado: só substitui o valor).</summary>
        private static Pred? PinPred(Var v, Value value)
        {
            var code = $"{v.Display} == {ToJson(v, value)}";
            if (value.IsNull) return new NullPred(v, $"{v.Display} == null");
            return v.Kind switch
            {
                VarKind.Object => Pred.Not(new NullPred(v, $"{v.Display} == null")),
                VarKind.Collection => Pred.And(Pred.Not(new NullPred(v, $"{v.Display} == null")),
                    new CmpPred(new VarTerm(v, Measure.Length), "==", ConstTerm.Of(value.Length ?? 0), $"{v.Display}.Count == {value.Length ?? 0}")),
                VarKind.Bool => value.Bool == false ? Pred.Not(new BoolPred(v, v.Display)) : new BoolPred(v, v.Display),
                VarKind.Int or VarKind.Decimal or VarKind.Enum or VarKind.Date => new CmpPred(new VarTerm(v), "==", ConstTerm.Of(value.Number ?? 0), code),
                VarKind.String or VarKind.Guid => new CmpPred(new VarTerm(v), "==", ConstTerm.Of(value.Text ?? ""), code),
                _ => null,
            };
        }
    }
}
