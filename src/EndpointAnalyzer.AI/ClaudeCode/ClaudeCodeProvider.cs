using System.Text.Json;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>
/// Usa o Claude Code da máquina (claude -p), logado com a conta do Claude: o consumo sai do plano mensal,
/// não da cobrança por uso da API. A resposta vem em JSON validado pelo mesmo schema do ClaudeProvider.
/// </summary>
public class ClaudeCodeProvider(ClaudeCodeOptions options) : IAiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Effort faz parte do nome: entra na chave do cache e no versionamento da análise.
    public string Model => string.IsNullOrEmpty(options.Effort)
        ? $"{options.Model} (assinatura)"
        : $"{options.Model} (assinatura, effort {options.Effort})";

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, AnalysisSections.All, cancellationToken);

    public async Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, AnalysisSections sections, CancellationToken cancellationToken = default)
    {
        var schemaJson = JsonSerializer.Serialize(AnalysisResultSchema.Create(sections));
        var result = await RunAsync(PromptBuilder.SystemPrompt, schemaJson, PromptBuilder.BuildUserPrompt(context, sections), cancellationToken);
        return ParseResult(result.Output, result.Error);
    }

    public async Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(request.SystemPrompt, JsonSerializer.Serialize(request.Schema), request.UserPrompt, cancellationToken);
        return ParseStructured(result.Output, result.Error);
    }

    private async Task<ProcessResult> RunAsync(string systemPrompt, string schemaJson, string userPrompt, CancellationToken cancellationToken)
    {
        // Diretório neutro: não carrega CLAUDE.md, hooks ou configurações de projeto.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "claude-code");
        Directory.CreateDirectory(workingDirectory);

        var arguments = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--json-schema", schemaJson,
            "--model", options.Model,
            "--system-prompt", systemPrompt,
            "--tools", "",
            "--setting-sources", "",
            "--no-session-persistence",
        };
        if (!string.IsNullOrEmpty(options.Effort)) arguments.AddRange(["--effort", options.Effort]);

        var info = ProcessRunner.StartInfo(options, arguments, workingDirectory);

        return await ProcessRunner.RunAsync(
            info,
            userPrompt,
            TimeSpan.FromMinutes(options.TimeoutMinutes),
            cancellationToken);
    }

    public static EndpointAnalysisResult ParseResult(string output, string error) =>
        ParseStructured(output, error).Deserialize<EndpointAnalysisResult>(JsonOptions)
        ?? throw new AiProviderException("Resposta vazia do Claude Code.");

    /// <summary>Objeto de "structured_output" da saída JSON do `claude -p`.</summary>
    public static JsonElement ParseStructured(string output, string error)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new AiProviderException($"Resposta inesperada do Claude Code: {ProcessRunner.StripAnsi(detail).Trim()}");
        }

        using (document)
        {
            var root = document.RootElement;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            if (isError)
            {
                var message = root.TryGetProperty("result", out var r) ? r.ToString() : "erro desconhecido";
                throw new AiProviderException($"Claude Code: {message}");
            }

            if (!root.TryGetProperty("structured_output", out var structured) || structured.ValueKind != JsonValueKind.Object)
                throw new AiProviderException("O Claude Code não devolveu a saída estruturada.");

            return structured.Clone();
        }
    }
}
