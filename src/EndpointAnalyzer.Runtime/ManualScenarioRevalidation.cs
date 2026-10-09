using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

public sealed class ScenarioRevalidationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public partial class RuntimeValidationService
{
    public async Task<RuntimeExecution> RevalidateScenarioAsync(LoadedSolution solution, EndpointAnalysisContext analysis,
        ScenarioModel model, RuntimeValidation report, string scenarioId, JsonNode? body, string? apiToken = null,
        CancellationToken cancellationToken = default, Func<bool>? sourceIsCurrent = null)
    {
        var candidate = PrepareManual(model, report, scenarioId, body);
        var catalog = await EndpointCatalog.BuildAsync(solution, analysis.Endpoint, cancellationToken);
        var request = RuntimeRequest.From(candidate.Request);
        if (catalog.Match(request.Method, request.Url) is null)
            throw new ScenarioRevalidationException(403, "A rota do cenário não pertence ao catálogo da API.");
        if (!request.IsRead && !options.AllowWrites)
            throw new ScenarioRevalidationException(403, "Escrita desligada (Runtime:AllowWrites = false).");
        try
        {
            await using var app = await runner.StartAsync(solution, analysis.Endpoint, _ => { }, cancellationToken);
            if (sourceIsCurrent?.Invoke() == false)
                throw new ScenarioRevalidationException(409, "O código ou a configuração mudou durante a inicialização da API. Execute o cenário novamente após concluir as alterações.");
            // Orçamento de uma chamada; o limite acumulado das tentativas manuais pertence à sessão.
            var executor = new ScenarioExecutor(app.Client, options, catalog, options.AllowWrites, report,
                ApiTokenChecker.Authorization(apiToken), requestBudget: 1);
            return await ExecuteManualAsync(model, report, candidate, executor, cancellationToken);
        }
        catch (RuntimeStartException e)
        {
            throw new ScenarioRevalidationException(503, $"A API não está disponível para reanálise: {e.Message}");
        }
    }

    internal Task<RuntimeExecution> ExecuteManualAsync(ScenarioModel model, RuntimeValidation report, string scenarioId,
        JsonNode? body, ScenarioExecutor executor, CancellationToken cancellationToken = default) =>
        ExecuteManualAsync(model, report, PrepareManual(model, report, scenarioId, body), executor, cancellationToken);

    private sealed record ManualCandidate(Scenario Scenario, ValidatedScenario Target, ScenarioRequest Request);

    private ManualCandidate PrepareManual(ScenarioModel model, RuntimeValidation report, string id, JsonNode? body)
    {
        var target = report.Matrix?.Scenarios.FirstOrDefault(s => s.Id == id)
            ?? throw new ScenarioRevalidationException(404, "Cenário não encontrado na matriz validada.");
        if (target.Status is not (ScenarioValidationStatuses.Inconclusive or ScenarioValidationStatuses.NotMaterialized))
            throw new ScenarioRevalidationException(409, "Somente cenários inconclusivos ou não materializados podem ser reanalisados.");
        if (report.Executions.Count(e => e.Phase == RuntimePhases.Manual && e.ScenarioId == id) >= options.MaxManualAttemptsPerScenario)
            throw new ScenarioRevalidationException(429, "Limite de tentativas manuais do cenário atingido.");
        if ((body?.ToJsonString().Length ?? 4) > options.MaxManualBodyChars)
            throw new ScenarioRevalidationException(413, "O payload excede o tamanho máximo permitido.");
        var scenario = model.Matrix.Scenarios.FirstOrDefault(s => s.Id == id)
            ?? throw new ScenarioRevalidationException(404, "O cenário não possui restrições estáticas.");
        var original = target.Request ?? scenario.Request;
        var request = new ScenarioRequest
        {
            Method = original.Method,
            Url = original.Url,
            Route = original.Route is null ? null : new(original.Route),
            Query = original.Query is null ? null : new(original.Query),
            Headers = original.Headers?.Where(p => !ScenarioExecutor.SensitiveHeader(p.Key)).ToDictionary(),
            Body = body?.DeepClone(),
        };
        // A tentativa manual envia o body informado; a resposta da API decide se o esperado foi reproduzido.
        return new ManualCandidate(scenario, target, request);
    }

    private static async Task<RuntimeExecution> ExecuteManualAsync(ScenarioModel model, RuntimeValidation report,
        ManualCandidate candidate, ScenarioExecutor executor, CancellationToken cancellationToken)
    {
        var target = candidate.Target;
        var previousCount = report.Executions.Count;
        RuntimeExecution execution;
        try
        {
            execution = await executor.ExecuteAsync(RuntimeRequest.From(candidate.Request), RuntimePhases.Manual,
                target.Id, target.Attempts + 1, "Reanálise manual com payload informado pelo usuário", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (report.Executions.Count > previousCount) ApplyManual(model, report, candidate, report.Executions[^1]);
            throw;
        }
        return ApplyManual(model, report, candidate, execution);
    }

    private static RuntimeExecution ApplyManual(ScenarioModel model, RuntimeValidation report, ManualCandidate candidate, RuntimeExecution execution)
    {
        var target = candidate.Target;
        var match = OutcomeMatcher.Match(candidate.Scenario, target.Expected, null, execution, model.Matrix);
        // Nenhum baseline anterior ou fato do relatório é usado como evidência atual de isolamento.
        execution.MatchLevel = match.Level.ToString();
        execution.Evidence = [.. match.Evidence];
        execution.Reasons = [.. match.Reasons];
        target.Request = candidate.Request;
        target.Observed = new ObservedResult { HttpStatus = execution.Status, Body = execution.ResponseBody, Exception = execution.Exception, ElapsedMs = execution.ElapsedMs };
        target.ExecutionId = execution.Id;
        target.Attempts++;
        target.Evidence = [.. execution.Evidence];
        target.Reasons = [.. execution.Reasons];
        target.Status = match.Level == MatchLevel.Confirmed ? ScenarioValidationStatuses.Confirmed : ScenarioValidationStatuses.Inconclusive;
        // Bindings/precondições anteriores não são provas da requisição nova.
        target.Bindings = [];
        target.Preconditions = [];
        foreach (var status in ScenarioValidationStatuses.All)
            report.Matrix!.Counts[status] = report.Matrix.Scenarios.Count(s => s.Status == status) + report.Matrix.Removed.Count(s => s.Status == status);
        if (report.Status != RuntimeValidationStatuses.Failed)
            report.Status = report.Matrix!.Scenarios.Any(s => s.Status is ScenarioValidationStatuses.Inconclusive or ScenarioValidationStatuses.NotMaterialized)
                ? RuntimeValidationStatuses.Partial : RuntimeValidationStatuses.Completed;
        report.Log.Add($"Reanálise manual {target.Id} ({execution.Id}): {match.Level} — {match.Observed}");
        return execution;
    }
}
