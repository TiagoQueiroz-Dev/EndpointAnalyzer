using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
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
/// Decide, a cada análise, se a IA roda pela assinatura do Claude (Claude Code) ou pela API.
/// </summary>
public class AiProviderRouter(ClaudeCodeProvider subscription, ClaudeCodeAuth auth, ClaudeProvider? api = null) : IAiProviderSelector
{
    public AiMode Mode { get; set; } = AiMode.Auto;

    public bool ApiConfigured => api is not null;

    public async Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default)
    {
        if (Mode != AiMode.Api)
        {
            var status = await auth.GetStatusAsync(cancellationToken: cancellationToken);
            if (status.UsesSubscription) return subscription;
            if (Mode == AiMode.Subscription) return null;
        }

        return api;
    }
}
