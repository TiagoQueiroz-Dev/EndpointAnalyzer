using EndpointAnalyzer.AI;
using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Context.BusinessFlow;
using EndpointAnalyzer.Core;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Orquestra todo o processo: call graph → condições → alterações → contexto → IA.
/// Com IA, a matriz de cenários também é validada com a API em execução (RuntimeValidationService) e o fluxo de negócio
/// é polido a partir do call graph e das evidências (BusinessFlowAiAnalyzer).
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
    ApiTokenChecker? tokens = null,
    IBusinessFlowAiAnalyzer? businessFlow = null,
    AnalysisSessionStore? sessions = null)
{
    /// <summary>Abas cuja IA vai na chamada da documentação (Resumo: regras; Cenários: títulos). Negócio tem chamada própria.</summary>
    private const AnalysisSections DocumentationSections = AnalysisSections.Summary | AnalysisSections.Scenarios;

    private readonly ILogger _logger = logger ?? NullLogger<EndpointAnalysisService>.Instance;

    private readonly IBusinessFlowAiAnalyzer _businessFlow = businessFlow ?? new BusinessFlowAiAnalyzer();

    private sealed record StaticAnalysis(LoadedSolution Solution, EndpointAnalysisContext Context, ScenarioModel? Scenarios, string Fingerprint);

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
        (await BuildStaticAsync(solutionPath, endpoint, true, cancellationToken)).Context;

    /// <param name="generateScenarios">Gera a matriz de cenários (Z3); só as abas Cenários e Completo (e o Resumo sem IA) usam.</param>
    private async Task<StaticAnalysis> BuildStaticAsync(string solutionPath, EndpointInfo endpoint, bool generateScenarios, CancellationToken cancellationToken)
    {
        var loaded = await solutions.GetCurrentAsync(solutionPath, cancellationToken);
        var fingerprint = AnalysisSourceVersion.Capture(loaded);

        var graph = await callGraph.BuildAsync(loaded, endpoint, cancellationToken);
        var foundConditions = await conditions.AnalyzeAsync(graph, cancellationToken);
        var foundChanges = await changes.AnalyzeAsync(graph, cancellationToken);

        var analysisContext = context.Build(graph, foundConditions, foundChanges);
        if (!generateScenarios) return new StaticAnalysis(loaded, analysisContext, null, fingerprint);

        // A matriz é a mesma de GenerateAsync; o modelo guarda as restrições para a validação em runtime.
        var model = await scenarios.GenerateModelAsync(graph, analysisContext, cancellationToken);
        analysisContext.Scenarios = model.Matrix;
        return new StaticAnalysis(loaded, analysisContext, model, fingerprint);
    }

    /// <param name="useAi">Sem <paramref name="aiSections"/>: IA em todas as abas pedidas (como antes, rótulos do fluxograma na documentação).</param>
    /// <param name="validateRuntime">Valida a matriz com a API em execução quando a IA dos cenários está ligada. Nulo = Runtime:Enabled.</param>
    /// <param name="apiToken">Token da API analisada, enviado no header Authorization das requisições da validação em runtime.</param>
    /// <param name="sections">
    /// Abas pedidas: só roda o que elas exibem. Matriz de cenários para Cenários/Completo (e Resumo sem IA) e IA só nas abas pedidas.
    /// </param>
    /// <param name="aiSections">
    /// Abas que usam IA (ignora <paramref name="useAi"/>): Resumo → documentação; Negócio → fluxo de negócio polido
    /// (BusinessFlowAiAnalyzer); Cenários → títulos dos cenários e validação em runtime. Completo não usa IA.
    /// </param>
    public async Task<EndpointAnalysisReport> AnalyzeAsync(string solutionPath, EndpointInfo endpoint, bool useAi = true, bool? validateRuntime = null,
        string? apiToken = null, CancellationToken cancellationToken = default, AnalysisSections sections = AnalysisSections.All,
        AnalysisSections? aiSections = null)
    {
        var scenarioTabs = (sections & (AnalysisSections.Scenarios | AnalysisSections.Complete)) != 0;
        var runtimeAllowed = runtime is not null && (validateRuntime ?? runtime.Options.Enabled);
        AnalysisSections documentation;
        bool runBusinessFlow, runRuntime;
        if (aiSections is { } chosen)
        {
            var withAi = chosen & sections & PromptBuilder.AiSections;
            documentation = withAi & DocumentationSections;
            runBusinessFlow = withAi.HasFlag(AnalysisSections.Business);
            runRuntime = withAi.HasFlag(AnalysisSections.Scenarios) && runtimeAllowed;
        }
        else
        {
            documentation = useAi ? sections & PromptBuilder.AiSections : AnalysisSections.None;
            runBusinessFlow = false;
            runRuntime = useAi && scenarioTabs && runtimeAllowed;
        }

        // O Resumo sem IA monta as regras e validações a partir da matriz.
        var generateScenarios = scenarioTabs || (sections.HasFlag(AnalysisSections.Summary) && !documentation.HasFlag(AnalysisSections.Summary));
        var analysis = await BuildStaticAsync(solutionPath, endpoint, generateScenarios, cancellationToken);
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
            Sections = AnalysisSectionNames.ToNames(sections),
        };

        if (documentation == AnalysisSections.None && !runBusinessFlow && !runRuntime)
        {
            sessions?.Add(analysis.Solution, analysis.Scenarios, report, analysis.Fingerprint, apiToken);
            return report;
        }
        var ai = aiSelector is null ? null : await aiSelector.SelectAsync(cancellationToken);
        if (ai is null)
            throw new InvalidOperationException("Nenhuma IA disponível. Entre com sua conta do Claude (plano mensal) ou defina ANTHROPIC_API_KEY.");

        report.Version.AiModel = ai.Model;

        // Documentação, fluxo de negócio e validação em runtime são independentes: rodam em paralelo.
        var runtimeTask = runRuntime
            ? runtime!.ValidateAsync(analysis.Solution, analysisContext, analysis.Scenarios!, ai, apiToken, cancellationToken)
            : null;

        try
        {
            await Task.WhenAll(
                documentation != AnalysisSections.None ? DocumentAsync(report, endpoint, ai, documentation, cancellationToken) : Task.CompletedTask,
                runBusinessFlow ? BusinessFlowAsync(report, endpoint, ai, cancellationToken) : Task.CompletedTask);
        }
        finally
        {
            // A API iniciada pela validação é encerrada mesmo se a documentação falhar.
            if (runtimeTask is not null) report.Runtime = await runtimeTask;
        }
        sessions?.Add(analysis.Solution, analysis.Scenarios, report, analysis.Fingerprint, apiToken);
        return report;
    }

    public async Task<ScenarioRevalidationResult> RevalidateScenarioAsync(string analysisId, string scenarioId, JsonElement body,
        CancellationToken cancellationToken = default, string? apiToken = null)
    {
        if (sessions is null || runtime is null) throw new AnalysisSessionException(503, "Reanálise manual indisponível.");
        if (string.IsNullOrWhiteSpace(analysisId) || string.IsNullOrWhiteSpace(scenarioId))
            throw new ArgumentException("Informe analysisId e scenarioId.");
        if (body.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Informe o body JSON (use null para uma requisição sem corpo).");
        if (body.GetRawText().Length > runtime.Options.MaxManualBodyChars)
            throw new ScenarioRevalidationException(413, "O payload excede o tamanho máximo permitido.");
        ValidateJson(body);
        var session = sessions.Acquire(analysisId);
        try
        {
            if (session.ManualRequests >= runtime.Options.MaxManualRequests)
                throw new AnalysisSessionException(429, "Limite de requisições manuais desta análise atingido.");
            var validation = session.Report.Runtime
                ?? throw new AnalysisSessionException(409, "A análise salva não possui uma matriz validada em runtime.");
            if (apiToken is not null)
            {
                session.Token = string.IsNullOrWhiteSpace(apiToken) ? null : apiToken.Trim();
                session.TokenUnavailable = false;
                sessions.Save(session);
            }
            if (session.TokenUnavailable)
                throw new AnalysisSessionException(409, "Não foi possível recuperar o token protegido. Informe o token da API novamente; a análise salva continua disponível.");
            var solution = await solutions.GetCurrentAsync(session.SolutionPath, cancellationToken);
            var savedEndpoint = session.Report.Context.Endpoint;
            var endpoint = (await scanner.ScanAsync(solution, cancellationToken)).FirstOrDefault(e =>
                e.Id == savedEndpoint.Id && e.Project == savedEndpoint.Project)
                ?? throw new AnalysisSessionException(404, "O endpoint da análise salva não foi encontrado no projeto atual.");
            var current = new EndpointAnalysisContext { Endpoint = endpoint };
            var testedFingerprint = AnalysisSourceVersion.Capture(solution);
            var testedCommit = GitInfo.Read(solution.RootDirectory).Commit;
            var count = validation.Executions.Count;
            RuntimeExecution execution;
            try
            {
                execution = await runtime.RevalidateScenarioAsync(solution, current, session.Model, validation,
                    scenarioId, JsonNode.Parse(body.GetRawText()), session.Token, cancellationToken,
                    () => AnalysisSourceVersion.Capture(solution) == testedFingerprint);
            }
            finally
            {
                // Uma chamada cancelada depois do envio também consome orçamento; não dispara retry automático.
                var attempts = validation.Executions.Skip(count).ToList();
                foreach (var attempt in attempts)
                {
                    attempt.SourceFingerprint = testedFingerprint;
                    attempt.TestedCommit = testedCommit;
                }
                session.ManualRequests += attempts.Count(e => !e.Blocked);
                sessions.Save(session);
            }
            // A resposta é uma cópia: outra tentativa não a altera durante a serialização HTTP.
            return new ScenarioRevalidationResult(analysisId,
                JsonSerializer.Deserialize<RuntimeValidation>(JsonSerializer.Serialize(validation))!,
                ReportRenderer.Markdown(session.Report),
                JsonSerializer.Deserialize<RuntimeExecution>(JsonSerializer.Serialize(execution))!);
        }
        finally { session.Gate.Release(); }
    }

    public EndpointAnalysisReport? SavedAnalysis(string solutionPath, string method, string route, string project)
    {
        var session = sessions?.Latest(solutionPath, method, route, project);
        if (session is null) return null;
        session = sessions!.Acquire(session.Id);
        try { return JsonSerializer.Deserialize<EndpointAnalysisReport>(JsonSerializer.Serialize(session.Report)); }
        finally { session.Gate.Release(); }
    }

    /// <summary>Importa o relatório antigo do navegador sem IA; relatórios já persistidos prevalecem sobre o cache.</summary>
    public async Task<EndpointAnalysisReport> RestoreAnalysisAsync(string solutionPath, EndpointAnalysisReport report,
        string? apiToken = null, CancellationToken cancellationToken = default)
    {
        if (sessions is null) throw new AnalysisSessionException(503, "Persistência de análises indisponível.");
        if (report.AnalysisId is { } id)
        {
            try
            {
                var saved = sessions.Acquire(id);
                try
                {
                    if (!string.Equals(saved.SolutionPath, Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("O caminho da solução não corresponde à análise salva.");
                    if (apiToken is not null)
                    {
                        saved.Token = string.IsNullOrWhiteSpace(apiToken) ? null : apiToken.Trim();
                        saved.TokenUnavailable = false;
                        sessions.Save(saved);
                    }
                    return JsonSerializer.Deserialize<EndpointAnalysisReport>(JsonSerializer.Serialize(saved.Report))!;
                }
                finally { saved.Gate.Release(); }
            }
            catch (AnalysisSessionException e) when (e.StatusCode == 404) { }
        }
        if (report.Context.Scenarios is null || report.Runtime?.Matrix is null)
            throw new ArgumentException("O relatório precisa conter a matriz original e a matriz validada para restaurar a reanálise.");
        var solution = await solutions.GetCurrentAsync(solutionPath, cancellationToken);
        if (!(await scanner.ScanAsync(solution, cancellationToken)).Any(e => e.Id == report.Context.Endpoint.Id && e.Project == report.Context.Endpoint.Project))
            throw new AnalysisSessionException(404, "O endpoint salvo não existe no projeto atual.");
        sessions.Add(solution, null, report, "importado", apiToken);
        return report;
    }

    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException($"Propriedade JSON duplicada: {property.Name}.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJson(item);
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

    /// <summary>
    /// Fluxo de negócio por IA: evidências da análise estática → IA (structured outputs) → validação contra as
    /// evidências → Mermaid. Uma falha aqui não derruba a análise: fica registrada e a aba mostra o fluxo técnico.
    /// </summary>
    private async Task BusinessFlowAsync(EndpointAnalysisReport report, EndpointInfo endpoint, IAiProvider ai, CancellationToken cancellationToken)
    {
        var analysisContext = report.Context;
        var (graph, evidence) = BusinessFlowEvidenceBuilder.Build(analysisContext);
        var flow = new BusinessFlowAnalysis { CallGraph = graph, Evidence = evidence };
        report.BusinessFlow = flow;
        try
        {
            var prompt = BusinessFlowAiAnalyzer.BuildUserPrompt(analysisContext, graph, evidence);
            var key = AnalysisCache.Key($"{BusinessFlowAiAnalyzer.SystemPrompt}\n{prompt}", ai.Model, AnalyzerInfo.Version);
            var result = await cache.GetAsync<BusinessFlowResult>(key, cancellationToken);
            flow.FromCache = result is not null;
            if (result is null)
            {
                _logger.LogInformation("Enviando o fluxo de negócio de {Endpoint} para {Model}", endpoint.Id, ai.Model);
                result = await _businessFlow.AnalyzeAsync(analysisContext, graph, evidence, ai, cancellationToken);
                // O cache guarda a resposta da IA como veio; a validação roda sempre (e altera o objeto).
                await cache.SetAsync(key, result, cancellationToken);
            }

            flow.Issues = BusinessFlowValidator.Validate(result, graph, evidence);
            flow.Result = result;
            flow.Diagrams = BusinessFlowMermaidGenerator.All(result, graph);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Fluxo de negócio por IA de {Endpoint} falhou", endpoint.Id);
            flow.Error = e.Message;
        }
    }

    private async Task DocumentAsync(EndpointAnalysisReport report, EndpointInfo endpoint, IAiProvider ai, AnalysisSections sections, CancellationToken cancellationToken)
    {
        var analysisContext = report.Context;

        // A chave é o que a IA recebe: o payload dos cenários (com datas relativas a hoje) não entra no prompt nem no cache.
        // Com parte das abas, uma análise completa do mesmo código também serve (tem todas as partes).
        var key = AnalysisCache.Key(PromptBuilder.BuildUserPrompt(analysisContext, sections), ai.Model, AnalyzerInfo.Version);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is null && sections != PromptBuilder.AiSections)
            cached = await cache.GetAsync(AnalysisCache.Key(PromptBuilder.BuildUserPrompt(analysisContext), ai.Model, AnalyzerInfo.Version), cancellationToken);
        if (cached is not null)
        {
            _logger.LogInformation("Análise de {Endpoint} reaproveitada do cache ({Key})", endpoint.Id, key);
            report.Ai = cached;
            report.FromCache = true;
            return;
        }

        _logger.LogInformation("Enviando {Endpoint} para {Model}", endpoint.Id, ai.Model);
        report.Ai = await ai.AnalyzeAsync(analysisContext, sections, cancellationToken);
        await cache.SetAsync(key, report.Ai, cancellationToken);
    }
}
