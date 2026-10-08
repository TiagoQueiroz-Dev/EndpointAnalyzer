using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EndpointAnalyzer.Api.Controllers;

public record LoginRequest(string? Email);

public record CodeRequest(string Code);

public record ModeRequest(AiMode Mode);

public record ModelRequest(string Model, string? Effort);

/// <summary>
/// Login com a conta do Claude (plano mensal) através do Claude Code instalado na máquina.
/// Só aceita chamadas locais: o login vale para o Claude Code desta máquina.
/// </summary>
[ApiController]
[Route("api/auth")]
[LocalOnly]
public class AuthController(ClaudeCodeAuth auth, AiProviderRouter router, ClaudeModelCatalog catalog, ClaudeCodeOptions claudeCode) : ControllerBase
{
    /// <summary>Modelos da conta logada (disponíveis ou não), efforts de cada um e a seleção atual.</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var status = await auth.GetStatusAsync(refresh, cancellationToken);
        var models = await catalog.GetAsync(status, refresh, cancellationToken);
        return Ok(new { models, model = claudeCode.Model, effort = claudeCode.Effort });
    }

    /// <summary>Escolhe o modelo e o effort usados nas análises pelo plano mensal.</summary>
    [HttpPost("model")]
    public async Task<IActionResult> SelectModel(ModelRequest request, CancellationToken cancellationToken)
    {
        var status = await auth.GetStatusAsync(cancellationToken: cancellationToken);
        var models = await catalog.GetAsync(status, cancellationToken: cancellationToken);
        var model = models.FirstOrDefault(m => m.Id == request.Model)
            ?? throw new ArgumentException($"Modelo desconhecido: {request.Model}");
        if (!model.Available)
            throw new ArgumentException($"{model.Name} não está disponível para esta conta: {model.Reason}");

        var effort = model.Efforts.Count == 0
            ? ""
            : request.Effort is { } e && model.Efforts.Contains(e) ? e : model.DefaultEffort ?? "";

        claudeCode.SaveSelection(model.Id, effort);
        return Ok(new { model = claudeCode.Model, effort = claudeCode.Effort });
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(await DescribeAsync(await auth.GetStatusAsync(refresh, cancellationToken), cancellationToken));

    /// <summary>Inicia o login e devolve a URL a ser aberta no navegador.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Ok(new { loginUrl = await auth.StartLoginAsync(request.Email, cancellationToken) });

    /// <summary>Envia o código exibido pelo Claude após o login.</summary>
    [HttpPost("code")]
    public async Task<IActionResult> Code(CodeRequest request, CancellationToken cancellationToken) =>
        Ok(await DescribeAsync(await auth.SubmitCodeAsync(request.Code, cancellationToken), cancellationToken));

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        await auth.CancelLoginAsync();
        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken) =>
        Ok(await DescribeAsync(await auth.LogoutAsync(cancellationToken), cancellationToken));

    /// <summary>Define de onde vem a IA: auto, subscription (plano mensal) ou api.</summary>
    [HttpPost("mode")]
    public async Task<IActionResult> Mode(ModeRequest request, CancellationToken cancellationToken)
    {
        router.Mode = request.Mode;
        return Ok(await DescribeAsync(await auth.GetStatusAsync(cancellationToken: cancellationToken), cancellationToken));
    }

    private async Task<object> DescribeAsync(ClaudeAuthStatus status, CancellationToken cancellationToken) => new
    {
        // De onde a próxima análise vai usar a IA: "subscription", "api" ou null.
        ActiveSource = await router.SelectAsync(cancellationToken) switch
        {
            ClaudeCodeProvider => "subscription",
            null => null,
            _ => "api",
        },
        status.CliInstalled,
        status.LoggedIn,
        status.UsesSubscription,
        status.AuthMethod,
        status.Email,
        status.SubscriptionType,
        status.OrgName,
        LoginPending = auth.LoginPending,
        Model = claudeCode.Model,
        Effort = claudeCode.Effort,
        ApiConfigured = router.ApiConfigured,
        Mode = router.Mode.ToString().ToLowerInvariant(),
    };
}

/// <summary>Bloqueia chamadas que não venham da própria máquina.</summary>
public sealed class LocalOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var connection = context.HttpContext.Connection;
        var remote = connection.RemoteIpAddress;
        var isLocal = remote is null || System.Net.IPAddress.IsLoopback(remote) || remote.Equals(connection.LocalIpAddress);
        if (!isLocal)
            context.Result = new ObjectResult(new { error = "Login disponível apenas na máquina onde o analisador está rodando." }) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
