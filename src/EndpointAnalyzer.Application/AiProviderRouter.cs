using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.AI.Codex;
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
/// Decide, a cada análise, qual IA roda: o Claude (assinatura via Claude Code ou API, conforme <see cref="Mode"/>) ou o
/// Codex (conta do ChatGPT via Codex CLI). As duas contas são independentes e podem ficar conectadas ao mesmo tempo.
/// </summary>
public class AiProviderRouter(
    ClaudeCodeProvider subscription,
    ClaudeCodeAuth auth,
    ClaudeProvider? api = null,
    CodexProvider? codex = null,
    CodexAuth? codexAuth = null,
    CodexModelCatalog? codexModels = null) : IAiProviderSelector
{
    /// <summary>Origem do Claude: assinatura, API ou automática.</summary>
    public AiMode Mode { get; set; } = AiMode.Auto;

    public bool ApiConfigured => api is not null;

    /// <summary>Sem provedor escolhido: o Claude e, se ele estiver indisponível, o Codex.</summary>
    public Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default) => SelectAsync(null, cancellationToken);

    /// <param name="provider"><see cref="AiProviders.Claude"/>, <see cref="AiProviders.Codex"/> ou nulo (automático).</param>
    public async Task<IAiProvider?> SelectAsync(string? provider, CancellationToken cancellationToken = default) =>
        provider?.Trim().ToLowerInvariant() switch
        {
            null or "" or "auto" => await SelectClaudeAsync(cancellationToken) ?? await SelectCodexAsync(cancellationToken),
            AiProviders.Claude => await SelectClaudeAsync(cancellationToken),
            AiProviders.Codex => await SelectCodexAsync(cancellationToken),
            _ => throw new ArgumentException($"Provedor de IA desconhecido: '{provider}'. Use '{AiProviders.Claude}' ou '{AiProviders.Codex}'."),
        };

    public async Task<IAiProvider?> SelectClaudeAsync(CancellationToken cancellationToken = default)
    {
        if (Mode != AiMode.Api)
        {
            var status = await auth.GetStatusAsync(cancellationToken: cancellationToken);
            if (status.UsesSubscription) return subscription;
            if (Mode == AiMode.Subscription) return null;
        }

        return api;
    }

    public async Task<IAiProvider?> SelectCodexAsync(CancellationToken cancellationToken = default)
    {
        if (codex is null || codexAuth is null) return null;
        var status = await codexAuth.GetStatusAsync(cancellationToken: cancellationToken);
        if (!status.LoggedIn) return null;

        if (codexModels is not null)
        {
            // Modelo padrão da conta quando nenhum foi escolhido; sem o catálogo, o Codex decide o modelo.
            try { await codexModels.ResolveSelectionAsync(status, cancellationToken); }
            catch (Exception e) when (e is InvalidOperationException or CodexAppServerException or TimeoutException) { }
        }
        return codex;
    }
}
