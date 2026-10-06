using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace EndpointAnalyzer.Api.Controllers;

public record EndpointSelector(string Method, string Route);

public record AnalysisRequest
{
    public string? SolutionPath { get; init; }

    /// <summary>{ "method": "POST", "route": "/api/programacoes" }</summary>
    public EndpointSelector? Endpoint { get; init; }

    /// <summary>Alternativa: "POST /api/programacoes" ou "ProgramacaoController.Criar".</summary>
    public string? EndpointId { get; init; }

    /// <summary>Nulo = usa IA quando estiver configurada.</summary>
    public bool? UseAi { get; init; }

    /// <summary>Com IA: valida a matriz de cenários com a API em execução. Nulo = Runtime:Enabled.</summary>
    public bool? ValidateRuntime { get; init; }

    /// <summary>Token da API analisada (header Authorization das requisições da validação em runtime).</summary>
    public string? ApiToken { get; init; }

    /// <summary>Abas a produzir: summary, business, complete, scenarios. Nulo = todas; a análise roda só o que elas usam.</summary>
    public List<string>? Sections { get; init; }

    /// <summary>
    /// Abas que usam IA (substitui UseAi/ValidateRuntime): summary (documentação), business (fluxo de negócio polido) e
    /// scenarios (títulos e validação com a API em execução). Nulo = comportamento de UseAi.
    /// </summary>
    public List<string>? Ai { get; init; }
}

/// <summary>{ "endpoint": { "method": "POST", "route": "/api/x" }, "token": "eyJ..." } (token com ou sem "Bearer ").</summary>
public record ApiTokenRequest(string? SolutionPath, EndpointSelector? Endpoint, string? EndpointId, string Token);

public record AnalysisResponse(EndpointAnalysisReport Report, string Markdown);

[ApiController]
[Route("api/analysis")]
public class AnalysisController(EndpointAnalysisService service, IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>Analisa um endpoint: análise estática + documentação gerada pela IA.</summary>
    [HttpPost]
    public async Task<ActionResult<AnalysisResponse>> Analyze(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var report = await RunAsync(request, cancellationToken);
        return Ok(new AnalysisResponse(report, ReportRenderer.Markdown(report)));
    }

    /// <summary>Mesma análise, devolvendo a documentação em Markdown.</summary>
    [HttpPost("markdown")]
    [Produces("text/markdown")]
    public async Task<IActionResult> Markdown(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var report = await RunAsync(request, cancellationToken);
        return Content(ReportRenderer.Markdown(report), "text/markdown; charset=utf-8");
    }

    /// <summary>Sobe o serviço do endpoint (como na análise) e confere se o token é aceito: { valid, message }.</summary>
    [HttpPost("token")]
    public async Task<ActionResult<ApiTokenCheck>> CheckToken(ApiTokenRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) throw new ArgumentException("Informe o token da API.");
        var solution = SolutionPath.Resolve(request.SolutionPath, configuration, environment);
        var endpoint = await service.FindEndpointAsync(solution, EndpointId(request.EndpointId, request.Endpoint), cancellationToken);
        return Ok(await service.CheckApiTokenAsync(solution, endpoint, request.Token, cancellationToken));
    }

    private static string EndpointId(string? id, EndpointSelector? endpoint) =>
        id ?? (endpoint is { } e ? $"{e.Method.ToUpperInvariant()} {e.Route}" : null)
        ?? throw new ArgumentException("Informe 'endpoint' ({ method, route }) ou 'endpointId'.");

    private async Task<EndpointAnalysisReport> RunAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var solution = SolutionPath.Resolve(request.SolutionPath, configuration, environment);
        var endpoint = await service.FindEndpointAsync(solution, EndpointId(request.EndpointId, request.Endpoint), cancellationToken);
        var sections = AnalysisSectionNames.Parse(request.Sections);
        AnalysisSections? ai = request.Ai is null ? null : AnalysisSectionNames.Parse(request.Ai, allowEmpty: true);
        var useAi = ai is null ? request.UseAi ?? await service.IsAiAvailableAsync(cancellationToken) : ai != AnalysisSections.None;
        return await service.AnalyzeAsync(solution, endpoint, useAi, request.ValidateRuntime, request.ApiToken, cancellationToken, sections, ai);
    }
}
