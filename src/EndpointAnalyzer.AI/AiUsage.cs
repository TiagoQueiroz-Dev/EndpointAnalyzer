namespace EndpointAnalyzer.AI;

/// <summary>Uso de uma janela do limite do plano (ex.: sessão de 5 horas, semanal).</summary>
public sealed record AiUsageWindow(int UsedPercent, long? WindowMinutes, DateTimeOffset? ResetsAt);

/// <summary>Uso do limite do plano da conta (Claude ou Codex), no mesmo formato para a interface.</summary>
/// <param name="Primary">Janela curta (sessão de 5 horas).</param>
/// <param name="Secondary">Janela semanal.</param>
/// <param name="LimitReached">O provedor informou que o limite foi atingido (ou que o uso do plano está bloqueado).</param>
public sealed record AiUsage(AiUsageWindow? Primary, AiUsageWindow? Secondary, bool LimitReached);
