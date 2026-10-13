using System.Collections.Concurrent;
using System.Text.Json;

namespace EndpointAnalyzer.AI.Codex;

/// <summary>Modelo do Codex com os reasoning efforts que ele aceita (mesmo formato do catálogo do Claude na interface).</summary>
public sealed record CodexModelInfo(
    string Id,
    string Name,
    bool Available,
    string? Reason,
    IReadOnlyList<string> Efforts,
    string? DefaultEffort,
    bool IsDefault);

/// <summary>Modelos informados pelo Codex (model/list do app-server) para a conta logada no projeto.</summary>
public class CodexModelCatalog
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<CodexModelInfo> Models)> _cache = new();
    private readonly CodexOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CodexModelInfo>>> _discover;

    public CodexModelCatalog(CodexOptions options)
        : this(options, TimeProvider.System, cancellationToken => DiscoverAsync(options, cancellationToken)) { }

    internal CodexModelCatalog(CodexOptions options, TimeProvider clock, Func<CancellationToken, Task<IReadOnlyList<CodexModelInfo>>> discover)
    {
        _options = options;
        _clock = clock;
        _discover = discover;
    }

    public async Task<IReadOnlyList<CodexModelInfo>> GetAsync(CodexAuthStatus account, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!account.LoggedIn) return [];
        var key = $"{account.Email}|{account.PlanType}|{account.AuthMethod}|{_options.HomeDirectory}|{_options.Executable}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache.TryGetValue(key, out var cached) && _clock.GetUtcNow() - cached.At < CacheDuration)
                return cached.Models;
            var models = await _discover(cancellationToken);
            if (models.Count == 0)
                throw new InvalidOperationException("O Codex não retornou modelos para esta conta. Atualize o Codex e tente novamente.");
            _cache[key] = (_clock.GetUtcNow(), models);
            return models;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sem modelo escolhido, usa o padrão da conta; com effort que o modelo não aceita, o padrão do modelo. Só altera a
    /// seleção em memória (a escolha salva pela interface continua valendo).
    /// </summary>
    public Task ResolveSelectionAsync(CodexAuthStatus account, CancellationToken cancellationToken = default) =>
        ResolveSelectionAsync(account, _options, cancellationToken);

    /// <summary>Resolve com os modelos desta conta, gravando em <paramref name="target"/> (a seleção que vale para todas as contas).</summary>
    public async Task ResolveSelectionAsync(CodexAuthStatus account, CodexOptions target, CancellationToken cancellationToken = default)
    {
        var models = await GetAsync(account, cancellationToken: cancellationToken);
        var model = string.IsNullOrWhiteSpace(target.Model)
            ? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault(m => m.Available)
            : models.FirstOrDefault(m => m.Id == target.Model);
        if (model is null) return;
        target.Model = model.Id;
        if (model.Efforts.Count == 0) target.Effort = "";
        else if (!model.Efforts.Contains(target.Effort)) target.Effort = model.DefaultEffort ?? "";
    }

    private static async Task<IReadOnlyList<CodexModelInfo>> DiscoverAsync(CodexOptions options, CancellationToken cancellationToken)
    {
        await using var server = await CodexAppServer.StartAsync(options, cancellationToken);
        var models = new List<CodexModelInfo>();
        string? cursor = null;
        do
        {
            var page = await server.RequestAsync("model/list", new { cursor, includeHidden = false }, cancellationToken);
            models.AddRange(ParseModels(page));
            cursor = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("nextCursor", out var next)
                && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor) && models.Count < 200);
        return models.DistinctBy(m => m.Id).ToList();
    }

    internal static IEnumerable<CodexModelInfo> ParseModels(JsonElement page)
    {
        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Esta versão do Codex não informou o catálogo de modelos. Atualize o Codex e tente novamente.");
        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            // "model" é o identificador aceito por `codex exec -m`.
            var id = Text(model, "model") ?? Text(model, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var efforts = model.TryGetProperty("supportedReasoningEfforts", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Select(e => Text(e, "reasoningEffort")).OfType<string>().Where(e => e.Length > 0).Distinct().ToArray()
                : [];
            var defaultEffort = Text(model, "defaultReasoningEffort");
            if (!efforts.Contains(defaultEffort)) defaultEffort = null;
            var isDefault = model.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True;
            yield return new CodexModelInfo(id, Text(model, "displayName") ?? id, true, null, efforts, defaultEffort, isDefault);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
