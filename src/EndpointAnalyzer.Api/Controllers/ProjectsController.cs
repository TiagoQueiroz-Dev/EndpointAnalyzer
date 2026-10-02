using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace EndpointAnalyzer.Api.Controllers;

public record ConditionSourceRequest(string? SolutionPath, string Condition, List<MethodLocation> Methods);

[ApiController]
[Route("api/projects")]
public class ProjectsController(EndpointAnalysisService service, SourceViewer sources, IConfiguration configuration, IWebHostEnvironment environment) : ControllerBase
{
    /// <summary>Lista os endpoints POST/PUT/PATCH da solução.</summary>
    [HttpGet("endpoints")]
    public async Task<ActionResult<IEnumerable<EndpointInfo>>> Endpoints([FromQuery] string? path, [FromQuery] bool reload, CancellationToken cancellationToken)
    {
        var solution = SolutionPath.Resolve(path, configuration, environment);
        return Ok(await service.ListEndpointsAsync(solution, reload, cancellationToken));
    }

    /// <summary>Abre o explorador de arquivos (na máquina do analisador) para escolher a .sln/.slnx/.csproj.</summary>
    [HttpPost("browse")]
    [LocalOnly]
    public async Task<IActionResult> Browse([FromQuery] string? current, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { path = await FileDialog.OpenSolutionAsync(current, cancellationToken) });
        }
        catch (PlatformNotSupportedException e)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = e.Message });
        }
    }

    /// <summary>Código de um arquivo da solução, com o trecho que começa em <paramref name="line"/> em destaque.</summary>
    [HttpGet("source")]
    public async Task<ActionResult<SourceView>> Source([FromQuery] string? path, [FromQuery] string file, [FromQuery] int line,
        [FromQuery] int? endLine, CancellationToken cancellationToken)
    {
        var solution = SolutionPath.Resolve(path, configuration, environment);
        var view = await sources.GetAsync(solution, file, line, endLine, cancellationToken);
        return view is null ? NotFound(new { error = $"Código não encontrado na solução: {file}:{line}" }) : Ok(view);
    }

    /// <summary>Código de onde vem uma condição do registro (C1, C2...), procurando nos métodos do grafo informados.</summary>
    [HttpPost("source/condition")]
    public async Task<ActionResult<SourceView>> ConditionSource(ConditionSourceRequest request, CancellationToken cancellationToken)
    {
        var solution = SolutionPath.Resolve(request.SolutionPath, configuration, environment);
        var view = await sources.FindConditionAsync(solution, request.Condition, request.Methods, cancellationToken);
        return view is null ? NotFound(new { error = "Origem da condição não encontrada nos métodos do grafo." }) : Ok(view);
    }

    /// <summary>Informações para a interface: solução padrão e se a IA está configurada.</summary>
    [HttpGet("info")]
    public async Task<IActionResult> Info(CancellationToken cancellationToken) => Ok(new
    {
        defaultSolution = SolutionPath.Resolve(null, configuration, environment),
        aiAvailable = await service.IsAiAvailableAsync(cancellationToken),
    });
}

internal static class SolutionPath
{
    public static string Resolve(string? path, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var value = string.IsNullOrWhiteSpace(path) ? configuration["Analyzer:SolutionPath"] : path;
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Informe o caminho da solução (.sln/.slnx/.csproj).");

        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(environment.ContentRootPath, value));
    }
}
