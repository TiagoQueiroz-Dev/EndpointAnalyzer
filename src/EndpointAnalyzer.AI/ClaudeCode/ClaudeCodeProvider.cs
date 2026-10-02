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

    private static readonly string SchemaJson = JsonSerializer.Serialize(AnalysisResultSchema.Create());

    // Effort faz parte do nome: entra na chave do cache e no versionamento da análise.
    public string Model => string.IsNullOrEmpty(options.Effort)
        ? $"{options.Model} (assinatura)"
        : $"{options.Model} (assinatura, effort {options.Effort})";

    public async Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default)
    {
        // Diretório neutro: não carrega CLAUDE.md, hooks ou configurações de projeto.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "claude-code");
        Directory.CreateDirectory(workingDirectory);

        var arguments = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--json-schema", SchemaJson,
            "--model", options.Model,
            "--system-prompt", PromptBuilder.SystemPrompt,
            "--tools", "",
            "--setting-sources", "",
            "--no-session-persistence",
        };
        if (!string.IsNullOrEmpty(options.Effort)) arguments.AddRange(["--effort", options.Effort]);

        var info = ProcessRunner.StartInfo(options.Executable, arguments, workingDirectory);

        var result = await ProcessRunner.RunAsync(
            info,
            PromptBuilder.BuildUserPrompt(context),
            TimeSpan.FromMinutes(options.TimeoutMinutes),
            cancellationToken);

        return ParseResult(result.Output, result.Error);
    }

    public static EndpointAnalysisResult ParseResult(string output, string error)
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

            return structured.Deserialize<EndpointAnalysisResult>(JsonOptions)
                ?? throw new AiProviderException("Resposta vazia do Claude Code.");
        }
    }
}
