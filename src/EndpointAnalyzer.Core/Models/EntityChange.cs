using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

public static class EntityOperations
{
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
}

/// <summary>
/// Entidade criada, alterada ou removida pelo fluxo do endpoint.
/// </summary>
public class EntityChange
{
    public string Entity { get; set; } = "";

    public string Operation { get; set; } = "";

    /// <summary>PRIMARY (efeito do endpoint) ou INFRASTRUCTURE (ex.: histórico gravado pelo barramento).</summary>
    public string EffectClass { get; set; } = EffectClasses.Primary;

    /// <summary>Nomes das propriedades alteradas (visão resumida).</summary>
    public List<string> Properties { get; set; } = [];

    /// <summary>Detalhe de cada propriedade: valor atribuído e condição.</summary>
    public List<PropertyChange> PropertyChanges { get; set; } = [];

    /// <summary>Condição necessária para a operação acontecer (texto completo).</summary>
    [JsonIgnore]
    public string? Condition { get; set; }

    /// <summary>Referências ao registro de condições.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }

    public SourceReference? Source { get; set; }
}

public class PropertyChange
{
    public string Property { get; set; } = "";

    /// <summary>Expressão atribuída, ex.: "ProgramacaoStatus.Pendente".</summary>
    public string Value { get; set; } = "";

    [JsonIgnore]
    public string? Condition { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }

    public SourceReference? Source { get; set; }
}
