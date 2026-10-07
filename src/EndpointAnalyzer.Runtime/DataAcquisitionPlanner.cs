using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// DataAcquisitionPlanner (Fase 4): a IA escolhe, no catálogo, os endpoints que trazem os dados de cada requisito — os
/// registros completos para o payload base e o estado específico de cada cenário ("UnidadeFabril com RamoAtividade =
/// Planta → GET /unidades"); as requisições são executadas e os dados encontrados
/// vão para o ScenarioContext, com a execução de onde vieram. Rodadas curtas: a IA vê as respostas antes de pedir mais.
/// </summary>
internal sealed class DataAcquisitionPlanner(RuntimeAi ai, RuntimeOptions options)
{
    internal static readonly Dictionary<string, JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["requests"] = Schema.Arr(Schema.Request()),
        ["context"] = Schema.Arr(Schema.ContextItem()),
        ["unsatisfiable"] = Schema.Arr(Schema.Obj(new() { ["requirementId"] = Schema.Str(), ["reason"] = Schema.Str() })),
        ["done"] = Schema.Bool(),
    });

    public async Task AcquireAsync(EndpointAnalysisContext analysis, List<DataRequirement> requirements, EndpointCatalog catalog,
        ScenarioContext context, ScenarioExecutor executor, RuntimeValidation report, CancellationToken cancellationToken)
    {
        if (requirements.Count == 0) return;

        var executedLastRound = false;
        for (var round = 1; round <= options.MaxAcquisitionRounds + 1; round++)
        {
            var final = round > options.MaxAcquisitionRounds || executor.Remaining == 0;
            if (final && !executedLastRound) break;
            if (requirements.All(r => r.Status != "pendente") && !executedLastRound) break;

            var prompt = new StringBuilder();
            prompt.AppendLine($"Endpoint analisado: {analysis.Endpoint.HttpMethod} {analysis.Endpoint.Route}");
            prompt.AppendLine($"Rodada {round} de {options.MaxAcquisitionRounds}. Requisições restantes: {executor.Remaining}. " +
                              $"Escrita permitida: {(executor.WritesAllowed ? "sim" : "não (apenas GET)")}.");
        prompt.AppendLine(executor.Authenticated
            ? "Autenticação: todas as requisições já levam o token da API informado pelo usuário; não tente obter outro token."
            : "Autenticação: nenhum token informado; se a API responder 401, o cenário depende de autenticação (não tente adivinhar credenciais).");
            prompt.AppendLine();
            prompt.Append(RuntimeAi.Section("requirements", RuntimeAi.Json(requirements.Select(r => new { r.Id, r.Kind, r.Description, r.Entity, r.Constraints, r.Fields, r.Status, r.Reason }))));
            prompt.Append(RuntimeAi.Section("catalog", catalog.Describe()));
            prompt.Append(RuntimeAi.Section("context", context.Describe()));
            prompt.Append(RuntimeAi.Section("executions", Executions(report, RuntimePhases.Acquisition)));
            prompt.AppendLine(final
                ? """
                  Rodada final: não peça novas requisições (requests vazio). Registre em context os dados encontrados nas
                  respostas das execuções e marque em unsatisfiable os requisitos que continuam sem dado.
                  """
                : $$"""
                  Tarefa:
                  - requests: até {{options.MaxRequestsPerRound}} requisições para buscar dados dos requisitos pendentes. Só rotas do
                    catálogo, com os valores de rota e query preenchidos (url relativa, ex.: /api/veiculos?ativo=true).
                    bodyJson: o body em JSON ou "" quando não houver. Prefira listagens/consultas (GET); escrita só para
                    criar um dado que não existe e só se permitida. Uma mesma consulta pode atender vários requisitos:
                    não repita requisições já executadas.
                  - Requisitos kind = "payload": busque registros completos (listagem ou consulta por id da mesma
                    entidade e das entidades referenciadas) para preencher todos os campos do payload com dados reais,
                    coerentes entre si (o e-mail, o telefone e o nome do mesmo registro, por exemplo).
                  - Requisitos kind = "cenario": o estado específico de que os cenários dependem.
                  - context: dados já encontrados nas respostas das execuções acima (executionId = a execução EX-.. cuja
                    resposta contém o dado, ou "" se derivado). key curta e reutilizável (ex.: "veiculoDisponivel",
                    "pessoaModelo"), valueJson com o registro (completo para requisitos de payload) ou os campos
                    relevantes (ex.: {"id": 37, "disponivel": true}).
                  - unsatisfiable: requisitos que não podem ser atendidos pelos endpoints disponíveis, com o motivo.
                  - done: true quando não há mais nada útil para buscar.
                  """);

            var response = await ai.AskAsync($"adquirir dados (rodada {round})", prompt.ToString(), ResponseSchema, cancellationToken);
            if (response is null) break;

            ApplyContext(response.Value, requirements, context, RuntimePhases.Acquisition);
            foreach (var u in Read.Arr(response, "unsatisfiable"))
                if (requirements.FirstOrDefault(r => r.Id == Read.Str(u, "requirementId")) is { Status: "pendente" } requirement)
                {
                    requirement.Status = "insatisfeito";
                    requirement.Reason = Read.NonEmpty(Read.Str(u, "reason"));
                }

            executedLastRound = false;
            if (final) break;
            foreach (var r in Read.Arr(response, "requests").Take(options.MaxRequestsPerRound))
            {
                if (ParseRequest(r, out var reason) is not { } request) continue;
                var execution = await executor.ExecuteAsync(request, RuntimePhases.Acquisition, reason: reason, cancellationToken: cancellationToken);
                executedLastRound |= !execution.Blocked;
                if (!request.IsRead && execution.Status is >= 200 and < 300) context.InvalidateFacts();
            }
            if (Read.Bool(response.Value, "done") && !executedLastRound) break;
        }

        foreach (var r in requirements.Where(r => r.Status == "pendente"))
            r.Reason ??= "nenhum dado real encontrado para o requisito";
    }

    /// <summary>Dados citados pela IA; o requisito só conta como atendido com um dado verificado na resposta.</summary>
    internal static void ApplyContext(JsonElement response, List<DataRequirement> requirements, ScenarioContext context, string origin)
    {
        foreach (var c in Read.Arr(response, "context"))
        {
            var key = Read.NonEmpty(Read.Str(c, "key"));
            var value = Read.NonEmpty(Read.Str(c, "valueJson"));
            if (key is null || value is null) continue;
            var ids = Read.Strs(c, "requirementIds");
            var item = context.Add(key, Read.Str(c, "description"), value, ids, Read.NonEmpty(Read.Str(c, "executionId")), origin);
            foreach (var requirement in requirements.Where(r => ids.Contains(r.Id)))
            {
                if (item.Verified)
                {
                    requirement.Status = "atendido";
                    requirement.Reason = null;
                }
                else if (requirement.Status == "pendente")
                    requirement.Reason = $"dado \"{key}\" sem evidência na resposta citada";
            }
        }
    }

    internal static RuntimeRequest? ParseRequest(JsonElement r, out string reason)
    {
        reason = Read.Str(r, "reason");
        var url = Read.NonEmpty(Read.Str(r, "url"));
        if (url is null) return null;
        JsonNode? body = null;
        if (Read.NonEmpty(Read.Str(r, "bodyJson")) is { } json)
        {
            try { body = JsonNode.Parse(json); }
            catch (JsonException) { return null; }
        }
        var method = Read.NonEmpty(Read.Str(r, "method"))?.ToUpperInvariant() ?? "GET";
        return new RuntimeRequest(method, url, null, method == "GET" ? null : body);
    }

    /// <summary>Execuções para o prompt (corpo da resposta resumido).</summary>
    internal static string Executions(RuntimeValidation report, params string[] phases)
    {
        var list = report.Executions.Where(e => phases.Length == 0 || phases.Contains(e.Phase)).TakeLast(40).ToList();
        if (list.Count == 0) return "(nenhuma)";
        return string.Join("\n", list.Select(e =>
            $"{e.Id} {e.Method} {e.Url}{(e.RequestBody is null ? "" : " body=" + ScenarioContext.Truncate(e.RequestBody.ToJsonString(), 400))}"
            + (e.Blocked ? $" → BLOQUEADA ({e.Reason})"
                : $" → {(e.Status?.ToString() ?? "sem resposta")}{(e.Exception is null ? "" : $" [{e.Exception}]")}: {ScenarioContext.Truncate(e.ResponseBody ?? "", 1500)}")));
    }
}
