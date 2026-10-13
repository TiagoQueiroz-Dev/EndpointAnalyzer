using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Application;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EndpointAnalyzer.Api.Controllers;

/// <param name="Account">Conta Claude (id); nulo = a primeira da ordem.</param>
public record LoginRequest(string? Email, string? Account = null);

public record CodeRequest(string Code, string? Account = null);

public record ModeRequest(AiMode Mode);

public record ModelRequest(string Model, string? Effort);

public record AccountRequest(string? Label);

public record OrderRequest(List<string> Ids);

public record SettingsRequest(bool ContinueWithOtherProvider);

/// <summary>
/// Contas do Claude (plano mensal) através do Claude Code instalado na máquina: várias contas, cada uma com o próprio login,
/// usadas na ordem cadastrada. Sem <c>account</c>, as rotas valem para a primeira conta. Só aceita chamadas locais: o login
/// vale para o Claude Code desta máquina.
/// </summary>
[ApiController]
[Route("api/auth")]
[LocalOnly]
public class AuthController(ClaudeAccounts accounts, AiProviderRouter router, ClaudeCodeOptions claudeCode, AiProviderSettings settings)
    : ControllerBase
{
    /// <summary>Uso dos limites do plano (sessão de 5 horas e semana) da conta; nulo quando ela não tem limites de plano.</summary>
    [HttpGet("usage")]
    public async Task<IActionResult> Usage([FromQuery] string? account, [FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(new { usage = await accounts.UsageAsync(account, refresh, cancellationToken) });

    /// <summary>Modelos da primeira conta logada (disponíveis ou não), efforts de cada um e a seleção atual (vale para todas as contas).</summary>
    [HttpGet("models")]
    public async Task<IActionResult> Models([FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var (catalog, status) = await ModelSourceAsync(refresh, cancellationToken);
        var models = await catalog.GetAsync(status, refresh, cancellationToken);
        return Ok(new { models, model = claudeCode.Model, effort = claudeCode.Effort });
    }

    /// <summary>Escolhe o modelo e o effort usados nas análises pelo plano mensal.</summary>
    [HttpPost("model")]
    public async Task<IActionResult> SelectModel(ModelRequest request, CancellationToken cancellationToken)
    {
        var (catalog, status) = await ModelSourceAsync(false, cancellationToken);
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

    /// <summary>Contas cadastradas (na ordem), o estado de cada uma e a conta que a próxima análise usaria.</summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status([FromQuery] bool refresh, CancellationToken cancellationToken) =>
        Ok(await DescribeAsync(refresh, cancellationToken));

    /// <summary>Inicia o login da conta e devolve a URL a ser aberta no navegador.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken) =>
        Ok(new { loginUrl = await accounts.StartLoginAsync(request.Account, request.Email, cancellationToken) });

    /// <summary>Envia o código exibido pelo Claude após o login.</summary>
    [HttpPost("code")]
    public async Task<IActionResult> Code(CodeRequest request, CancellationToken cancellationToken)
    {
        await accounts.SubmitCodeAsync(request.Account, request.Code, cancellationToken);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel([FromQuery] string? account)
    {
        await accounts.Get(account).Auth.CancelLoginAsync();
        return NoContent();
    }

    /// <summary>Sai da conta (as outras continuam conectadas).</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromQuery] string? account, CancellationToken cancellationToken)
    {
        await accounts.LogoutAsync(account, cancellationToken);
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Cadastra uma conta no fim da ordem; o login vem em seguida (login/code com o id devolvido).</summary>
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
        return Ok(await DescribeAsync(true, cancellationToken));
    }

    /// <summary>Ao esgotar as contas do provedor escolhido, continuar nas contas do outro (vale para o Claude e o Codex).</summary>
    [HttpPut("settings")]
    public IActionResult Settings(SettingsRequest request)
    {
        settings.ContinueWithOtherProvider = request.ContinueWithOtherProvider;
        return Ok(new { settings.ContinueWithOtherProvider });
    }

    /// <summary>Define de onde vem a IA: auto, subscription (plano mensal) ou api.</summary>
    [HttpPost("mode")]
    public async Task<IActionResult> Mode(ModeRequest request, CancellationToken cancellationToken)
    {
        router.Mode = request.Mode;
        return Ok(await DescribeAsync(false, cancellationToken));
    }

    /// <summary>Catálogo da primeira conta logada (os modelos dependem do plano); sem nenhuma, o da primeira conta.</summary>
    private async Task<(ClaudeModelCatalog Catalog, ClaudeAuthStatus Status)> ModelSourceAsync(bool refresh, CancellationToken cancellationToken)
    {
        foreach (var services in accounts.All)
        {
            var status = await services.Auth.GetStatusAsync(refresh, cancellationToken);
            if (status.LoggedIn) return (services.Catalog, status);
        }
        var first = accounts.Get(null);
        return (first.Catalog, await first.Auth.GetStatusAsync(refresh, cancellationToken));
    }

    private async Task<object> DescribeAsync(bool refresh, CancellationToken cancellationToken)
    {
        var list = await accounts.DescribeAsync(refresh, cancellationToken);
        var first = list.FirstOrDefault(a => a.UsesSubscription) ?? list.FirstOrDefault();
        return new
        {
            // De onde a próxima análise com o Claude vai usar a IA: "subscription", "api" ou null (o Codex tem /api/auth/codex).
            ActiveSource = await router.ClaudeSourceAsync(cancellationToken),
            CliInstalled = list.Count == 0 || list.Any(a => a.CliInstalled),
            LoggedIn = list.Any(a => a.LoggedIn),
            UsesSubscription = list.Any(a => a.UsesSubscription),
            first?.AuthMethod,
            first?.Email,
            SubscriptionType = first?.Plan,
            first?.OrgName,
            LoginPending = list.Any(a => a.LoginPending),
            Accounts = list,
            settings.ContinueWithOtherProvider,
            Model = claudeCode.Model,
            Effort = claudeCode.Effort,
            ApiConfigured = router.ApiConfigured,
            Mode = router.Mode.ToString().ToLowerInvariant(),
        };
    }
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
