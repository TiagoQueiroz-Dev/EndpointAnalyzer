namespace EndpointAnalyzer.Core.Models;

/// <summary>
/// Resposta estruturada da IA (formato obrigatório, nunca texto livre).
/// </summary>
public class EndpointAnalysisResult
{
    public string Summary { get; set; } = "";

    public List<BusinessRule> BusinessRules { get; set; } = [];

    public List<BusinessRule> Validations { get; set; } = [];

    public List<AiEntityChange> EntityChanges { get; set; } = [];

    /// <summary>Pontos que não puderam ser determinados com certeza.</summary>
    public List<string> Uncertainties { get; set; } = [];

    /// <summary>Textos em linguagem natural para os nós do fluxograma (nulo em resultados antigos).</summary>
    public FlowLabels? FlowLabels { get; set; }

    /// <summary>Título e descrição de cada cenário da matriz (nulo em resultados antigos).</summary>
    public List<ScenarioLabel>? Scenarios { get; set; }
}

/// <summary>Rótulos do fluxograma: objetivo de cada método e pergunta de cada decisão.</summary>
public class FlowLabels
{
    /// <summary>Key = "Tipo.Metodo".</summary>
    public List<FlowLabel> Methods { get; set; } = [];

    /// <summary>Key = expressão da estrutura de controle (ControlStep.Expression).</summary>
    public List<FlowLabel> Decisions { get; set; } = [];
}

public class FlowLabel
{
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";
}

public class BusinessRule
{
    public string Id { get; set; } = "";

    /// <summary>Frase direta do comportamento: "Gerente deve ter no máximo 50 caracteres."</summary>
    public string Title { get; set; } = "";

    /// <summary>Uma frase sobre onde a regra entra no fluxo: "Valida o gerente informado antes da persistência."</summary>
    public string Context { get; set; } = "";

    /// <summary>Condição que dispara a regra, limpa/normalizada: "Unidade.Gerente.Length &gt; 50".</summary>
    public string Condition { get; set; } = "";

    /// <summary>Mensagem de erro definida no código (vazio quando não há).</summary>
    public string ErrorMessage { get; set; } = "";

    /// <summary>Texto livre dos resultados anteriores ao <see cref="Title"/> (a IA não preenche mais).</summary>
    public string Description { get; set; } = "";

    /// <summary>0.00 → 1.00</summary>
    public double Confidence { get; set; }

    public Evidence? Evidence { get; set; }
}

public class Evidence
{
    public string File { get; set; } = "";

    public string Method { get; set; } = "";

    public int Line { get; set; }

    public string Code { get; set; } = "";
}

public class AiEntityChange
{
    public string Entity { get; set; } = "";

    public string Operation { get; set; } = "";

    public List<AiPropertyChange> Properties { get; set; } = [];
}

public class AiPropertyChange
{
    public string Name { get; set; } = "";

    /// <summary>Condição necessária para a alteração, ou vazio quando sempre acontece.</summary>
    public string Condition { get; set; } = "";
}

/// <summary>
/// Resultado completo devolvido pela aplicação: análise estática + interpretação da IA.
/// </summary>
public class EndpointAnalysisReport
{
    public string? AnalysisId { get; set; }

    public DateTimeOffset? SessionExpiresAt { get; set; }

    public EndpointAnalysisContext Context { get; set; } = new();

    /// <summary>Nulo quando a análise foi feita sem IA.</summary>
    public EndpointAnalysisResult? Ai { get; set; }

    public AnalysisVersion Version { get; set; } = new();

    public bool FromCache { get; set; }

    /// <summary>Validação dos cenários com a API em execução (só na análise com IA; nulo quando não rodou).</summary>
    public RuntimeValidation? Runtime { get; set; }

    /// <summary>Fluxo de negócio polido pela IA (aba Negócio com IA); nulo quando não foi pedido.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BusinessFlowAnalysis? BusinessFlow { get; set; }

    /// <summary>Abas produzidas por esta análise (summary, business, complete, scenarios).</summary>
    public List<string> Sections { get; set; } = AnalysisSectionNames.ToNames(AnalysisSections.All);
}

public class AnalysisVersion
{
    public string? Commit { get; set; }

    public string? Branch { get; set; }

    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;

    public string? AiModel { get; set; }

    public string AnalyzerVersion { get; set; } = "";
}
