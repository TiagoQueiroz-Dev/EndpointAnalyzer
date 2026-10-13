namespace EndpointAnalyzer.Core.Interfaces;

/// <summary>Identificadores dos provedores de IA (requisições, relatório e interface).</summary>
public static class AiProviders
{
    public const string Claude = "claude";

    public const string Codex = "codex";

    public static string DisplayName(string? provider) => provider switch
    {
        Codex => "Codex",
        Claude => "Claude",
        _ => "IA",
    };
}

public enum AiUnavailableReason
{
    /// <summary>Conta desconectada, sessão expirada ou credencial recusada.</summary>
    Authentication,

    /// <summary>Limite de uso do plano atingido.</summary>
    UsageLimit,
}

/// <summary>
/// O provedor não tem como responder agora (conta sem autenticação ou limite de uso atingido): repetir a chamada no mesmo
/// provedor não adianta. A validação em runtime para em vez de seguir sem IA, e a análise pode continuar em outro provedor.
/// </summary>
public class AiProviderUnavailableException(string provider, AiUnavailableReason reason, string message)
    : Exception(message)
{
    public string Provider { get; } = provider;

    public AiUnavailableReason Reason { get; } = reason;
}
