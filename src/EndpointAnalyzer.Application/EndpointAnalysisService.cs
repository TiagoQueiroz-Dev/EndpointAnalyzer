using EndpointAnalyzer.AI;
using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Core;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Orquestra todo o processo: call graph → condições → alterações → contexto → IA.
/// Com IA, a matriz de cenários também é validada com a API em execução (RuntimeValidationService).
/// </summary>
public class EndpointAnalysisService(
    SolutionCache solutions,
    IEndpointScanner scanner,
    ICallGraphBuilder callGraph,
    IConditionAnalyzer conditions,
    IEntityChangeAnalyzer changes,
    IAnalysisContextBuilder context,
    IScenarioGenerator scenarios,
    AnalysisCache cache,
    IAiProviderSelector? aiSelector = null,
    ILogger<EndpointAnalysisService>? logger = null,
    RuntimeValidationService? runtime = null,
    ApiTokenChecker? tokens = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<EndpointAnalysisService>.Instance;

    private sealed record StaticAnalysis(LoadedSolution Solution, EndpointAnalysisContext Context, ScenarioModel Scenarios);

    public async Task<bool> IsAiAvailableAsync(CancellationToken cancellationToken = default) =>
        aiSelector is not null && await aiSelector.SelectAsync(cancellationToken) is not null;

    /// <summary>A análise com IA valida os cenários em runtime quando a requisição não diz o contrário (Runtime:Enabled).</summary>
    public bool RuntimeValidationEnabled => runtime?.Options.Enabled == true;

    public async Task<IReadOnlyList<EndpointInfo>> ListEndpointsAsync(string solutionPath, bool reload = false, CancellationToken cancellationToken = default)
    {
        var loaded = await solutions.GetAsync(solutionPath, reload, cancellationToken);
        return await scanner.ScanAsync(loaded, cancellationToken);
    }

    /// <summary>Localiza o endpoint por id ("POST /api/programacoes") ou por Controller.Action.</summary>
    public async Task<EndpointInfo> FindEndpointAsync(string solutionPath, string endpointId, CancellationToken cancellationToken = default)
    {
        var endpoints = await ListEndpointsAsync(solutionPath, cancellationToken: cancellationToken);
        var normalized = string.Join(' ', endpointId.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return endpoints.FirstOrDefault(e => string.Equals(e.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? endpoints.FirstOrDefault(e => string.Equals($"{e.Controller}.{e.Action}", normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Endpoint não encontrado: {endpointId}");
    }

    /// <summary>Somente a análise estática (Roslyn), sem IA.</summary>
    public async Task<EndpointAnalysisContext> BuildContextAsync(string solutionPath, EndpointInfo endpoint, CancellationToken cancellationToken = default) =>
        (await BuildStaticAsync(solutionPath, endpoint, cancellationToken)).Context;

    private async Task<StaticAnalysis> BuildStaticAsync(string solutionPath, EndpointInfo endpoint, CancellationToken cancellationToken)
    {
        var loaded = await solutions.GetAsync(solutionPath, cancellationToken: cancellationToken);

        var graph = await callGraph.BuildAsync(loaded, endpoint, cancellationToken);
        var foundConditions = await conditions.AnalyzeAsync(graph, cancellationToken);
        var foundChanges = await changes.AnalyzeAsync(graph, cancellationToken);

        var analysisContext = context.Build(graph, foundConditions, foundChanges);
        // A matriz é a mesma de GenerateAsync; o modelo guarda as restrições para a validação em runtime.
        var model = await scenarios.GenerateModelAsync(graph, analysisContext, cancellationToken);
        analysisContext.Scenarios = model.Matrix;
        return new StaticAnalysis(loaded, analysisContext, model);
    }

    /// <param name="validateRuntime">Com IA: valida a matriz com a API em execução. Nulo = Runtime:Enabled. Sem IA, nunca roda.</param>
    /// <param name="apiToken">Token da API analisada, enviado no header Authorization das requisições da validação em runtime.</param>
    public async Task<EndpointAnalysisReport> AnalyzeAsync(string solutionPath, EndpointInfo endpoint, bool useAi = true, bool? validateRuntime = null,
        string? apiToken = null, CancellationToken cancellationToken = default)
    {
        var analysis = await BuildStaticAsync(solutionPath, endpoint, cancellationToken);
        var analysisContext = analysis.Context;
        var (commit, branch) = GitInfo.Read(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);

        var report = new EndpointAnalysisReport
        {
            Context = analysisContext,
            Version = new AnalysisVersion
            {
                Commit = commit,
                Branch = branch,
                AnalyzerVersion = AnalyzerInfo.Version,
            },
        };

        if (!useAi) return report;
        var ai = aiSelector is null ? null : await aiSelector.SelectAsync(cancellationToken);
        if (ai is null)
            throw new InvalidOperationException("Nenhuma IA disponível. Entre com sua conta do Claude (plano mensal) ou defina ANTHROPIC_API_KEY.");

        report.Version.AiModel = ai.Model;

        // Documentação e validação em runtime são independentes: rodam em paralelo.
        var runtimeTask = runtime is not null && (validateRuntime ?? runtime.Options.Enabled)
            ? runtime.ValidateAsync(analysis.Solution, analysisContext, analysis.Scenarios, ai, apiToken, cancellationToken)
            : null;

        try
        {
            await DocumentAsync(report, endpoint, ai, cancellationToken);
        }
        finally
        {
            // A API iniciada pela validação é encerrada mesmo se a documentação falhar.
            if (runtimeTask is not null) report.Runtime = await runtimeTask;
        }
        return report;
    }

    /// <summary>
    /// Sobe o serviço do endpoint (o mesmo AppRunner e o mesmo projeto da validação em runtime) e confere se ele aceita
    /// o token: GET protegido sem e com o token.
    /// </summary>
    public async Task<ApiTokenCheck> CheckApiTokenAsync(string solutionPath, EndpointInfo endpoint, string token, CancellationToken cancellationToken = default)
    {
        if (tokens is null) throw new InvalidOperationException("Verificação de token indisponível.");
        var loaded = await solutions.GetAsync(solutionPath, cancellationToken: cancellationToken);
        try
        {
            return await tokens.CheckAsync(loaded, endpoint, token, cancellationToken);
        }
        catch (RuntimeStartException e)
        {
            return new ApiTokenCheck(false, $"Não foi possível subir o serviço para testar o token: {e.Message}");
        }
    }

    private async Task DocumentAsync(EndpointAnalysisReport report, EndpointInfo endpoint, IAiProvider ai, CancellationToken cancellationToken)
    {
        var analysisContext = report.Context;

        // A chave é o que a IA recebe: o payload dos cenários (com datas relativas a hoje) não entra no prompt nem no cache.
        var key = AnalysisCache.Key(PromptBuilder.BuildUserPrompt(analysisContext), ai.Model, AnalyzerInfo.Version);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            _logger.LogInformation("Análise de {Endpoint} reaproveitada do cache ({Key})", endpoint.Id, key);
            report.Ai = cached;
            report.FromCache = true;
            return;
        }

        _logger.LogInformation("Enviando {Endpoint} para {Model}", endpoint.Id, ai.Model);
        report.Ai = await ai.AnalyzeAsync(analysisContext, cancellationToken);
        await cache.SetAsync(key, report.Ai, cancellationToken);
    }
}
