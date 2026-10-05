using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// Chamadas à IA da validação em runtime (via IAiProvider, com structured outputs). Uma falha da IA não interrompe a
/// validação: a etapa segue sem a resposta (ex.: materializa só com os valores do gerador) e o motivo vai para as notas.
/// </summary>
internal sealed class RuntimeAi(IAiProvider provider, RuntimeValidation report, Action<string> log)
{
    public const string SystemPrompt = """
        Você ajuda a validar, com a API em execução, uma matriz de cenários de teste gerada pela análise estática
        (Roslyn) de um endpoint ASP.NET Core. Cada cenário tem payload, estado/pré-condições e resultado esperado.
        O objetivo é uma matriz sem falso positivo: cada cenário precisa ser reproduzido com dados reais.

        Princípios:
        - Primeiro entenda de quais dados cada cenário precisa; depois busque esses dados na própria API pelos
          endpoints do catálogo (prefira GET). Não tente valores aleatórios.
        - Nunca invente ids, códigos ou registros. Use só valores que aparecem em respostas reais registradas
          (cite a execução EX-.. ou o item do contexto) ou que você mesmo criou numa requisição registrada.
        - Não faça força bruta de ids. Para "registro inexistente", derive o valor dos dados reais (ex.: maior id
          listado + 100000) e diga isso na justificativa.
        - POST/PUT/PATCH/DELETE só para preparar dados que não existem, e só quando a escrita estiver permitida.
        - Reutilize o contexto entre os cenários e altere o mínimo possível do payload confirmado (baseline).
        - Use exatamente os nomes de variáveis, ids de cenários, requisitos e execuções recebidos.
        - Valores sempre em JSON: números sem aspas, textos com aspas, true/false, null; objetos de estado:
          {} = existe, null = não existe.
        - Responda apenas com o JSON do schema. Textos em português, curtos e objetivos.
        """;

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<JsonElement?> AskAsync(string purpose, string prompt, Dictionary<string, JsonElement> schema, CancellationToken cancellationToken)
    {
        report.Stats.AiCalls++;
        log($"IA: {purpose}...");
        try
        {
            return await provider.CompleteJsonAsync(new AiJsonRequest(purpose, SystemPrompt, prompt, schema), cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log($"IA falhou em \"{purpose}\": {e.Message}");
            report.Notes.Add($"A IA não respondeu em \"{purpose}\" ({e.Message}); a etapa seguiu sem ela.");
            return null;
        }
    }

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, PromptJson);

    public static string Section(string tag, string content) => $"<{tag}>\n{content}\n</{tag}>\n\n";

    /// <summary>Código dos métodos do grafo de negócio (evidência para a IA), limitado em tamanho.</summary>
    public static string Code(EndpointAnalysisContext context, int maxChars = 24000)
    {
        var sb = new StringBuilder();
        foreach (var snippet in context.Methods.Concat(context.Types))
        {
            var block = $"// {snippet.Name} ({snippet.File}:{snippet.Line})\n{snippet.Code}\n";
            if (sb.Length + block.Length > maxChars) break;
            sb.Append(block);
        }
        return sb.ToString();
    }
}

/// <summary>JSON Schema para structured outputs (todas as propriedades obrigatórias, sem extras).</summary>
internal static class Schema
{
    public static Dictionary<string, JsonElement> Root(Dictionary<string, object> properties) =>
        JsonSerializer.SerializeToElement(Obj(properties)).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    public static object Str() => new { type = "string" };

    public static object Bool() => new { type = "boolean" };

    public static object Arr(object items) => new { type = "array", items };

    public static object Enum(params string[] values) => new { type = "string", @enum = values };

    public static Dictionary<string, object> Obj(Dictionary<string, object> properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = properties.Keys.ToArray(),
        ["additionalProperties"] = false,
    };

    // Respostas de aquisição/exploração compartilham estes formatos.

    public static object Request() => Obj(new()
    {
        ["method"] = Enum("GET", "POST", "PUT", "PATCH", "DELETE"),
        ["url"] = Str(),
        ["bodyJson"] = Str(),
        ["reason"] = Str(),
        ["requirementIds"] = Arr(Str()),
    });

    public static object ContextItem() => Obj(new()
    {
        ["key"] = Str(),
        ["description"] = Str(),
        ["valueJson"] = Str(),
        ["executionId"] = Str(),
        ["requirementIds"] = Arr(Str()),
    });

    public static object Binding() => Obj(new()
    {
        ["variable"] = Str(),
        ["valueJson"] = Str(),
        ["source"] = Str(),
    });
}

internal static class Read
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().ToList() : [];

    public static List<string> Strs(JsonElement e, string name) =>
        Arr(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList();

    public static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
