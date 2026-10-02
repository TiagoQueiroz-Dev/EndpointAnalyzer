using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

public static class ConditionKinds
{
    public const string If = "if";
    public const string Switch = "switch";
    public const string Throw = "throw";
    public const string Guard = "guard";
    public const string Validation = "validation";
}

/// <summary>
/// Regra / condição encontrada no fluxo (if, switch, throw, validação).
/// </summary>
public class ConditionInfo
{
    public string Kind { get; set; } = ConditionKinds.If;

    public string Expression { get; set; } = "";

    /// <summary>O que acontece quando a condição é verdadeira: "throw RegraNegocioException", "return BadRequest", "branch".</summary>
    public string Action { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    public string Method { get; set; } = "";

    public string SourceFile { get; set; } = "";

    public int SourceLine { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
}
