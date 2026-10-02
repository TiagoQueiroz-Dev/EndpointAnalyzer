using EndpointAnalyzer.AI;
using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Core;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Orquestra todo o processo: call graph → condições → alterações → contexto → IA.
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
    ILogger<EndpointAnalysisService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<EndpointAnalysisService>.Instance;

    public async Task<bool> IsAiAvailableAsync(CancellationToken cancellationToken = default) =>
        aiSelector is not null && await aiSelector.SelectAsync(cancellationToken) is not null;

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
    public async Task<EndpointAnalysisContext> BuildContextAsync(string solutionPath, EndpointInfo endpoint, CancellationToken cancellationToken = default)
    {
        var loaded = await solutions.GetAsync(solutionPath, cancellationToken: cancellationToken);

        var graph = await callGraph.BuildAsync(loaded, endpoint, cancellationToken);
        var foundConditions = await conditions.AnalyzeAsync(graph, cancellationToken);
        var foundChanges = await changes.AnalyzeAsync(graph, cancellationToken);

        var analysisContext = context.Build(graph, foundConditions, foundChanges);
        analysisContext.Scenarios = await scenarios.GenerateAsync(graph, analysisContext, cancellationToken);
        return analysisContext;
    }

    public async Task<EndpointAnalysisReport> AnalyzeAsync(string solutionPath, EndpointInfo endpoint, bool useAi = true, CancellationToken cancellationToken = default)
    {
        var analysisContext = await BuildContextAsync(solutionPath, endpoint, cancellationToken);
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

        // A chave é o que a IA recebe: o payload dos cenários (com datas relativas a hoje) não entra no prompt nem no cache.
        var key = AnalysisCache.Key(PromptBuilder.BuildUserPrompt(analysisContext), ai.Model, AnalyzerInfo.Version);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            _logger.LogInformation("Análise de {Endpoint} reaproveitada do cache ({Key})", endpoint.Id, key);
            report.Ai = cached;
            report.FromCache = true;
            return report;
        }

        _logger.LogInformation("Enviando {Endpoint} para {Model}", endpoint.Id, ai.Model);
        report.Ai = await ai.AnalyzeAsync(analysisContext, cancellationToken);
        await cache.SetAsync(key, report.Ai, cancellationToken);
        return report;
    }
}
