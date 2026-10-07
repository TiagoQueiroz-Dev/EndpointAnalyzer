using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

/// <param name="Materializable">A IA conseguiu dados reais para o cenário.</param>
internal sealed record BindingPlan(List<ScenarioBinding> Bindings, bool Materializable, string? Reason);

/// <param name="Payload">Payload base: todos os campos do payload, cada um com a origem do valor.</param>
/// <param name="Scenarios">O que cada cenário liga a dados reais além do payload base.</param>
internal sealed record MaterializationPlan(List<PayloadFieldValue> Payload, Dictionary<string, BindingPlan> Scenarios);

/// <summary>
/// RuntimePayloadMaterializer (Fase 5): a IA monta o payload base completo com os dados reais do ScenarioContext (todos
/// os campos, não só os que as condições usam) e liga as variáveis de cada cenário (ids, estado lido pelo fluxo) aos dados
/// reais de que ele depende. O <see cref="ScenarioModel"/> confere os valores com o solver (e o Z3), simula o fluxo de
/// novo e monta o payload executável (rota, query, headers, body): cada cenário parte do payload base e muda só o
/// necessário. Valores que contradizem o cenário são recusados.
/// </summary>
internal sealed class RuntimePayloadMaterializer(ScenarioModel model, RuntimeAi ai)
{
    private static readonly string[] Origins = [PayloadValueOrigins.Real, PayloadValueOrigins.Derived, PayloadValueOrigins.Synthetic];

    private static readonly Dictionary<string, JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["payload"] = Schema.Arr(Schema.Obj(new()
        {
            ["variable"] = Schema.Str(),
            ["valueJson"] = Schema.Str(),
            ["origin"] = Schema.Enum(Origins),
            ["source"] = Schema.Str(),
            ["reason"] = Schema.Str(),
        })),
        ["scenarios"] = Schema.Arr(Schema.Obj(new()
        {
            ["id"] = Schema.Str(),
            ["materializable"] = Schema.Bool(),
            ["reason"] = Schema.Str(),
            ["bindings"] = Schema.Arr(Schema.Binding()),
        })),
    });

    public ScenarioModel Model => model;

    public async Task<MaterializationPlan> PlanAsync(IReadOnlyList<Scenario> scenarios, string? baselineId, List<DataRequirement> requirements,
        ScenarioContext context, CancellationToken cancellationToken)
    {
        var fields = PayloadFields(baselineId ?? scenarios.FirstOrDefault()?.Id);
        var prompt = new StringBuilder();
        prompt.AppendLine($"Cenário baseline (caminho feliz, executado primeiro): {baselineId ?? "nenhum"}");
        prompt.AppendLine();
        prompt.Append(RuntimeAi.Section("context", context.Describe()));
        prompt.Append(RuntimeAi.Section("requirements", RuntimeAi.Json(requirements.Select(r => new { r.Id, r.Kind, r.Description, r.Scenarios, r.Fields, r.Status, r.Reason }))));
        prompt.Append(RuntimeAi.Section("payload_fields", string.Join("\n", fields.Select(v =>
            $"- {v.Name} ({v.Location}, {v.Type}{(v.Nullable ? ", aceita null" : "")}) gerador = {ScenarioContext.Truncate(v.Value, 80)}"
            + (v.EnumValues is { Count: > 0 } e ? $" [{string.Join(", ", e)}]" : "")))));
        prompt.Append(RuntimeAi.Section("scenarios", Scenarios(scenarios, model)));
        prompt.AppendLine("""
            Tarefa 1 — payload: o payload base, completo e coerente, do caminho feliz. Um valor para cada campo de
            payload_fields (variable = o nome exatamente como lá), na ordem de prioridade:
            1. origin "real": o valor aparece num dado real do contexto (source = a key do item ou a execução EX-..).
               Prefira campos do mesmo registro para os dados que andam juntos (nome, e-mail e telefone da mesma pessoa).
            2. origin "derivado": derivado de um dado real (source = de onde veio; reason = como). Ex.: campo único
               (código, CNPJ, e-mail, placa) de um registro real com a alteração mínima para não duplicar.
            3. origin "sintetico": não há dado real; um valor semanticamente válido para o campo (CPF/CNPJ com dígitos
               verificadores válidos, e-mail com domínio plausível, nome de pessoa plausível, telefone com DDD). Nunca
               "texto", "string", "teste" ou "usuario@exemplo.com". source = "".
            Respeite tipo, tamanho, formato e enum do campo. Omita só os campos que devem ficar com o valor do gerador
            (ex.: opcional que precisa ficar null no caminho feliz). Esse payload é reutilizado por todos os cenários.

            Tarefa 2 — scenarios: para cada cenário, ligue as variáveis que dependem de dados reais aos itens do contexto.
            - bindings: { variable: o nome exatamente como em variables, valueJson: o valor real em JSON, source: a key do
              item do contexto (ou a execução EX-..) de onde veio }.
            - O cenário parte do payload base: ligue só o que ele precisa diferente e o estado que comprova o cenário.
              Ex.: o id de um registro com o estado exigido (request.VeiculoId = 37) e, quando o dado real mostrar, o
              estado que o fluxo lê (veiculos.ObterPorId(request.VeiculoId).Disponivel = true; o objeto: {} = existe,
              null = não existe).
            - Não ligue campos que só dependem de formato ou limite (texto vazio, tamanho máximo, data no passado): o
              gerador já resolve e muda só o necessário.
            - Cada cenário precisa do estado descrito nas pré-condições (ex.: "Disponivel = false" exige um registro real
              com disponivel = false). Não reutilize um registro cujo estado contradiz o cenário.
            - materializable = false (com reason) quando o contexto não tem dado real que atenda o cenário.
            - Cenários sem dependência de dados: materializable = true e bindings vazio.
            """);

        var response = await ai.AskAsync("montar o payload base e ligar dados reais aos cenários", prompt.ToString(), ResponseSchema, cancellationToken);
        var plans = new Dictionary<string, BindingPlan>();
        foreach (var s in Read.Arr(response, "scenarios"))
        {
            var id = Read.Str(s, "id");
            if (scenarios.All(x => x.Id != id) || plans.ContainsKey(id)) continue;
            plans[id] = new BindingPlan(Bindings(s), Read.Bool(s, "materializable"), Read.NonEmpty(Read.Str(s, "reason")));
        }
        return new MaterializationPlan(Payload(fields, Read.Arr(response, "payload"), context), plans);
    }

    public ScenarioMaterialization Materialize(ScenarioState state, ScenarioMaterialization? baseline, ScenarioContext context) =>
        model.Materialize(state.Scenario.Id, state.Bindings, baseline, context.Facts, context.Payload);

    /// <summary>Campos do payload, com os valores do gerador no cenário de referência.</summary>
    private List<ScenarioVariable> PayloadFields(string? scenarioId) =>
        scenarioId is null ? [] : model.Variables(scenarioId).Where(v => v.Origin == "payload").ToList();

    /// <summary>
    /// Um valor por campo: o da IA (o real só fica verificado se aparece no dado citado) ou, sem ele, o do gerador
    /// (último recurso).
    /// </summary>
    internal static List<PayloadFieldValue> Payload(IReadOnlyList<ScenarioVariable> fields, IEnumerable<JsonElement> values, ScenarioContext context)
    {
        var chosen = new Dictionary<string, PayloadFieldValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in values)
        {
            var name = Read.Str(p, "variable").Trim();
            var value = Read.NonEmpty(Read.Str(p, "valueJson"));
            var field = fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? fields.FirstOrDefault(f => string.Equals(f.JsonName, name, StringComparison.OrdinalIgnoreCase));
            if (field is null || value is null || chosen.ContainsKey(field.Name)) continue;

            var origin = Read.Str(p, "origin") is var o && Origins.Contains(o) ? o : PayloadValueOrigins.Synthetic;
            var source = Read.NonEmpty(Read.Str(p, "source"));
            var verified = origin == PayloadValueOrigins.Real && context.Supports(source, value);
            chosen[field.Name] = new PayloadFieldValue
            {
                Field = field.Name,
                Value = value,
                Origin = origin,
                Source = source,
                Verified = verified,
                Reason = origin == PayloadValueOrigins.Real && !verified
                    ? $"valor não encontrado em {source ?? "nenhuma fonte citada"}"
                    : Read.NonEmpty(Read.Str(p, "reason")),
            };
        }

        return fields.Select(f => chosen.GetValueOrDefault(f.Name) ?? new PayloadFieldValue
        {
            Field = f.Name,
            Value = f.Value,
            Origin = PayloadValueOrigins.Generator,
        }).ToList();
    }

    internal static List<ScenarioBinding> Bindings(JsonElement e) => Read.Arr(e, "bindings")
        .Select(b => new ScenarioBinding(Read.Str(b, "variable"), Read.Str(b, "valueJson"), Read.NonEmpty(Read.Str(b, "source"))))
        .Where(b => b.Variable.Length > 0 && b.Value.Length > 0)
        .ToList();

    /// <summary>Cenários com as variáveis que podem receber valores reais (* = a variável restringe o cenário).</summary>
    internal static string Scenarios(IEnumerable<Scenario> scenarios, ScenarioModel model)
    {
        var sb = new StringBuilder();
        foreach (var s in scenarios)
        {
            sb.AppendLine($"## {s.Id} [{s.Kind}] {s.Title}");
            sb.AppendLine($"esperado: HTTP {s.Expected.HttpStatus?.ToString() ?? "?"} {s.Expected.Outcome}{(s.Expected.Messages.Count > 0 ? $" \"{s.Expected.Messages[0]}\"" : "")}");
            foreach (var p in s.Preconditions) sb.AppendLine($"pré-condição [{p.Kind}]: {p.Description}");
            sb.AppendLine("variables:");
            foreach (var v in model.Variables(s.Id))
                sb.AppendLine($"- {v.Name}{(v.Constrained ? " *" : "")} ({v.Origin}, {v.Type}{(v.Nullable ? ", aceita null" : "")}) = {ScenarioContext.Truncate(v.Value, 80)}"
                              + (v.EnumValues is { Count: > 0 } e ? $" [{string.Join(", ", e)}]" : ""));
        }
        return sb.ToString();
    }
}
