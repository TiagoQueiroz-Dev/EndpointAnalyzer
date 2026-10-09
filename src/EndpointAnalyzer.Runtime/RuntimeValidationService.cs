using System.Diagnostics;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// Validação dinâmica da matriz de cenários (analise-ia-validacao-dinamica-cenarios.md), só na análise com IA:
/// <code>
/// matriz candidata → AppRunner → ScenarioRequirementPlanner (requisitos de payload + de cenário)
/// → DataAcquisitionPlanner → GETs direcionados → ScenarioContext → RuntimePayloadMaterializer (payload base completo)
/// → baseline válido → ScenarioExecutor → ScenarioExplorer → MatrixReconciler → matriz validada
/// </code>
/// Falhas (API que não sobe, IA indisponível) não interrompem a análise: o relatório traz o erro e a matriz estática.
/// </summary>
public partial class RuntimeValidationService(RuntimeOptions options, IAppRunner runner, ILogger<RuntimeValidationService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<RuntimeValidationService>.Instance;

    public RuntimeOptions Options => options;

    /// <param name="apiToken">Token da API informado pelo usuário: vai no header Authorization de todas as requisições.</param>
    public async Task<RuntimeValidation> ValidateAsync(LoadedSolution solution, EndpointAnalysisContext analysis, ScenarioModel model, IAiProvider ai,
        string? apiToken = null, CancellationToken cancellationToken = default)
    {
        var report = new RuntimeValidation();
        var watch = Stopwatch.StartNew();
        void Log(string message)
        {
            report.Log.Add($"[{watch.Elapsed:mm\\:ss}] {message}");
            _logger.LogInformation("Runtime {Endpoint}: {Message}", analysis.Endpoint.Id, message);
        }

        ScenarioExplorer? explorer = null;
        ScenarioExecutor? executor = null;
        try
        {
            if (!model.CanMaterialize || model.Matrix.Scenarios.Count == 0)
                throw new RuntimeStartException("A matriz candidata está vazia: não há cenários para validar.");

            // Fase 2: runtime só com IA.
            var catalog = await EndpointCatalog.BuildAsync(solution, analysis.Endpoint, cancellationToken);
            Log($"Catálogo: {catalog.Endpoints.Count} endpoint(s) ({catalog.Endpoints.Count(e => e.ReadOnly)} de leitura).");
            await using var app = await runner.StartAsync(solution, analysis.Endpoint, Log, cancellationToken);
            report.BaseUrl = app.BaseUrl.ToString();
            report.WritesAllowed = options.AllowWrites;
            if (!report.WritesAllowed)
                report.Notes.Add("Escrita desligada (Runtime:AllowWrites = false): só GETs serão executados.");

            var runtimeAi = new RuntimeAi(ai, report, Log);
            executor = new ScenarioExecutor(app.Client, options, catalog, report.WritesAllowed, report, ApiTokenChecker.Authorization(apiToken));
            if (executor.Authenticated) report.Notes.Add("Requisições autenticadas com o token da API informado.");
            var context = new ScenarioContext(report);
            var scenarios = Select(model.Matrix);
            report.Stats.Scenarios = scenarios.Count;
            if (scenarios.Count < model.Matrix.Scenarios.Count)
                report.Notes.Add($"Validados {scenarios.Count} de {model.Matrix.Scenarios.Count} cenários (Runtime:MaxScenarios).");

            // Fase 3: requisitos de dados do payload completo e de cada cenário.
            report.Requirements = await new ScenarioRequirementPlanner(runtimeAi).PlanAsync(analysis, model.Matrix, scenarios, catalog, report.Notes, cancellationToken);
            Log($"{report.Requirements.Count} requisito(s) de dados ({report.Requirements.Count(r => r.Kind == DataRequirementKinds.Payload)} de payload, " +
                $"{report.Requirements.Count(r => r.Kind == DataRequirementKinds.Scenario)} de cenário).");

            // Fase 4: contexto com dados reais (prioritariamente GET).
            await new DataAcquisitionPlanner(runtimeAi, options).AcquireAsync(analysis, report.Requirements, catalog, context, executor, report, cancellationToken);
            Log($"Contexto: {report.Context.Count} dado(s); requisitos atendidos: {report.Requirements.Count(r => r.Status == "atendido")}/{report.Requirements.Count}.");

            // Fase 5: payload base completo (dados reais › derivados › sintéticos › gerador) e o que cada cenário muda.
            var materializer = new RuntimePayloadMaterializer(model, runtimeAi);
            var baselineId = scenarios.FirstOrDefault(s => s.Kind == ScenarioKinds.Success && s.Focus is null)?.Id;
            var plan = await materializer.PlanAsync(scenarios, baselineId, report.Requirements, context, cancellationToken);
            context.SetPayload(plan.Payload);
            Log("Payload base: " + string.Join(", ", PayloadValueOrigins.All
                .Select(o => (Origin: o, Count: plan.Payload.Count(f => f.Origin == o))).Where(x => x.Count > 0).Select(x => $"{x.Count} {x.Origin}")));

            // Fases 5 e 6: baseline e exploração dos demais cenários.
            explorer = new ScenarioExplorer(materializer, runtimeAi, options, executor, context, catalog, report, Log);
            await explorer.RunAsync(analysis, scenarios, plan.Scenarios, report.Requirements, cancellationToken);

            if (app.HasExited) report.Notes.Add($"A API encerrou durante a validação:\n{app.OutputTail}");
        }
        catch (Exception e) when (e is RuntimeStartException or HttpRequestException or IOException or InvalidOperationException)
        {
            report.Status = RuntimeValidationStatuses.Failed;
            report.Error = e.Message;
            Log($"Falhou: {e.Message}");
        }
        finally
        {
            // Fase 7: reconcilia o que foi executado (mesmo se a validação parou no meio).
            if (explorer is not null)
            {
                report.Matrix = MatrixReconciler.Reconcile(model.Matrix, explorer.States, report.Executions, report.WritesAllowed, options);
                if (report.Status != RuntimeValidationStatuses.Failed && executor?.BudgetExhausted == true)
                {
                    report.Status = RuntimeValidationStatuses.Partial;
                    report.Notes.Add($"Limite de {options.MaxRequests} requisições atingido (Runtime:MaxRequests).");
                }
                var open = report.Matrix.Scenarios.Where(s => s.Status == ScenarioValidationStatuses.Inconclusive).Select(s => s.Id).ToList();
                if (open.Count > 0 && report.Status != RuntimeValidationStatuses.Failed)
                {
                    report.Status = RuntimeValidationStatuses.Partial;
                    report.Notes.Add($"Exploração interrompida sem conclusão (confirmado ou inalcançável) para {string.Join(", ", open)}: o motivo (limite atingido ou IA sem proposta nova) está em cada cenário; aumente os limites em Runtime para continuar.");
                }
                Log("Matriz reconciliada: " + string.Join(", ", report.Matrix.Counts.Where(c => c.Value > 0).Select(c => $"{c.Value} {c.Key}")));
            }
            report.DurationMs = watch.ElapsedMilliseconds;
        }
        return report;
    }

    /// <summary>Caminho feliz primeiro (baseline), depois validações, regras, limites e variações de sucesso.</summary>
    private List<Scenario> Select(ScenarioMatrix matrix)
    {
        var order = new[] { ScenarioKinds.Validation, ScenarioKinds.Rule, ScenarioKinds.Boundary, ScenarioKinds.Success };
        return matrix.Scenarios
            .OrderBy(s => s.Kind == ScenarioKinds.Success && s.Focus is null ? -1 : Array.IndexOf(order, s.Kind))
            .Take(Math.Max(1, options.MaxScenarios))
            .ToList();
    }
}
