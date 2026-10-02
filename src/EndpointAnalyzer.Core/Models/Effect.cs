using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>Tipos de sink: operações observáveis do fluxo.</summary>
public static class SinkKinds
{
    public const string Throw = "THROW";
    public const string Return = "RETURN";
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string SaveChanges = "SAVE_CHANGES";
    public const string Publish = "PUBLISH";
    public const string Send = "SEND";
    public const string Http = "HTTP_REQUEST";
    public const string FileWrite = "FILE_WRITE";
}

public static class EffectClasses
{
    public const string Primary = "PRIMARY";
    public const string Secondary = "SECONDARY";
    public const string Infrastructure = "INFRASTRUCTURE";
}

public static class EventKinds
{
    public const string BusinessEvent = "BUSINESS_EVENT";
    public const string DomainError = "DOMAIN_ERROR";
    public const string InfraEvent = "INFRA_EVENT";
}

/// <summary>
/// Efeito observável do endpoint (sink): exceção, retorno HTTP, alteração de entidade, persistência, evento, HTTP, arquivo.
/// </summary>
public class Effect
{
    public string Kind { get; set; } = "";

    /// <summary>PRIMARY, SECONDARY ou INFRASTRUCTURE.</summary>
    public string Class { get; set; } = EffectClasses.Primary;

    /// <summary>Entidade, exceção, evento ou destino do efeito.</summary>
    public string Target { get; set; } = "";

    /// <summary>Para eventos: BUSINESS_EVENT, DOMAIN_ERROR ou INFRA_EVENT.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventKind { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    public string Description { get; set; } = "";

    [JsonIgnore]
    public string? Condition { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }

    public SourceReference? Source { get; set; }
}

/// <summary>Tamanho dos grafos: o de negócio deve ser bem menor que o técnico.</summary>
public class AnalysisStats
{
    public int TechnicalNodes { get; set; }

    public int BusinessNodes { get; set; }

    public int PrunedBranches { get; set; }

    public int AmbiguousCalls { get; set; }

    public int CollapsedInfrastructure { get; set; }

    public int HiddenHelpers { get; set; }

    public int TechnicalConditions { get; set; }

    public int BusinessConditions { get; set; }
}
