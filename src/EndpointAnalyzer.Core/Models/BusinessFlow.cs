using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

// ---- Fase 1: modelo intermediário do call graph (independente da interface e do Mermaid) ----

/// <summary>Classificação determinística de um nó técnico, antes da IA (distingue regra real de infraestrutura).</summary>
public static class TechnicalNodeKinds
{
    public const string Controller = "Controller";
    public const string Application = "Application";
    public const string BusinessRule = "BusinessRule";
    public const string Validation = "Validation";
    public const string Repository = "Repository";
    public const string Persistence = "Persistence";
    public const string Query = "Query";
    public const string Integration = "Integration";
    public const string Mapping = "Mapping";
    public const string Helper = "Helper";
    public const string Infrastructure = "Infrastructure";
    public const string Unknown = "Unknown";

    /// <summary>
    /// Método de tipo genérico reaproveitado por várias entidades (ServiceBase&lt;TEntity&gt;.ObterTodos, Repository&lt;T&gt;.ObterPorId).
    /// O nó representa o uso pela entidade no ponto de chamada; o que a abstração executa por dentro fica em Hides.
    /// </summary>
    public const string Abstraction = "Abstraction";
}

/// <summary>Call graph técnico do endpoint em nós (N1, N2...) e arestas.</summary>
public class CallGraphResult
{
    public string Endpoint { get; set; } = "";

    public List<CallGraphNode> Nodes { get; set; } = [];

    public List<CallGraphEdge> Edges { get; set; } = [];
}

public class CallGraphNode
{
    /// <summary>N1, N2... (N1 é a action do endpoint).</summary>
    public string Id { get; set; } = "";

    /// <summary>"UnidadeController.AdicionarUnidadeV1".</summary>
    public string Method { get; set; } = "";

    /// <summary>Um de <see cref="TechnicalNodeKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>"UnidadeController.cs:237".</summary>
    public string Source { get; set; } = "";

    /// <summary>Caminho completo (para abrir o código); fora do prompt da IA, que usa <see cref="Source"/>.</summary>
    public string File { get; set; } = "";

    public int Line { get; set; }

    /// <summary>Comentário XML do método.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Resumo da infraestrutura colapsada pela análise estática (ex.: "Retorna erro de validação").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>Métodos auxiliares ocultos que ainda podem mudar a interpretação (ex.: SanitizarCnpj).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Helpers { get; set; }

    /// <summary>Faz parte do grafo de negócio da análise estática (o restante é técnico).</summary>
    public bool Relevant { get; set; }

    // ---- Só em abstrações (Kind = Abstraction): o uso pela entidade, não a implementação genérica ----

    /// <summary>Entidade em que a abstração opera aqui (ex.: Hierarquia).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; set; }

    /// <summary>Abstração chamada: "ServiceBase&lt;TEntity, TKey&gt;.ObterTodos".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Via { get; set; }

    /// <summary>Método que usa a abstração (o ponto de chamada é Source/File/Line).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caller { get; set; }

    /// <summary>Instrução do chamador que usa a abstração: "_hierarquiaService.ObterTodos().FirstOrDefault(p =&gt; p.Id == ...)".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Usage { get; set; }

    /// <summary>Chamadas internas da abstração, escondidas do fluxo (ex.: Repository&lt;T&gt;.ObterTodos).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Hides { get; set; }
}

public class CallGraphEdge
{
    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>Condição local para a chamada acontecer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }
}

// ---- Fase 2: evidências extraídas do call graph ----

/// <summary>Tudo que a IA pode citar: cada passo do fluxo de negócio referencia ids daqui (ou nós N*).</summary>
public class EvidenceCollection
{
    public List<ConditionEvidence> Conditions { get; set; } = [];

    public List<ExceptionEvidence> Exceptions { get; set; } = [];

    public List<PersistenceEvidence> Persistence { get; set; } = [];

    public List<QueryEvidence> Queries { get; set; } = [];

    public List<ExternalCallEvidence> ExternalCalls { get; set; } = [];
}

/// <summary>Campos comuns: id citável e origem no código.</summary>
public abstract class EvidenceBase
{
    public string Id { get; set; } = "";

    public string Method { get; set; } = "";

    /// <summary>"UnidadeWebServiceApplicationService.cs:154" (dentro de abstração: o ponto onde a entidade a usa).</summary>
    public string Source { get; set; } = "";

    /// <summary>Abstração genérica em que o código está de fato (ex.: "Repository&lt;T&gt;.Adicionar"); Source aponta para o uso.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Via { get; set; }

    /// <summary>Caminho completo (para abrir o código); fora do prompt da IA, que usa <see cref="Source"/>.</summary>
    public string File { get; set; } = "";

    public int Line { get; set; }
}

/// <summary>C*: condição do registro (if, guarda, switch...).</summary>
public class ConditionEvidence : EvidenceBase
{
    public string Expression { get; set; } = "";
}

/// <summary>E*: exceção lançada e a condição que a dispara.</summary>
public class ExceptionEvidence : EvidenceBase
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }

    public string Type { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatus { get; set; }
}

/// <summary>P*: INSERT/UPDATE/DELETE de entidade ou SAVE_CHANGES.</summary>
public class PersistenceEvidence : EvidenceBase
{
    public string Type { get; set; } = "";

    public string Entity { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }
}

/// <summary>Q*: consulta (acesso a dados que não grava).</summary>
public class QueryEvidence : EvidenceBase
{
    public string Type { get; set; } = "QUERY";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; set; }

    /// <summary>Comentário XML do método, quando houver.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Purpose { get; set; }

    /// <summary>Nó do call graph que faz a consulta.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Consulta por abstração: a instrução do chamador que a usa (filtro, FirstOrDefault...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Usage { get; set; }
}

/// <summary>X*: integração externa (HTTP, evento publicado/enviado, arquivo).</summary>
public class ExternalCallEvidence : EvidenceBase
{
    /// <summary>HTTP_REQUEST, PUBLISH, SEND, FILE_WRITE ou EXTERNAL (nó classificado como efeito externo).</summary>
    public string Type { get; set; } = "";

    /// <summary>Destino: serviço, evento ou arquivo.</summary>
    public string Service { get; set; } = "";
}

// ---- Fase 4: resposta estruturada da IA ----

/// <summary>Tipos de passo do fluxo de negócio (valores do JSON, em camelCase).</summary>
public static class BusinessFlowStepTypes
{
    public const string Entry = "entry";
    public const string Validation = "validation";
    public const string BusinessRule = "businessRule";
    public const string Query = "query";
    public const string Transformation = "transformation";
    public const string Persistence = "persistence";
    public const string ExternalIntegration = "externalIntegration";
    public const string Error = "error";
    public const string Result = "result";

    public static readonly string[] All = [Entry, Validation, BusinessRule, Query, Transformation, Persistence, ExternalIntegration, Error, Result];
}

public class BusinessFlowResult
{
    public List<BusinessFlowStep> Steps { get; set; } = [];

    public List<CollapsedTechnicalNode> CollapsedNodes { get; set; } = [];

    public List<UncertainStep> UncertainSteps { get; set; } = [];
}

public class BusinessFlowStep
{
    /// <summary>B1, B2...</summary>
    public string Id { get; set; } = "";

    /// <summary>Um de <see cref="BusinessFlowStepTypes"/>.</summary>
    public string Type { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Ids das evidências (N*, C*, E*, P*, Q*, X*) que sustentam o passo.</summary>
    public List<string> EvidenceIds { get; set; } = [];

    /// <summary>Passo seguinte sem decisão; vazio quando o passo decide (branches) ou encerra o fluxo.</summary>
    public string Next { get; set; } = "";

    public List<BusinessFlowBranch> Branches { get; set; } = [];

    /// <summary>Preenchido na validação: o passo não cumpre alguma regra (ex.: persistência sem evidência de persistência).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Inconsistencies { get; set; }

    /// <summary>Preenchido na validação: de onde o passo vem no código (para o clique no fluxograma).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BusinessFlowTrace? Trace { get; set; }
}

public class BusinessFlowBranch
{
    /// <summary>Rótulo da saída em linguagem de negócio: "CNPJ já cadastrado".</summary>
    public string Condition { get; set; } = "";

    /// <summary>Id do passo de destino.</summary>
    public string Target { get; set; } = "";
}

/// <summary>Nós técnicos agrupados num passo (StepId) ou removidos do fluxo de negócio (StepId vazio).</summary>
public class CollapsedTechnicalNode
{
    public string StepId { get; set; } = "";

    public List<string> NodeIds { get; set; } = [];

    public string Reason { get; set; } = "";
}

public class UncertainStep
{
    public string StepId { get; set; } = "";

    public string Reason { get; set; } = "";
}

/// <summary>Rastreabilidade de um passo: origem, condição e local no código, com as evidências citadas.</summary>
public class BusinessFlowTrace
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; set; }
}

// ---- Resultado completo, anexado ao relatório ----

/// <summary>Fluxo de negócio polido pela IA, já validado contra as evidências, com o Mermaid gerado a partir dele.</summary>
public class BusinessFlowAnalysis
{
    /// <summary>Nulo quando a etapa falhou (ver <see cref="Error"/>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BusinessFlowResult? Result { get; set; }

    public CallGraphResult CallGraph { get; set; } = new();

    public EvidenceCollection Evidence { get; set; } = new();

    /// <summary>Problemas encontrados na validação (passos removidos, ids inexistentes, persistência fora do fluxo...).</summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>Fluxogramas gerados do <see cref="Result"/>, um por combinação de opções de exibição.</summary>
    public List<BusinessFlowDiagram> Diagrams { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    public bool FromCache { get; set; }
}

public class BusinessFlowDiagram
{
    public bool ShowTechnicalDetails { get; set; }

    public bool ShowCollapsedNodes { get; set; }

    public string Mermaid { get; set; } = "";
}
