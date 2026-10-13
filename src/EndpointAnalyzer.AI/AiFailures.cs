using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Interfaces;

namespace EndpointAnalyzer.AI;

/// <summary>
/// Traduz as mensagens de erro dos CLIs (Claude Code, Codex) em falhas tipadas de autenticação e de limite de uso e remove
/// credenciais das mensagens antes que cheguem aos logs e aos relatórios.
/// </summary>
public static partial class AiFailures
{
    /// <summary>Falha tipada quando a mensagem indica conta sem autenticação ou limite de uso; senão, <see cref="AiProviderException"/>.</summary>
    public static Exception FromMessage(string provider, string message)
    {
        var clean = Redact(message).Trim();
        var name = AiProviders.DisplayName(provider);
        if (UsageLimitRegex().IsMatch(clean))
            return new AiProviderUnavailableException(provider, AiUnavailableReason.UsageLimit,
                $"Limite de uso do {name} atingido: {clean}");
        if (AuthenticationRegex().IsMatch(clean))
            return new AiProviderUnavailableException(provider, AiUnavailableReason.Authentication,
                $"A conta do {name} não está autenticada ou a sessão expirou: entre novamente com {name} ({clean}).");
        return new AiProviderException($"{name}: {clean}");
    }

    /// <summary>Remove tokens (Bearer, JWT, chaves de API e campos de token em JSON) de uma mensagem.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = BearerRegex().Replace(text, "Bearer ***");
        text = JwtRegex().Replace(text, "***");
        text = ApiKeyRegex().Replace(text, "***");
        return TokenFieldRegex().Replace(text, m => $"{m.Groups["name"].Value}***");
    }

    // Codex: "You've hit your usage limit", status 429, usage_limit_reached. Claude Code: "Claude AI usage limit reached",
    // "You've hit your limit · resets 5pm", "5-hour limit reached".
    [GeneratedRegex(@"usage[ _]limit|hit your (?:usage )?limit|limit reached|rate[ _]limit|too many requests|insufficient_quota|""status""\s*:\s*429|\b429\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UsageLimitRegex();

    // Codex: 401 Unauthorized, refresh token expirado ou revogado. Claude Code: "Not logged in · Please run /login",
    // "Invalid API key", "OAuth token has expired".
    [GeneratedRegex(@"\b401\b|unauthori[sz]ed|not logged in|please run /login|log ?in again|sign ?in again|re-?authenticate|refresh[ _]token|token[ _](?:has )?expired|token_expired|invalid[ _]api[ _]key|authentication[ _](?:required|failed)|invalid_grant",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuthenticationRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex ApiKeyRegex();

    // "access_token": "...", refresh_token=..., id_token: ...
    [GeneratedRegex(@"(?<name>\b(?:access|refresh|id)_token[""']?\s*[:=]\s*[""']?)[^""'\s,;&}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenFieldRegex();
}
