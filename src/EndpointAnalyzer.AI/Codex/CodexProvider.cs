using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI.Codex;

/// <summary>
/// Usa o Codex CLI da máquina (codex exec), logado com a conta do ChatGPT: o consumo sai do limite do plano do Codex.
/// Recebe o mesmo contexto e o mesmo JSON Schema do Claude (structured outputs), então o resultado entra nos mesmos
/// relatórios. Roda sem ferramentas úteis: diretório vazio, sandbox somente leitura e sessão não gravada em disco.
/// </summary>
public class CodexProvider(CodexOptions options) : IAiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string Provider => AiProviders.Codex;

    // Effort faz parte do nome: entra na chave do cache e no versionamento da análise.
    public string Model
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(options.Model) ? "modelo padrão" : options.Model;
            return string.IsNullOrEmpty(options.Effort) ? $"{model} (Codex)" : $"{model} (Codex, effort {options.Effort})";
        }
    }

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, AnalysisSections.All, cancellationToken);

    public async Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, AnalysisSections sections, CancellationToken cancellationToken = default)
    {
        var json = await RunAsync(PromptBuilder.SystemPrompt, AnalysisResultSchema.Create(sections), PromptBuilder.BuildUserPrompt(context, sections), cancellationToken);
        return json.Deserialize<EndpointAnalysisResult>(JsonOptions) ?? throw new AiProviderException("Resposta vazia do Codex.");
    }

    public Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request.SystemPrompt, request.Schema, request.UserPrompt, cancellationToken);

    private async Task<JsonElement> RunAsync(string systemPrompt, object schema, string userPrompt, CancellationToken cancellationToken)
    {
        var workingDirectory = CodexProcess.WorkingDirectory;
        // O schema vai por arquivo (--output-schema); um por chamada, porque as etapas rodam em paralelo.
        var schemaFile = Path.Combine(workingDirectory, $"schema-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(schemaFile, JsonSerializer.Serialize(schema), cancellationToken);
        try
        {
            var info = CodexProcess.StartInfo(options, Arguments(schemaFile, workingDirectory), workingDirectory);
            var result = await CodexProcess.RunAsync(info, BuildPrompt(systemPrompt, userPrompt),
                TimeSpan.FromMinutes(options.TimeoutMinutes), cancellationToken);
            return ParseOutput(result.Output, result.Error);
        }
        finally
        {
            try { File.Delete(schemaFile); }
            catch (IOException) { }
        }
    }

    internal IReadOnlyList<string> Arguments(string schemaFile, string workingDirectory)
    {
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--output-schema", schemaFile,
            "--sandbox", "read-only",
            "--skip-git-repo-check",
            "--ephemeral",
            "--cd", workingDirectory,
        };
        if (!string.IsNullOrWhiteSpace(options.Model)) arguments.AddRange(["--model", options.Model]);
        // -c usa TOML: o effort vai como string.
        if (!string.IsNullOrEmpty(options.Effort)) arguments.AddRange(["--config", $"model_reasoning_effort=\"{options.Effort}\""]);
        // Prompt pelo stdin: sem limite de tamanho da linha de comando e sem problemas de escape.
        arguments.Add("-");
        return arguments;
    }

    /// <summary>
    /// O `codex exec` não tem system prompt próprio: as instruções vão no início da mensagem, seguidas do contexto do endpoint.
    /// </summary>
    internal static string BuildPrompt(string systemPrompt, string userPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<instrucoes>");
        sb.AppendLine(systemPrompt.Trim());
        sb.AppendLine("</instrucoes>");
        sb.AppendLine();
        sb.AppendLine("Todo o contexto necessário está nesta mensagem: não execute comandos e não leia nem altere arquivos.");
        sb.AppendLine("Responda apenas com o objeto JSON do schema de saída.");
        sb.AppendLine();
        sb.Append(userPrompt);
        return sb.ToString();
    }

    /// <summary>
    /// Eventos JSONL do `codex exec --json`: a resposta é a última mensagem do agente (texto em JSON, validado pelo schema).
    /// Falhas vêm em turn.failed ou error (as de reconexão são transitórias e só a última conta).
    /// </summary>
    public static JsonElement ParseOutput(string output, string error)
    {
        string? message = null, failure = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }
            if (root.ValueKind != JsonValueKind.Object) continue;
            switch (Text(root, "type"))
            {
                case "item.completed" when root.TryGetProperty("item", out var item) && Text(item, "type") == "agent_message":
                    message = Text(item, "text") ?? message;
                    break;
                case "turn.failed" when root.TryGetProperty("error", out var turnError):
                    failure = ErrorMessage(Text(turnError, "message")) ?? failure;
                    break;
                case "error":
                    failure = ErrorMessage(Text(root, "message")) ?? failure;
                    break;
                case "turn.completed":
                    failure = null;
                    break;
            }
        }

        if (failure is not null) throw AiFailures.FromMessage(AiProviders.Codex, failure);
        if (message is null)
        {
            var detail = LastLines(string.IsNullOrWhiteSpace(error) ? output : error);
            throw detail.Length == 0
                ? new AiProviderException("O Codex não devolveu resposta.")
                : AiFailures.FromMessage(AiProviders.Codex, detail);
        }

        try
        {
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new AiProviderException("O Codex não devolveu a saída estruturada.");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AiProviderException("O Codex não devolveu a saída estruturada (a resposta não é JSON).");
        }
    }

    /// <summary>Erros da API chegam como JSON dentro da mensagem: {"status":400,"error":{"message":"..."}}.</summary>
    private static string? ErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('{')) return trimmed;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            var inner = root.TryGetProperty("error", out var e) ? Text(e, "message") ?? Text(e, "type") : Text(root, "message");
            var status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? $" (HTTP {code})" : "";
            return inner is null ? trimmed : inner + status;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static string LastLines(string text) =>
        string.Join(" ", ClaudeCode.ProcessRunner.StripAnsi(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
