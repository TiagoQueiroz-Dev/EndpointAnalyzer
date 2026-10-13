namespace EndpointAnalyzer.Core.Interfaces;

/// <summary>
/// Escolhe o provedor de IA no momento da análise (ex.: assinatura do Claude, Codex ou chave de API).
/// Retorna null quando nenhum está disponível.
/// </summary>
public interface IAiProviderSelector
{
    Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default);

    /// <param name="provider"><see cref="AiProviders.Claude"/>, <see cref="AiProviders.Codex"/> ou nulo (automático).</param>
    Task<IAiProvider?> SelectAsync(string? provider, CancellationToken cancellationToken = default) => SelectAsync(cancellationToken);
}

/// <summary>Seletor que sempre devolve o mesmo provedor.</summary>
public sealed class SingleAiProviderSelector(IAiProvider provider) : IAiProviderSelector
{
    public Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAiProvider?>(provider);
}
