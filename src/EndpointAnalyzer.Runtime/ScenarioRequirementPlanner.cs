using System.Text;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// ScenarioRequirementPlanner (Fase 3): a IA converte as condições e suposições dos cenários em requisitos de dados
/// ("Veículo deve existir com Disponivel = true", "id de veículo que não existe").
/// </summary>
internal sealed class ScenarioRequirementPlanner(RuntimeAi ai)
{
    private static readonly Dictionary<string, System.Text.Json.JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["requirements"] = Schema.Arr(Schema.Obj(new()
        {
            ["id"] = Schema.Str(),
            ["description"] = Schema.Str(),
            ["entity"] = Schema.Str(),
            ["constraints"] = Schema.Arr(Schema.Str()),
            ["scenarios"] = Schema.Arr(Schema.Str()),
            ["fields"] = Schema.Arr(Schema.Str()),
        })),
        ["notes"] = Schema.Arr(Schema.Str()),
    });

    public async Task<List<DataRequirement>> PlanAsync(EndpointAnalysisContext context, ScenarioMatrix matrix, IReadOnlyList<Scenario> scenarios,
        EndpointCatalog catalog, List<string> notes, CancellationToken cancellationToken)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Endpoint analisado: {context.Endpoint.HttpMethod} {context.Endpoint.Route} ({context.Endpoint.Controller}.{context.Endpoint.Action})");
        prompt.AppendLine();
        prompt.Append(RuntimeAi.Section("scenarios", RuntimeAi.Json(scenarios.Select(s => new
        {
            id = s.Id,
            kind = s.Kind,
            title = s.Title,
            focus = s.Focus?.Expression,
            preconditions = s.Preconditions.Select(p => $"[{p.Kind}] {p.Description}"),
            payload = new { url = s.Request.Url, body = s.Request.Body?.ToJsonString() },
            expected = new { status = s.Expected.HttpStatus, s.Expected.Outcome, s.Expected.Messages, s.Expected.Rule },
        }))));
        prompt.Append(RuntimeAi.Section("payload_fields", RuntimeAi.Json(matrix.Inputs.Select(i => new { i.Path, i.Name, i.Location, i.Type, i.Constraints }))));
        prompt.Append(RuntimeAi.Section("catalog", catalog.Describe()));
        prompt.Append(RuntimeAi.Section("code", RuntimeAi.Code(context)));
        prompt.AppendLine("""
            Tarefa: liste os requisitos de dados reais para materializar os cenários na API em execução.
            - Um requisito é um dado do estado (banco, serviço, usuário/configuração) que precisa existir ou não existir,
              com as propriedades exigidas pelas condições e suposições. Ex.: "Veículo existente com Disponivel = true",
              "Id de veículo que não existe", "Placa já cadastrada".
            - Ids e chaves referenciados no payload (veiculoId, clienteId...) quase sempre dependem de um requisito.
            - Campos que só precisam de um valor de formato (texto, data, número dentro do limite) não são requisitos:
              o gerador já resolve.
            - scenarios: os ids dos cenários que dependem do requisito; fields: os campos do payload preenchidos com o
              dado (como em payload_fields.path); entity: a entidade (ou "" se não houver); constraints: as condições
              do dado, como no código quando possível.
            - Numere como R1, R2... Agrupe cenários que podem usar o mesmo dado. Cenários sem dependência de estado não
              precisam de requisito.
            - notes: suposições que não podem ser atendidas pela API (ex.: depende de serviço externo).
            """);

        var response = await ai.AskAsync("planejar requisitos de dados", prompt.ToString(), ResponseSchema, cancellationToken);
        if (response is null) return [];

        var known = scenarios.Select(s => s.Id).ToHashSet();
        var requirements = new List<DataRequirement>();
        foreach (var r in Read.Arr(response, "requirements"))
        {
            var id = Read.NonEmpty(Read.Str(r, "id")) ?? $"R{requirements.Count + 1}";
            if (requirements.Any(x => x.Id == id)) id = $"R{requirements.Count + 1}";
            requirements.Add(new DataRequirement
            {
                Id = id,
                Description = Read.Str(r, "description"),
                Entity = Read.NonEmpty(Read.Str(r, "entity")),
                Constraints = Read.Strs(r, "constraints"),
                Scenarios = Read.Strs(r, "scenarios").Where(known.Contains).ToList(),
                Fields = Read.Strs(r, "fields"),
            });
        }
        notes.AddRange(Read.Strs(response.Value, "notes").Select(n => $"Requisitos: {n}"));
        return requirements;
    }
}
