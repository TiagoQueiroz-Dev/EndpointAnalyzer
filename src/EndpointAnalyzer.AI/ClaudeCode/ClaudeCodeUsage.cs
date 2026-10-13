using System.Text.Json;
using System.Text.Json.Nodes;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>
/// Uso dos limites do plano do Claude (sessão de 5 horas e semana), o mesmo do /usage do Claude Code: get_usage do
/// protocolo de controle, sem prompt ao modelo e sem consumir o limite. Só para login com a conta do claude.ai.
/// </summary>
public class ClaudeCodeUsage
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ClaudeCodeOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task<AiUsage?>> _read;
    private (string Key, DateTimeOffset At, AiUsage? Usage)? _cache;

    public ClaudeCodeUsage(ClaudeCodeOptions options)
        : this(options, TimeProvider.System, cancellationToken => ReadAsync(options, cancellationToken)) { }

    internal ClaudeCodeUsage(ClaudeCodeOptions options, TimeProvider clock, Func<CancellationToken, Task<AiUsage?>> read)
    {
        _options = options;
        _clock = clock;
        _read = read;
    }

    /// <summary>Nulo quando a conta não tem limites de plano (login pela API) ou o Claude Code não os informou.</summary>
    public async Task<AiUsage?> GetAsync(ClaudeAuthStatus account, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!account.UsesSubscription) return null;
        var key = $"{account.Email}|{account.OrgName}|{account.SubscriptionType}|{_options.ConfigDirectory}|{_options.Executable}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache is { } cached && cached.Key == key && _clock.GetUtcNow() - cached.At < CacheDuration)
                return cached.Usage;
            var usage = await _read(cancellationToken);
            _cache = (key, _clock.GetUtcNow(), usage);
            return usage;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<AiUsage?> ReadAsync(ClaudeCodeOptions options, CancellationToken cancellationToken)
    {
        await using var control = ClaudeCodeControl.Start(options);
        await control.RequestAsync("initialize", null, "a sessão", cancellationToken);
        // skip_behaviors: só os limites do plano, sem varrer as transcrições locais.
        var usage = await control.RequestAsync("get_usage", new JsonObject { ["skip_behaviors"] = true }, "o uso do plano", cancellationToken);
        return Parse(usage);
    }

    /// <summary>
    /// rate_limits.five_hour e seven_day (utilization em %, resets_at); limits[] é a mesma informação já agrupada
    /// (session, weekly_all), usada quando os campos principais faltam. A API é experimental: campos ausentes são ignorados.
    /// </summary>
    internal static AiUsage? Parse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || response.TryGetProperty("rate_limits_available", out var available) && available.ValueKind == JsonValueKind.False
            || !response.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return null;

        var session = Window(limits, "five_hour", 300) ?? Grouped(limits, "session", 300);
        var weekly = Window(limits, "seven_day", 10080) ?? Grouped(limits, "weekly_all", 10080);
        if (session is null && weekly is null) return null;

        var locked = new[] { "five_hour", "seven_day" }.Any(name => limits.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object
            && w.TryGetProperty("locked_reason", out var reason) && reason.ValueKind == JsonValueKind.String);
        var reached = locked || session?.UsedPercent >= 100 || weekly?.UsedPercent >= 100;
        return new AiUsage(session, weekly, reached);
    }

    private static AiUsageWindow? Window(JsonElement limits, string name, long minutes) =>
        limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
            && window.TryGetProperty("utilization", out var utilization) && utilization.TryGetDouble(out var percent)
            ? new AiUsageWindow((int)Math.Round(percent), minutes, Date(window, "resets_at"))
            : null;

    private static AiUsageWindow? Grouped(JsonElement limits, string kind, long minutes)
    {
        if (!limits.TryGetProperty("limits", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in list.EnumerateArray())
            if (ClaudeCodeControl.Text(item, "kind") == kind && item.TryGetProperty("percent", out var p) && p.TryGetDouble(out var percent))
                return new AiUsageWindow((int)Math.Round(percent), minutes, Date(item, "resets_at"));
        return null;
    }

    private static DateTimeOffset? Date(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ClaudeCodeControl.Text(element, property), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;
}
