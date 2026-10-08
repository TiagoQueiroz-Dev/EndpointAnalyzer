using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>Consulta os metadados do protocolo usado pelo Agent SDK, sem enviar prompts ao modelo.</summary>
internal static class ClaudeCodeModelDiscovery
{
    private const string RequestId = "endpoint-analyzer-models";

    public static async Task<IReadOnlyList<ClaudeModelInfo>> GetAsync(ClaudeCodeOptions options,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "claude-code");
        Directory.CreateDirectory(workingDirectory);
        var info = ProcessRunner.StartInfo(options,
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
             "--tools", "", "--setting-sources", "", "--no-session-persistence"], workingDirectory);
        using var process = ProcessRunner.Start(info);
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var request = JsonSerializer.Serialize(new
            {
                type = "control_request", request_id = RequestId, request = new { subtype = "initialize" },
            });
            // O stdin permanece aberto até a resposta initialize: EOF antecipado pode encerrar a sessão.
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (Text(root, "type") != "control_response" || !root.TryGetProperty("response", out var response)
                    || Text(response, "request_id") != RequestId) continue;
                if (Text(response, "subtype") == "error")
                    throw new InvalidOperationException("Não foi possível consultar os modelos do Claude Code: " + Text(response, "error"));
                if (!response.TryGetProperty("response", out var payload)) break;
                return ParseModels(payload);
            }
            throw new InvalidOperationException("O Claude Code encerrou a consulta sem informar os modelos. Atualize o Claude Code e tente novamente.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O Claude Code não informou os modelos em 60 segundos.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("O Claude Code retornou metadados de modelos inválidos. Atualize o Claude Code e tente novamente.", e);
        }
        finally
        {
            ProcessRunner.TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await error;
        }
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
