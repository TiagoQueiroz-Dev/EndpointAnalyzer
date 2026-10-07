using System.Text;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// ScenarioRequirementPlanner (Fase 3): a IA lista dois tipos de requisito de dados.
/// <list type="bullet">
/// <item>Payload (P1, P2...): de onde vêm os dados reais para preencher o payload completo ("Pessoa existente: nome,
/// email, telefone, cnpj", "Hierarquia existente: hierarquiaSuperiorId"). Todos os cenários partem desse payload.</item>
/// <item>Cenário (R1, R2...): o estado específico de que um cenário depende ("Veículo existente com Disponivel = true",
/// "id de veículo que não existe").</item>
/// </list>
/// </summary>
internal sealed class ScenarioRequirementPlanner(RuntimeAi ai)
{
    private static object Requirement(bool scenarios)
    {
        var properties = new Dictionary<string, object>
        {
            ["id"] = Schema.Str(),
            ["description"] = Schema.Str(),
            ["entity"] = Schema.Str(),
            ["constraints"] = Schema.Arr(Schema.Str()),
            ["fields"] = Schema.Arr(Schema.Str()),
        };
        if (scenarios) properties["scenarios"] = Schema.Arr(Schema.Str());
        return Schema.Obj(properties);
    }

    private static readonly Dictionary<string, System.Text.Json.JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["payloadRequirements"] = Schema.Arr(Requirement(scenarios: false)),
        ["requirements"] = Schema.Arr(Requirement(scenarios: true)),
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
            Tarefa: liste os requisitos de dados reais para montar payloads completos e coerentes e materializar os
            cenários na API em execução. São dois tipos:

            payloadRequirements (P1, P2...): de onde vêm os valores reais de TODOS os campos do payload (payload_fields),
            não só dos que as condições usam. O objetivo é um payload que pareça um registro real ("gerente": um nome
            real, "email": um e-mail real), e não "texto" ou "usuario@exemplo.com".
            - Agrupe os campos pela entidade/endpoint que pode fornecê-los. Ex.: "Registro existente da mesma entidade
              (nome, email, telefone, cnpj)", "Hierarquia existente (hierarquiaSuperiorId)", "Tipo de pessoa
              existente (pessoaTipoId)".
            - Ids e chaves estrangeiras: um registro existente da entidade referenciada.
            - Campos de texto, contato e documento: um registro existente parecido (listagem/consulta da mesma
              entidade ou de uma relacionada) serve de modelo; campos únicos serão derivados dele.
            - fields: os campos (como em payload_fields.path); entity: a entidade (ou ""); constraints: o que o dado
              precisa respeitar para o caminho feliz (ex.: "Ativo == true"). Campos sem nenhuma fonte na API ficam de
              fora (receberão um valor sintético válido).

            requirements (R1, R2...): o estado específico de que cada cenário depende.
            - Um requisito é um dado do estado (banco, serviço, usuário/configuração) que precisa existir ou não
              existir, com as propriedades exigidas pelas condições e suposições. Ex.: "Veículo existente com
              Disponivel = true", "Id de veículo que não existe", "Placa já cadastrada", "hierarquiaSuperiorId de uma
              hierarquia inativa".
            - Não repita aqui o que o payload base já resolve: só o estado que diferencia o cenário.
            - scenarios: os ids dos cenários que dependem do requisito; fields, entity e constraints como acima
              (constraints como no código quando possível). Agrupe cenários que podem usar o mesmo dado. Cenários sem
              dependência de estado não precisam de requisito.

            notes: suposições que não podem ser atendidas pela API (ex.: depende de serviço externo).
            """);

        var response = await ai.AskAsync("planejar requisitos de dados", prompt.ToString(), ResponseSchema, cancellationToken);
        if (response is null) return [];

        var known = scenarios.Select(s => s.Id).ToHashSet();
        var requirements = new List<DataRequirement>();
        foreach (var (property, kind, prefix) in new[] { ("payloadRequirements", DataRequirementKinds.Payload, "P"), ("requirements", DataRequirementKinds.Scenario, "R") })
        {
            var count = 0;
            foreach (var r in Read.Arr(response, property))
            {
                count++;
                var id = Read.NonEmpty(Read.Str(r, "id")) ?? $"{prefix}{count}";
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || requirements.Any(x => x.Id == id)) id = $"{prefix}{count}";
                requirements.Add(new DataRequirement
                {
                    Id = id,
                    Kind = kind,
                    Description = Read.Str(r, "description"),
                    Entity = Read.NonEmpty(Read.Str(r, "entity")),
                    Constraints = Read.Strs(r, "constraints"),
                    Scenarios = Read.Strs(r, "scenarios").Where(known.Contains).ToList(),
                    Fields = Read.Strs(r, "fields"),
                });
            }
        }
        notes.AddRange(Read.Strs(response.Value, "notes").Select(n => $"Requisitos: {n}"));
        return requirements;
    }
}
