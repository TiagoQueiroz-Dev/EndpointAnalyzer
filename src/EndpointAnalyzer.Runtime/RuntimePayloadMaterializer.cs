using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

/// <param name="Materializable">A IA conseguiu dados reais para o cenário.</param>
internal sealed record BindingPlan(List<ScenarioBinding> Bindings, bool Materializable, string? Reason);

/// <summary>
/// RuntimePayloadMaterializer (Fase 5): a IA liga as variáveis de cada cenário (ids, estado lido pelo fluxo) aos dados
/// reais do ScenarioContext; o <see cref="ScenarioModel"/> confere os valores com o solver (e o Z3), simula o fluxo de
/// novo e monta o payload executável (rota, query, headers, body). Valores que contradizem o cenário são recusados.
/// </summary>
internal sealed class RuntimePayloadMaterializer(ScenarioModel model, RuntimeAi ai)
{
    private static readonly Dictionary<string, JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["scenarios"] = Schema.Arr(Schema.Obj(new()
        {
            ["id"] = Schema.Str(),
            ["materializable"] = Schema.Bool(),
            ["reason"] = Schema.Str(),
            ["bindings"] = Schema.Arr(Schema.Binding()),
        })),
    });

    public ScenarioModel Model => model;

    public async Task<Dictionary<string, BindingPlan>> PlanAsync(IReadOnlyList<Scenario> scenarios, string? baselineId, List<DataRequirement> requirements,
        ScenarioContext context, CancellationToken cancellationToken)
    {
        // Sem dado real no contexto não há o que ligar: o gerador materializa com os próprios valores.
        if (context.Items.Count == 0) return [];

        var prompt = new StringBuilder();
        prompt.AppendLine($"Cenário baseline (caminho feliz, executado primeiro): {baselineId ?? "nenhum"}");
        prompt.AppendLine();
        prompt.Append(RuntimeAi.Section("context", context.Describe()));
        prompt.Append(RuntimeAi.Section("requirements", RuntimeAi.Json(requirements.Select(r => new { r.Id, r.Description, r.Scenarios, r.Fields, r.Status, r.Reason }))));
        prompt.Append(RuntimeAi.Section("scenarios", Scenarios(scenarios, model)));
        prompt.AppendLine("""
            Tarefa: para cada cenário, ligue as variáveis que dependem de dados reais aos itens do contexto.
            - bindings: { variable: o nome exatamente como em variables, valueJson: o valor real em JSON, source: a key do
              item do contexto (ou a execução EX-..) de onde veio }.
            - Ligue os ids/chaves do payload (ex.: request.VeiculoId = 37) e, quando o dado real mostrar, o estado que o
              fluxo lê (ex.: veiculos.ObterPorId(request.VeiculoId).Disponivel = true; o objeto: {} = existe, null = não existe).
            - Não ligue campos que só dependem de formato ou limite (texto, datas, tamanho): o gerador já resolve e, a
              partir do baseline confirmado, muda só o necessário para cada cenário.
            - Cada cenário precisa do estado descrito nas pré-condições (ex.: "Disponivel = false" exige um registro real
              com disponivel = false). Não reutilize um registro cujo estado contradiz o cenário.
            - materializable = false (com reason) quando o contexto não tem dado real que atenda o cenário.
            - Cenários sem dependência de dados: materializable = true e bindings vazio.
            """);

        var response = await ai.AskAsync("ligar dados reais aos cenários", prompt.ToString(), ResponseSchema, cancellationToken);
        var plans = new Dictionary<string, BindingPlan>();
        foreach (var s in Read.Arr(response, "scenarios"))
        {
            var id = Read.Str(s, "id");
            if (scenarios.All(x => x.Id != id) || plans.ContainsKey(id)) continue;
            plans[id] = new BindingPlan(Bindings(s), Read.Bool(s, "materializable"), Read.NonEmpty(Read.Str(s, "reason")));
        }
        return plans;
    }

    public ScenarioMaterialization Materialize(ScenarioState state, ScenarioMaterialization? baseline, ScenarioContext context) =>
        model.Materialize(state.Scenario.Id, state.Bindings, baseline, context.Facts);

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
