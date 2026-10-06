using System.Text.Json;

namespace EndpointAnalyzer.AI;

/// <summary>
/// JSON Schema do EndpointAnalysisResult, usado em structured outputs para a IA nunca responder texto livre.
/// </summary>
public static class AnalysisResultSchema
{
    public static Dictionary<string, JsonElement> Create()
    {
        var evidence = Obj(new()
        {
            ["file"] = Str(),
            ["method"] = Str(),
            ["line"] = new { type = "integer" },
            ["code"] = Str(),
        });

        var rule = Obj(new()
        {
            ["id"] = Str(),
            ["title"] = Str(),
            ["context"] = Str(),
            ["condition"] = Str(),
            ["errorMessage"] = Str(),
            ["confidence"] = new { type = "number" },
            ["evidence"] = evidence,
        });

        var property = Obj(new()
        {
            ["name"] = Str(),
            ["condition"] = Str(),
        });

        var entityChange = Obj(new()
        {
            ["entity"] = Str(),
            ["operation"] = new { type = "string", @enum = new[] { "INSERT", "UPDATE", "DELETE" } },
            ["properties"] = Arr(property),
        });

        var label = Obj(new()
        {
            ["key"] = Str(),
            ["label"] = Str(),
        });

        var flowLabels = Obj(new()
        {
            ["methods"] = Arr(label),
            ["decisions"] = Arr(label),
        });

        var scenario = Obj(new()
        {
            ["id"] = Str(),
            ["title"] = Str(),
            ["description"] = Str(),
        });

        var root = Obj(new()
        {
            ["summary"] = Str(),
            ["businessRules"] = Arr(rule),
            ["validations"] = Arr(rule),
            ["entityChanges"] = Arr(entityChange),
            ["uncertainties"] = Arr(Str()),
            ["flowLabels"] = flowLabels,
            ["scenarios"] = Arr(scenario),
        });

        return JsonSerializer.SerializeToElement(root)
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static object Str() => new { type = "string" };

    private static object Arr(object items) => new { type = "array", items };

    private static Dictionary<string, object> Obj(Dictionary<string, object> properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = properties.Keys.ToArray(),
        ["additionalProperties"] = false,
    };
}
