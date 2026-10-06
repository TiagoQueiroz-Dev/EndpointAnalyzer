using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI;

public class ClaudeOptions
{
    /// <summary>Quando vazio, o SDK usa ANTHROPIC_API_KEY (ou o perfil do `ant auth login`).</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | xhigh | max. Análise de código se beneficia de "high".</summary>
    public string Effort { get; set; } = "high";

    public int MaxTokens { get; set; } = 16000;
}

/// <summary>
/// Envia o contexto estruturado para o Claude e recebe o resultado em JSON (structured outputs).
/// </summary>
public class ClaudeProvider(ClaudeOptions options) : IAiProvider
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AnthropicClient _client = string.IsNullOrWhiteSpace(options.ApiKey)
        ? new AnthropicClient()
        : new AnthropicClient { ApiKey = options.ApiKey };

    public string Model => options.Model;

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, AnalysisSections.All, cancellationToken);

    public async Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, AnalysisSections sections, CancellationToken cancellationToken = default)
    {
        var json = await CompleteAsync(PromptBuilder.SystemPrompt, AnalysisResultSchema.Create(sections), PromptBuilder.BuildUserPrompt(context, sections), cancellationToken);
        return JsonSerializer.Deserialize<EndpointAnalysisResult>(json, JsonOptions)
            ?? throw new AiProviderException("Resposta vazia da IA.");
    }

    public async Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default)
    {
        var json = await CompleteAsync(request.SystemPrompt, new Dictionary<string, JsonElement>(request.Schema), request.UserPrompt, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<string> CompleteAsync(string system, Dictionary<string, JsonElement> schema, string user, CancellationToken cancellationToken)
    {
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            // Se o modelo recusar por política, o servidor tenta o fallback padrão na mesma chamada.
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            System = system,
            OutputConfig = new BetaOutputConfig
            {
                Effort = ParseEffort(options.Effort),
                Format = new BetaJsonOutputFormat { Schema = schema },
            },
            Messages = [new() { Role = Role.User, Content = user }],
        }, cancellationToken);

        if (response.StopReason == "refusal")
            throw new AiProviderException($"O modelo recusou a análise: {response.StopDetails?.Explanation}");
        if (response.StopReason == "max_tokens")
            throw new AiProviderException($"Resposta truncada: limite de {options.MaxTokens} tokens atingido. Aumente MaxTokens.");

        return string.Concat(response.Content
            .Select(b => b.TryPickText(out var text) ? text.Text : null)
            .Where(t => t is not null));
    }

    private static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => Effort.High,
    };
}

public class AiProviderException(string message) : Exception(message);
