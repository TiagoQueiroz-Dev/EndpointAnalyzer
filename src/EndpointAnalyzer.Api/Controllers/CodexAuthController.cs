using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.Application;
using Microsoft.AspNetCore.Mvc;

namespace EndpointAnalyzer.Api.Controllers;

/// <param name="Account">Conta do Codex (id); nulo = a primeira da ordem.</param>
public record CodexLoginRequest(string? Account = null);

/// <summary>
/// Contas do Codex (ChatGPT) através do Codex CLI instalado na máquina, independentes das contas do Claude: várias contas,
/// cada uma com o próprio login, usadas na ordem cadastrada. Sem <c>account</c>, as rotas valem para a primeira conta. Só
/// aceita chamadas locais: o retorno do login vem para esta máquina.
/// </summary>
[ApiController]
[Route("api/auth/codex")]
[LocalOnly]
public class CodexAuthController(CodexAccounts accounts, CodexOptions codex, AiProviderSettings settings) : ControllerBase
{
    /// <summary>Contas na ordem (e-mail, plano, uso do limite, estado), login em andamento e o modelo/effort atuais.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(await DescribeAsync(refresh, cancellationToken));

    /// <summary>Inicia o login da conta com o ChatGPT e devolve a URL a ser aberta no navegador.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(CodexLoginRequest? request, CancellationToken cancellationToken) =>
        Ok(new { loginUrl = await accounts.StartLoginAsync(request?.Account, cancellationToken) });

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel([FromQuery] string? account)
    {
        await accounts.CancelLoginAsync(account);
        return NoContent();
    }

    /// <summary>Sai da conta (as outras continuam conectadas).</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? account, CancellationToken cancellationToken)
    {
        await accounts.LogoutAsync(account, cancellationToken);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Cadastra uma conta no fim da ordem; o login vem em seguida (login com o id devolvido).</summary>
    [HttpPost("accounts")]
    public async Task<IActionResult> AddAccount(AccountRequest request, CancellationToken cancellationToken)
    {
        var added = accounts.Add(request.Label);
        return Ok(new { id = added.Account.Id, status = await DescribeAsync(false, cancellationToken) });
    }

    [HttpPatch("accounts/{id}")]
    public async Task<IActionResult> RenameAccount(string id, AccountRequest request, CancellationToken cancellationToken)
    {
        accounts.Rename(id, request.Label);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Sai da conta e a retira da ordem.</summary>
    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> RemoveAccount(string id, CancellationToken cancellationToken)
    {
        await accounts.RemoveAsync(id, cancellationToken);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Nova ordem de uso: { "ids": ["principal", "a1b2c3d4"] }.</summary>
    [HttpPut("accounts/order")]
    public async Task<IActionResult> Reorder(OrderRequest request, CancellationToken cancellationToken)
    {
        accounts.Reorder(request.Ids ?? []);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Verificar novamente: esquece o bloqueio (limite de uso ou sessão expirada) e relê login e uso.</summary>
    [HttpPost("accounts/{id}/check")]
    public async Task<IActionResult> Check(string id, CancellationToken cancellationToken)
    {
        await accounts.CheckAsync(id, cancellationToken);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Modelos da primeira conta logada, efforts de cada um e a seleção atual (vale para todas as contas).</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var (services, status) = await ModelSourceAsync(refresh, cancellationToken);
        var models = await services.Catalog.GetAsync(status, refresh, cancellationToken);
        await services.Catalog.ResolveSelectionAsync(status, codex, cancellationToken);
        return Ok(new { models, model = codex.Model, effort = codex.Effort });
    }

    /// <summary>Escolhe o modelo e o effort usados nas análises com o Codex.</summary>
    [HttpPost("model")]
    public async Task<IActionResult> SelectModel(ModelRequest request, CancellationToken cancellationToken)
    {
        var (services, status) = await ModelSourceAsync(false, cancellationToken);
        var models = await services.Catalog.GetAsync(status, cancellationToken: cancellationToken);
        var model = models.FirstOrDefault(m => m.Id == request.Model)
            ?? throw new ArgumentException($"Modelo desconhecido: {request.Model}");

        var effort = model.Efforts.Count == 0
            ? ""
            : request.Effort is { } e && model.Efforts.Contains(e) ? e : model.DefaultEffort ?? "";

        codex.SaveSelection(model.Id, effort);
        return Ok(new { model = codex.Model, effort = codex.Effort });
    }

    /// <summary>Catálogo da primeira conta logada (os modelos dependem do plano); sem nenhuma, o da primeira conta.</summary>
    private async Task<(CodexAccountServices Services, CodexAuthStatus Status)> ModelSourceAsync(bool refresh, CancellationToken cancellationToken)
    {
        foreach (var services in accounts.All)
        {
            var status = await services.Auth.GetStatusAsync(refresh, cancellationToken);
            if (status.LoggedIn) return (services, status);
        }
        var first = accounts.Get(null);
        return (first, await first.Auth.GetStatusAsync(refresh, cancellationToken));
    }

    private async Task<object> DescribeAsync(bool refresh, CancellationToken cancellationToken)
    {
        var list = await accounts.DescribeAsync(refresh, cancellationToken);
        var first = list.FirstOrDefault(a => a.LoggedIn) ?? list.FirstOrDefault();
        return new
        {
            CliInstalled = list.Count == 0 || list.Any(a => a.CliInstalled),
            LoggedIn = list.Any(a => a.LoggedIn),
            UsesSubscription = list.Any(a => a.UsesSubscription),
            first?.AuthMethod,
            first?.Email,
            PlanType = first?.Plan,
            first?.Usage,
            LoginError = list.Select(a => a.LoginError).FirstOrDefault(e => e is not null),
            LoginPending = list.Any(a => a.LoginPending),
            Accounts = list,
            settings.ContinueWithOtherProvider,
            Model = codex.Model,
            Effort = codex.Effort,
        };
    }
}
