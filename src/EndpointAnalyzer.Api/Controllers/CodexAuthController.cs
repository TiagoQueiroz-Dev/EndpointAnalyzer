using EndpointAnalyzer.AI.Codex;
using Microsoft.AspNetCore.Mvc;

namespace EndpointAnalyzer.Api.Controllers;

/// <summary>
/// Login com a conta do Codex (ChatGPT) através do Codex CLI instalado na máquina, independente da conta do Claude:
/// as duas podem ficar conectadas ao mesmo tempo. Só aceita chamadas locais: o retorno do login vem para esta máquina.
/// </summary>
[ApiController]
[Route("api/auth/codex")]
[LocalOnly]
public class CodexAuthController(CodexAuth auth, CodexModelCatalog catalog, CodexOptions codex) : ControllerBase
{
    /// <summary>Conta (e-mail, plano), uso do limite, login em andamento e o modelo/effort atuais.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(Describe(await auth.GetStatusAsync(refresh, cancellationToken)));

    /// <summary>Inicia o login com a conta do ChatGPT e devolve a URL a ser aberta no navegador.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(CancellationToken cancellationToken) =>
        Ok(new { loginUrl = await auth.StartLoginAsync(cancellationToken) });

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        await auth.CancelLoginAsync();
        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken) =>
        Ok(Describe(await auth.LogoutAsync(cancellationToken)));

    /// <summary>Modelos da conta logada, efforts de cada um e a seleção atual (padrão da conta quando não há escolha).</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var status = await auth.GetStatusAsync(refresh, cancellationToken);
        var models = await catalog.GetAsync(status, refresh, cancellationToken);
        await catalog.ResolveSelectionAsync(status, cancellationToken);
        return Ok(new { models, model = codex.Model, effort = codex.Effort });
    }

    /// <summary>Escolhe o modelo e o effort usados nas análises com o Codex.</summary>
    [HttpPost("model")]
    public async Task<IActionResult> SelectModel(ModelRequest request, CancellationToken cancellationToken)
    {
        var status = await auth.GetStatusAsync(cancellationToken: cancellationToken);
        var models = await catalog.GetAsync(status, cancellationToken: cancellationToken);
        var model = models.FirstOrDefault(m => m.Id == request.Model)
            ?? throw new ArgumentException($"Modelo desconhecido: {request.Model}");

        var effort = model.Efforts.Count == 0
            ? ""
            : request.Effort is { } e && model.Efforts.Contains(e) ? e : model.DefaultEffort ?? "";

        codex.SaveSelection(model.Id, effort);
        return Ok(new { model = codex.Model, effort = codex.Effort });
    }

    private object Describe(CodexAuthStatus status) => new
    {
        status.CliInstalled,
        status.LoggedIn,
        status.UsesSubscription,
        status.AuthMethod,
        status.Email,
        status.PlanType,
        status.Usage,
        status.LoginError,
        LoginPending = auth.LoginPending,
        Model = codex.Model,
        Effort = codex.Effort,
    };
}
