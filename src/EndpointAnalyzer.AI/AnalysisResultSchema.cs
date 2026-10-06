using System.Text.Json;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI;

/// <summary>
/// JSON Schema do EndpointAnalysisResult, usado em structured outputs para a IA nunca responder texto livre.
/// </summary>
public static class AnalysisResultSchema
{
    /// <param name="sections">Só os campos das abas pedidas (padrão: todos).</param>
    public static Dictionary<string, JsonElement> Create(AnalysisSections sections = AnalysisSections.All)
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

        var properties = new Dictionary<string, object>();
        if (sections.HasFlag(AnalysisSections.Summary))
        {
            properties["summary"] = Str();
            properties["businessRules"] = Arr(rule);
            properties["validations"] = Arr(rule);
            properties["entityChanges"] = Arr(entityChange);
            properties["uncertainties"] = Arr(Str());
        }
        if (sections.HasFlag(AnalysisSections.Business)) properties["flowLabels"] = flowLabels;
        if (sections.HasFlag(AnalysisSections.Scenarios)) properties["scenarios"] = Arr(scenario);

        var root = Obj(properties);

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
