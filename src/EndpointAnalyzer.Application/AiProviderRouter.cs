using EndpointAnalyzer.AI;
using EndpointAnalyzer.Core.Interfaces;

namespace EndpointAnalyzer.Application;

public enum AiMode
{
    /// <summary>Assinatura quando o Claude Code estiver logado com a conta do Claude; senão, API.</summary>
    Auto,

    /// <summary>Sempre o plano mensal (Claude Code logado com claude.ai).</summary>
    Subscription,

    /// <summary>Sempre a API (ANTHROPIC_API_KEY), com cobrança por uso.</summary>
    Api,
}

/// <summary>
/// Decide, a cada análise, a ordem das contas da IA: as contas do Claude (plano mensal, na ordem cadastrada, ou a API
/// conforme <see cref="Mode"/>) e as contas do Codex (plano do ChatGPT, na ordem cadastrada). O provedor escolhido vem
/// primeiro; com <see cref="AiProviderSettings.ContinueWithOtherProvider"/>, as contas do outro entram depois. A análise
/// recebe um <see cref="FailoverAiProvider"/>, que passa para a próxima conta quando a atual atinge o limite de uso.
/// </summary>
public class AiProviderRouter(
    ClaudeAccounts accounts,
    AiAvailability availability,
    AiProviderSettings? settings = null,
    ClaudeProvider? api = null,
    CodexAccounts? codex = null) : IAiProviderSelector
{
    public const string ApiKey = "claude:api";

    /// <summary>Origem do Claude: assinatura, API ou automática.</summary>
    public AiMode Mode { get; set; } = AiMode.Auto;

    public bool ApiConfigured => api is not null;

    /// <summary>Sem provedor escolhido: o Claude e, se ele estiver indisponível, o Codex.</summary>
    public Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default) => SelectAsync(null, cancellationToken);

    /// <param name="provider"><see cref="AiProviders.Claude"/>, <see cref="AiProviders.Codex"/> ou nulo (automático).</param>
    /// <returns>Nulo quando nenhuma conta está conectada.</returns>
    /// <exception cref="AiProviderUnavailableException">Há contas conectadas, mas nenhuma com uso disponível.</exception>
    public async Task<IAiProvider?> SelectAsync(string? provider, CancellationToken cancellationToken = default)
    {
        var candidates = await CandidatesAsync(provider, cancellationToken);
        return candidates.Count == 0 ? null : FailoverAiProvider.Start(candidates, availability);
    }

    /// <summary>Contas conectadas na ordem de uso da análise.</summary>
    public async Task<IReadOnlyList<AiCandidate>> CandidatesAsync(string? provider, CancellationToken cancellationToken = default)
    {
        var chosen = provider?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => null,
            AiProviders.Claude => AiProviders.Claude,
            AiProviders.Codex => AiProviders.Codex,
            _ => throw new ArgumentException($"Provedor de IA desconhecido: '{provider}'. Use '{AiProviders.Claude}' ou '{AiProviders.Codex}'."),
        };
        // Automático: o Claude e depois o Codex, como sempre foi.
        var fallback = chosen is null || (settings?.ContinueWithOtherProvider ?? true);
        var claude = chosen == AiProviders.Codex && !fallback ? [] : await ClaudeCandidatesAsync(cancellationToken);
        var other = chosen == AiProviders.Claude && !fallback ? [] : await CodexCandidatesAsync(cancellationToken);
        return chosen == AiProviders.Codex ? [.. other, .. claude] : [.. claude, .. other];
    }

    private async Task<List<AiCandidate>> ClaudeCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<AiCandidate>();
        if (Mode != AiMode.Api)
            foreach (var (services, status) in await accounts.SubscriptionsAsync(cancellationToken))
                candidates.Add(new AiCandidate(services.Key, AiProviders.Claude, services.Account.Id,
                    services.Display(status.Email, accounts.OrderOf(services)), services.CreateProvider));
        // A API (cobrança por uso) só entra quando escolhida ou sem conta do plano: o limite do plano não vira custo sem aviso.
        if (api is not null && (Mode == AiMode.Api || (Mode == AiMode.Auto && candidates.Count == 0)))
            candidates.Add(new AiCandidate(ApiKey, AiProviders.Claude, null, "API", () => api));
        return candidates;
    }

    private async Task<List<AiCandidate>> CodexCandidatesAsync(CancellationToken cancellationToken)
    {
        if (codex is null) return [];
        var logged = await codex.LoggedInAsync(cancellationToken);
        if (logged.Count == 0) return [];
        await codex.ResolveSelectionAsync(logged[0].Services, logged[0].Status, cancellationToken);
        return logged.Select(x => new AiCandidate(x.Services.Key, AiProviders.Codex, x.Services.Account.Id,
            x.Services.Display(x.Status.Email, codex.OrderOf(x.Services)), x.Services.CreateProvider)).ToList();
    }

    /// <summary>De onde a próxima análise com o Claude usa a IA: "subscription", "api" ou nulo (sem conta).</summary>
    public async Task<string?> ClaudeSourceAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await ClaudeCandidatesAsync(cancellationToken);
        return candidates.Count == 0 ? null : candidates[0].Key == ApiKey ? "api" : "subscription";
    }
}
