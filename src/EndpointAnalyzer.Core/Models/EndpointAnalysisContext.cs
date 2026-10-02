namespace EndpointAnalyzer.Core.Models;

/// <summary>
/// Contexto estruturado (resultado da análise estática) enviado para a IA.
/// </summary>
public class EndpointAnalysisContext
{
    public EndpointInfo Endpoint { get; set; } = new();

    /// <summary>Métodos do grafo de negócio, em ordem: "VeiculoTipoService.ExistePorId".</summary>
    public List<string> CallGraph { get; set; } = [];

    /// <summary>Grafo de negócio: validações, regras, consultas relevantes, persistência e efeitos externos.</summary>
    public CallNode? BusinessGraph { get; set; }

    /// <summary>Grafo técnico: tudo que foi resolvido (debug, auditoria, diagnóstico).</summary>
    public CallNode? CallTree { get; set; }

    /// <summary>Registro de condições: "C1" → "ModelState.IsValid".</summary>
    public Dictionary<string, string> ConditionRegistry { get; set; } = [];

    /// <summary>Regras e condições dos métodos do grafo de negócio.</summary>
    public List<ConditionInfo> Conditions { get; set; } = [];

    public List<EntityChange> EntityChanges { get; set; } = [];

    /// <summary>Efeitos observáveis (sinks) do endpoint.</summary>
    public List<Effect> Effects { get; set; } = [];

    /// <summary>Pontos onde SaveChanges/SaveChangesAsync é chamado.</summary>
    public List<SourceReference> PersistencePoints { get; set; } = [];

    /// <summary>Código dos métodos do grafo de negócio (evidência para a IA).</summary>
    public List<CodeSnippet> Methods { get; set; } = [];

    /// <summary>DTOs e enums relevantes ao fluxo.</summary>
    public List<CodeSnippet> Types { get; set; } = [];

    public AnalysisStats Stats { get; set; } = new();

    /// <summary>Matriz de cenários: payload + pré-condições + resultado esperado.</summary>
    public ScenarioMatrix? Scenarios { get; set; }
}

public class CodeSnippet
{
    public string Name { get; set; } = "";

    public string File { get; set; } = "";

    public int Line { get; set; }

    public string Code { get; set; } = "";
}
