using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
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
}

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

    private async Task<EndpointAnalysisReport> RunAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        var solution = SolutionPath.Resolve(request.SolutionPath, configuration, environment);
        var id = request.EndpointId
            ?? (request.Endpoint is { } e ? $"{e.Method.ToUpperInvariant()} {e.Route}" : null)
            ?? throw new ArgumentException("Informe 'endpoint' ({ method, route }) ou 'endpointId'.");

        var endpoint = await service.FindEndpointAsync(solution, id, cancellationToken);
        return await service.AnalyzeAsync(solution, endpoint, request.UseAi ?? await service.IsAiAvailableAsync(cancellationToken), cancellationToken);
    }
}
