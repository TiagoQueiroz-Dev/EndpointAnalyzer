using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// ScenarioContext: dados reais encontrados na API (cache em memória da análise), reutilizados por todos os cenários,
/// e os fatos do estado (consulta + valores concretos) usados pelo materializador. Os fatos valem até a próxima
/// escrita bem-sucedida na API, que pode ter mudado o estado.
/// </summary>
public sealed class ScenarioContext(RuntimeValidation report)
{
    private readonly List<StateFact> _facts = [];

    public IReadOnlyList<ContextItem> Items => report.Context;

    public IReadOnlyCollection<StateFact> Facts => _facts;

    /// <summary>
    /// Guarda o dado (substitui a mesma chave). É verificado quando todos os valores aparecem na resposta (ou no body
    /// enviado) da execução citada: dado que a IA não consegue mostrar de onde veio fica marcado como não verificado.
    /// </summary>
    /// <param name="trusted">Dado montado pelo próprio analisador a partir da execução (ex.: o baseline confirmado).</param>
    public ContextItem Add(string key, string description, string valueJson, IEnumerable<string>? requirements, string? executionId, string origin,
        bool trusted = false)
    {
        var execution = executionId is null ? null : report.Executions.FirstOrDefault(e => e.Id == executionId);
        var item = new ContextItem
        {
            Key = key,
            Description = description,
            Value = valueJson,
            Requirements = requirements?.Distinct().ToList() ?? [],
            ExecutionId = execution?.Id,
            Verified = execution is not null && (trusted || Evidence.Supports(execution, valueJson)),
            Origin = origin,
        };
        report.Context.RemoveAll(i => i.Key == key);
        report.Context.Add(item);
        return item;
    }

    public void AddFacts(IEnumerable<StateFact> facts)
    {
        foreach (var fact in facts)
        {
            _facts.RemoveAll(f => f.Root == fact.Root && f.Member == fact.Member);
            _facts.Add(fact);
        }
    }

    public void InvalidateFacts() => _facts.Clear();

    /// <summary>Itens do contexto para os prompts.</summary>
    public string Describe() => report.Context.Count == 0
        ? "(vazio)"
        : string.Join("\n", report.Context.Select(i =>
            $"- {i.Key}{(i.Verified ? "" : " [NÃO VERIFICADO]")}: {i.Description} = {Truncate(i.Value, 600)}{(i.ExecutionId is null ? "" : $" (fonte {i.ExecutionId})")}"));

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>Textos e valores de uma resposta, para verificar evidências e mensagens.</summary>
internal static class Evidence
{
    /// <summary>Todos os valores escalares (exceto bool) do JSON aparecem na resposta ou no body enviado.</summary>
    public static bool Supports(RuntimeExecution execution, string valueJson)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(valueJson); }
        catch (JsonException) { node = JsonValue.Create(valueJson); }

        var wanted = Scalars(node).Where(s => s.Length > 0).ToList();
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { execution.ResponseBody, execution.RequestBody?.ToJsonString() })
        {
            if (string.IsNullOrEmpty(source)) continue;
            try { foreach (var s in Scalars(JsonNode.Parse(source))) available.Add(s); }
            catch (JsonException) { available.Add(source.Trim()); }
        }
        // Rota/query também contam: GET /api/veiculos/37 mostra que o 37 foi consultado.
        available.Add(execution.Url.Split('?')[0]);
        foreach (var part in execution.Url.Split('/', '?', '&', '='))
            if (part.Length > 0) available.Add(Uri.UnescapeDataString(part));

        if (wanted.Count == 0) return execution.Status is >= 200 and < 300;
        return wanted.All(w => available.Contains(w) || available.Any(a => a.Contains(w, StringComparison.OrdinalIgnoreCase) && w.Length >= 4));
    }

    /// <summary>Folhas escalares do JSON como texto (números normalizados), sem bool e null.</summary>
    public static IEnumerable<string> Scalars(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                    foreach (var s in Scalars(value)) yield return s;
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var s in Scalars(item)) yield return s;
                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        yield return value.GetValue<string>();
                        break;
                    case JsonValueKind.Number:
                        yield return decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                            ? n.ToString(CultureInfo.InvariantCulture)
                            : value.ToJsonString();
                        break;
                }
                break;
        }
    }

    /// <summary>Textos da resposta (strings do JSON e o corpo bruto), normalizados para comparação.</summary>
    public static List<string> Texts(string? body)
    {
        var texts = new List<string>();
        if (string.IsNullOrEmpty(body)) return texts;
        texts.Add(Normalize(body));
        try
        {
            foreach (var s in Strings(JsonNode.Parse(body))) texts.Add(Normalize(s));
        }
        catch (JsonException)
        {
        }
        return texts;
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => Strings(p.Value)),
        JsonArray array => array.SelectMany(Strings),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => [v.GetValue<string>()],
        _ => [],
    };

    public static bool Contains(IReadOnlyList<string> texts, string message)
    {
        var wanted = Normalize(message).TrimEnd('.');
        return wanted.Length > 0 && texts.Any(t => t.Contains(wanted, StringComparison.Ordinal));
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Mensagem principal da resposta: erro/mensagem/detail/title ou os erros de validação.</summary>
    public static string? MainMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.GetValue<string>();
            if (node is JsonObject obj)
            {
                if (obj["errors"] is JsonObject errors)
                {
                    var first = errors.SelectMany(e => Strings(e.Value).Select(s => $"{e.Key}: {s}")).FirstOrDefault();
                    if (first is not null) return first;
                }
                foreach (var key in new[] { "erro", "error", "mensagem", "message", "detail", "title", "errorMessage", "msg" })
                {
                    var property = obj.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (property.Value is JsonValue pv && pv.GetValueKind() == JsonValueKind.String) return pv.GetValue<string>();
                    if (property.Value is JsonObject nested && Strings(nested).FirstOrDefault() is { } inner) return inner;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            var text = body.Trim();
            return text.Length > 200 ? text[..200] + "…" : text;
        }
    }

    /// <summary>ProblemDetails de validação com erro no campo (errors.Placa, errors["$.placa"], errors["request.Placa"]).</summary>
    public static bool HasFieldError(string? body, string field)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(field)) return false;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject { } obj || obj["errors"] is not JsonObject errors) return false;
            var wanted = field.Trim().TrimStart('$', '.');
            return errors.Any(e =>
            {
                var key = e.Key.TrimStart('$', '.');
                return string.Equals(key, wanted, StringComparison.OrdinalIgnoreCase)
                    || key.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)
                    || wanted.EndsWith("." + key, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
