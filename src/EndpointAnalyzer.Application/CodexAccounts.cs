using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.Core.Interfaces;

namespace EndpointAnalyzer.Application;

/// <summary>Uma conta do Codex e os serviços do login dela (status com o uso do plano e modelos são separados por conta).</summary>
public sealed class CodexAccountServices(AiAccount account, CodexOptions options, CodexAuth auth, CodexModelCatalog catalog)
    : AiAccountServices(account, AiProviders.Codex)
{
    public CodexAuth Auth { get; } = auth;

    public CodexModelCatalog Catalog { get; } = catalog;

    /// <summary>Provedor com o login desta conta e o modelo/effort escolhidos agora (a escolha vale para todas as contas).</summary>
    public CodexProvider CreateProvider() => new(options.WithHomeDirectory(Account.Directory));
}

/// <summary>
/// Contas do Codex (plano do ChatGPT), na ordem de uso: cada uma com o próprio CODEX_HOME. A primeira reaproveita o login
/// de Codex:HomeDirectory; a lista fica em Codex:AccountsDirectory.
/// </summary>
public sealed class CodexAccounts : AiAccountRegistry<CodexAccountServices>
{
    private readonly CodexOptions _options;
    private readonly CodexAuth? _primaryAuth;
    private readonly CodexModelCatalog? _primaryCatalog;

    /// <param name="auth">Login do diretório padrão (Codex:HomeDirectory), compartilhado com quem já o usa.</param>
    public CodexAccounts(CodexOptions options, AiAvailability availability, CodexAuth? auth = null, CodexModelCatalog? catalog = null)
        : base(AiProviders.Codex, options.AccountsDirectory, options.HomeDirectory, availability)
    {
        _options = options;
        _primaryAuth = auth;
        _primaryCatalog = catalog;
    }

    public static string KeyOf(string accountId) => $"{AiProviders.Codex}:{accountId}";

    protected override CodexAccountServices Create(AiAccount account, bool primaryDirectory)
    {
        var options = _options.WithHomeDirectory(account.Directory);
        return new CodexAccountServices(account, _options,
            primaryDirectory && _primaryAuth is not null ? _primaryAuth : new CodexAuth(options),
            primaryDirectory && _primaryCatalog is not null ? _primaryCatalog : new CodexModelCatalog(options));
    }

    protected override Task LogoutAsync(CodexAccountServices services, CancellationToken cancellationToken) =>
        services.Auth.LogoutAsync(cancellationToken);

    /// <summary>
    /// Inicia o login da conta. O Codex recebe o retorno do navegador numa porta local fixa, então um login pendente de outra
    /// conta é cancelado antes.
    /// </summary>
    public async Task<string> StartLoginAsync(string? id, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        foreach (var other in All.Where(o => o != services && o.Auth.LoginPending)) await other.Auth.CancelLoginAsync();
        // Sem login concluído a conta não é usada; concluído, ela não fica presa ao bloqueio da sessão expirada.
        if (Availability.Blocked(services.Key)?.Reason == AiUnavailableReason.Authentication) Availability.Clear(services.Key);
        return await services.Auth.StartLoginAsync(cancellationToken);
    }

    public Task CancelLoginAsync(string? id) => Get(id).Auth.CancelLoginAsync();

    public async Task<CodexAuthStatus> LogoutAsync(string? id, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        var status = await services.Auth.LogoutAsync(cancellationToken);
        Availability.Clear(services.Key);
        return status;
    }

    /// <summary>"Verificar novamente": esquece o bloqueio e relê login e uso do plano.</summary>
    public async Task CheckAsync(string id, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        Availability.Clear(services.Key);
        ApplyUsage(services, (await services.Auth.GetStatusAsync(refresh: true, cancellationToken)).Usage, fresh: true);
    }

    /// <summary>Login, uso do plano e disponibilidade de cada conta, na ordem (consultas em paralelo).</summary>
    public async Task<IReadOnlyList<AiAccountState>> DescribeAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        var all = All;
        var statuses = await Task.WhenAll(all.Select(s => s.Auth.GetStatusAsync(refresh, cancellationToken)));
        return all.Select((services, i) =>
        {
            var status = statuses[i];
            ApplyUsage(services, status.Usage, fresh: refresh);
            var block = Availability.Blocked(services.Key);
            // Login com chave de API (cobrança por uso) também é usado, como antes: o estado não é "console".
            return new AiAccountState(services.Account.Id, services.Account.Label, services.Display(status.Email, i + 1), i + 1,
                status.CliInstalled, status.LoggedIn, status.UsesSubscription, status.Email, status.PlanType, null, status.AuthMethod,
                services.Auth.LoginPending, StateOf(services, status.LoggedIn, usesSubscription: status.LoggedIn), block?.Message, block?.Until,
                status.Usage, status.LoginError);
        }).ToList();
    }

    /// <summary>Contas logadas, na ordem de uso (as bloqueadas inclusive: quem escolhe decide pular).</summary>
    public async Task<IReadOnlyList<(CodexAccountServices Services, CodexAuthStatus Status)>> LoggedInAsync(CancellationToken cancellationToken = default)
    {
        var all = All;
        var statuses = await Task.WhenAll(all.Select(s => s.Auth.GetStatusAsync(cancellationToken: cancellationToken)));
        var logged = all.Select((s, i) => (s, statuses[i])).Where(x => x.Item2.LoggedIn).ToList();
        foreach (var (services, status) in logged) ApplyUsage(services, status.Usage, fresh: false);
        return logged;
    }

    /// <summary>
    /// Sem modelo escolhido, o padrão da conta (o primeiro login da ordem informa os modelos); a escolha vale para todas as
    /// contas. Sem o catálogo, o Codex decide o modelo.
    /// </summary>
    public async Task ResolveSelectionAsync(CodexAccountServices services, CodexAuthStatus status, CancellationToken cancellationToken = default)
    {
        try { await services.Catalog.ResolveSelectionAsync(status, _options, cancellationToken); }
        catch (Exception e) when (e is InvalidOperationException or CodexAppServerException or TimeoutException) { }
    }
}
