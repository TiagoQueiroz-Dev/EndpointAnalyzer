using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>Consulta os metadados do protocolo usado pelo Agent SDK, sem enviar prompts ao modelo.</summary>
internal static class ClaudeCodeModelDiscovery
{
    public static async Task<IReadOnlyList<ClaudeModelInfo>> GetAsync(ClaudeCodeOptions options,
        CancellationToken cancellationToken)
    {
        await using var control = ClaudeCodeControl.Start(options);
        var payload = await control.RequestAsync("initialize", null, "os modelos", cancellationToken);
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("O Claude Code encerrou a consulta sem informar os modelos. Atualize o Claude Code e tente novamente.");
        return ParseModels(payload);
    }

    internal static IReadOnlyList<ClaudeModelInfo> ParseModels(JsonElement payload)
    {
        if (!payload.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Esta versão do Claude Code não informou o catálogo de modelos. Atualize o Claude Code e tente novamente.");
        var result = new Dictionary<string, ClaudeModelInfo>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var value = Text(model, "value");
            // 'default' é uma escolha de roteamento, não um modelo adicional.
            if (value == "default") continue;
            var id = Text(model, "resolvedModel") ?? value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = ModelName(id, Text(model, "displayName"));
            var supportsEffort = model.TryGetProperty("supportsEffort", out var supported)
                && supported.ValueKind == JsonValueKind.True;
            var efforts = supportsEffort && model.TryGetProperty("supportedEffortLevels", out var levels)
                && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToArray()
                : [];
            var defaultEffort = Text(model, "defaultEffort");
            if (!efforts.Contains(defaultEffort)) defaultEffort = null;
            var available = !model.TryGetProperty("available", out var availability)
                || availability.ValueKind != JsonValueKind.False;
            result[id] = new ClaudeModelInfo(id, name, available, Text(model, "reason"), efforts, defaultEffort);
        }
        if (result.Count == 0)
            throw new InvalidOperationException("O Claude Code não retornou modelos para esta conta.");
        return result.Values.ToList();
    }

    private static string ModelName(string id, string? displayName)
    {
        // O identificador resolvido contém a versão; description pode ser apenas texto promocional.
        var match = Regex.Match(id, @"^claude-(?<family>[a-z]+(?:-[a-z]+)*)-(?<version>\d+(?:-\d+)*?)(?:-\d{8})?(?<suffix>\[[^\]]+\])?$",
            RegexOptions.CultureInvariant);
        if (!match.Success) return displayName ?? id;
        var family = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["family"].Value.Replace('-', ' '));
        return $"{family} {match.Groups["version"].Value.Replace('-', '.')}{match.Groups["suffix"].Value}";
    }
    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
