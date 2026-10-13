using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Core.Interfaces;

namespace EndpointAnalyzer.Application;

/// <summary>Uma conta do Claude e os serviços do login dela (status, uso do plano e modelos são separados por conta).</summary>
public sealed class ClaudeAccountServices(AiAccount account, ClaudeCodeOptions options, ClaudeCodeAuth auth, ClaudeCodeUsage usage,
    ClaudeModelCatalog catalog) : AiAccountServices(account, AiProviders.Claude)
{
    public ClaudeCodeAuth Auth { get; } = auth;

    public ClaudeCodeUsage Usage { get; } = usage;

    public ClaudeModelCatalog Catalog { get; } = catalog;

    /// <summary>Provedor com o login desta conta e o modelo/effort escolhidos agora (a escolha vale para todas as contas).</summary>
    public ClaudeCodeProvider CreateProvider() => new(options.WithConfigDirectory(Account.Directory));
}

/// <summary>
/// Contas do Claude (plano mensal), na ordem de uso: cada uma com o próprio CLAUDE_CONFIG_DIR. A primeira reaproveita o
/// login de ClaudeCode:ConfigDirectory; a lista fica em ClaudeCode:AccountsDirectory.
/// </summary>
public sealed class ClaudeAccounts : AiAccountRegistry<ClaudeAccountServices>
{
    private readonly ClaudeCodeOptions _options;
    private readonly ClaudeCodeAuth? _primaryAuth;
    private readonly ClaudeCodeUsage? _primaryUsage;
    private readonly ClaudeModelCatalog? _primaryCatalog;

    /// <param name="auth">Login do diretório padrão (ClaudeCode:ConfigDirectory), compartilhado com quem já o usa.</param>
    public ClaudeAccounts(ClaudeCodeOptions options, AiAvailability availability, ClaudeCodeAuth? auth = null, ClaudeCodeUsage? usage = null,
        ClaudeModelCatalog? catalog = null)
        : base(AiProviders.Claude, options.AccountsDirectory, options.ConfigDirectory, availability)
    {
        _options = options;
        _primaryAuth = auth;
        _primaryUsage = usage;
        _primaryCatalog = catalog;
    }

    public static string KeyOf(string accountId) => $"{AiProviders.Claude}:{accountId}";

    protected override ClaudeAccountServices Create(AiAccount account, bool primaryDirectory)
    {
        // O diretório padrão reaproveita os serviços já registrados (mesmo login em andamento, mesmos caches).
        var options = _options.WithConfigDirectory(account.Directory);
        return new ClaudeAccountServices(account, _options,
            primaryDirectory && _primaryAuth is not null ? _primaryAuth : new ClaudeCodeAuth(options),
            primaryDirectory && _primaryUsage is not null ? _primaryUsage : new ClaudeCodeUsage(options),
            primaryDirectory && _primaryCatalog is not null ? _primaryCatalog : new ClaudeModelCatalog(options));
    }

    protected override Task LogoutAsync(ClaudeAccountServices services, CancellationToken cancellationToken) =>
        services.Auth.LogoutAsync(cancellationToken);

    public Task<string> StartLoginAsync(string? id, string? email, CancellationToken cancellationToken = default) =>
        Get(id).Auth.StartLoginAsync(email, cancellationToken);

    /// <summary>Conclui o login; a conta volta a ser tentada (o bloqueio por sessão expirada sai).</summary>
    public async Task<ClaudeAuthStatus> SubmitCodeAsync(string? id, string code, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        var status = await services.Auth.SubmitCodeAsync(code, cancellationToken);
        Availability.Clear(services.Key);
        return status;
    }

    public async Task<ClaudeAuthStatus> LogoutAsync(string? id, CancellationToken cancellationToken = default)
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
        var status = await services.Auth.GetStatusAsync(refresh: true, cancellationToken);
        if (status.UsesSubscription) await UsageAsync(id, refresh: true, cancellationToken);
    }

    /// <summary>Uso do plano da conta (limite atingido bloqueia a conta até a renovação).</summary>
    public async Task<AiUsage?> UsageAsync(string? id, bool refresh, CancellationToken cancellationToken = default)
    {
        var services = Get(id);
        var status = await services.Auth.GetStatusAsync(cancellationToken: cancellationToken);
        var usage = await services.Usage.GetAsync(status, refresh, cancellationToken);
        ApplyUsage(services, usage, fresh: refresh);
        return usage;
    }

    /// <summary>Login e disponibilidade de cada conta, na ordem (consultas em paralelo).</summary>
    public async Task<IReadOnlyList<AiAccountState>> DescribeAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        var all = All;
        var statuses = await Task.WhenAll(all.Select(s => s.Auth.GetStatusAsync(refresh, cancellationToken)));
        return all.Select((services, i) =>
        {
            var status = statuses[i];
            var block = Availability.Blocked(services.Key);
            return new AiAccountState(services.Account.Id, services.Account.Label, services.Display(status.Email, i + 1), i + 1,
                status.CliInstalled, status.LoggedIn, status.UsesSubscription, status.Email, status.SubscriptionType, status.OrgName,
                status.AuthMethod, services.Auth.LoginPending, StateOf(services, status.LoggedIn, status.UsesSubscription),
                block?.Message, block?.Until);
        }).ToList();
    }

    /// <summary>Contas logadas com o plano mensal, na ordem de uso (as bloqueadas inclusive: quem escolhe decide pular).</summary>
    public async Task<IReadOnlyList<(ClaudeAccountServices Services, ClaudeAuthStatus Status)>> SubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var all = All;
        var statuses = await Task.WhenAll(all.Select(s => s.Auth.GetStatusAsync(cancellationToken: cancellationToken)));
        return all.Select((s, i) => (s, statuses[i])).Where(x => x.Item2.UsesSubscription).ToList();
    }
}
