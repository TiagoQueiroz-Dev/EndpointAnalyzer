using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

public static class ScenarioKinds
{
    /// <summary>Caminho feliz e variações que mudam os efeitos (tabela de decisão).</summary>
    public const string Success = "sucesso";

    /// <summary>Entrada inválida (DataAnnotations, FluentValidation, tipo não anulável): partição inválida ou valor-limite.</summary>
    public const string Validation = "validacao";

    /// <summary>Regra de negócio do código que bloqueia a operação (throw, return de erro, notificação).</summary>
    public const string Rule = "regra";

    /// <summary>Valor-limite válido: a operação ainda deve ser aceita.</summary>
    public const string Boundary = "limite";
}

public static class ScenarioTechniques
{
    public const string DecisionTable = "decision-table";
    public const string EquivalencePartitioning = "equivalence-partitioning";
    public const string BoundaryValue = "boundary-value";
}

/// <summary>
/// Matriz de cenários do endpoint (Fase 4 de tecnologias-fases-geracao-payloads.md): cada cenário tem o payload,
/// o estado/pré-condições necessárias e o resultado esperado. Gerada de forma determinística a partir das regras
/// extraídas pelo Roslyn; o Z3 só entra nas restrições com mais de uma variável ou aritmética.
/// </summary>
public class ScenarioMatrix
{
    /// <summary>Campos que o endpoint recebe (rota, query, header, body) e as restrições de cada um.</summary>
    public List<ScenarioInput> Inputs { get; set; } = [];

    /// <summary>Linhas da tabela de decisão: validações, gatilhos das regras e ramos que mudam os efeitos.</summary>
    public List<DecisionCondition> Conditions { get; set; } = [];

    public List<Scenario> Scenarios { get; set; } = [];

    /// <summary>Limitações da geração (regras não resolvidas, cenários inviáveis...).</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>Enums no JSON como texto (JsonStringEnumConverter) ou como número (padrão do System.Text.Json).</summary>
    public bool EnumsAsStrings { get; set; }
}

public class ScenarioInput
{
    /// <summary>Caminho do campo como no código: "request.Placa", "id".</summary>
    public string Path { get; set; } = "";

    /// <summary>Nome no JSON/rota/query: "placa", "id".</summary>
    public string Name { get; set; } = "";

    /// <summary>route, query, header, form ou body.</summary>
    public string Location { get; set; } = "";

    public string Type { get; set; } = "";

    public bool Nullable { get; set; }

    public bool Required { get; set; }

    /// <summary>Restrições em texto: "[MaxLength(500)]", "RuleFor(x => x.Data).NotEmpty()".</summary>
    public List<string> Constraints { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? EnumValues { get; set; }
}

public class DecisionCondition
{
    /// <summary>D1, D2...</summary>
    public string Id { get; set; } = "";

    /// <summary>validacao, regra ou ramo.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Expressão como no código (para validações, a regra: "request.Observacao: [MaxLength(500)]").</summary>
    public string Expression { get; set; } = "";

    /// <summary>Texto curto para a linha da tabela (mensagem da regra ou a validação).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Id do registro de condições (C1...) quando a mesma condição está lá.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegistryId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }
}

public class Scenario
{
    /// <summary>CEN-01, CEN-02...</summary>
    public string Id { get; set; } = "";

    /// <summary>Um de <see cref="ScenarioKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Um de <see cref="ScenarioTechniques"/>.</summary>
    public string Technique { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Condição que o cenário exercita (regra violada, ramo, limite), para a interface descrever em linguagem natural.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioFocus? Focus { get; set; }

    public ScenarioRequest Request { get; set; } = new();

    public List<ScenarioPrecondition> Preconditions { get; set; } = [];

    public ScenarioExpectation Expected { get; set; } = new();

    /// <summary>Valor de cada condição da tabela de decisão neste cenário (true, false ou null quando não avaliada).</summary>
    public Dictionary<string, bool?> Decisions { get; set; } = [];

    /// <summary>Como as restrições foram resolvidas: "direto" (domínios simples) ou "z3".</summary>
    public string Solver { get; set; } = "direto";

    /// <summary>Partes que não puderam ser determinadas (condições opacas, status inferido...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Notes { get; set; }
}

public class ScenarioFocus
{
    /// <summary>Expressão como no código.</summary>
    public string Expression { get; set; } = "";

    /// <summary>Valor da expressão no cenário.</summary>
    public bool Value { get; set; } = true;

    /// <summary>Campo e valor testados (partição/limite): "observacao com 501 caracteres".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

public class ScenarioRequest
{
    public string Method { get; set; } = "";

    /// <summary>Rota com os valores preenchidos e a query string.</summary>
    public string Url { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Route { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Query { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Headers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Body { get; set; }
}

public class ScenarioPrecondition
{
    /// <summary>estado (banco, serviço consultado), contexto (usuário, configuração) ou suposicao (condição que não foi resolvida).</summary>
    public string Kind { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Expressão do código de onde vem o estado, com os valores do payload: "veiculos.ObterPorId(5)".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expression { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }
}

public class ScenarioExpectation
{
    /// <summary>sucesso ou erro.</summary>
    public string Outcome { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatus { get; set; }

    /// <summary>De onde veio o status: código (return do controller), middleware (mapeamento de exceções), validacao ou inferido.</summary>
    public string StatusSource { get; set; } = "";

    public List<string> Messages { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Exception { get; set; }

    /// <summary>Regra (ou validação) que interrompe a operação.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rule { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }

    /// <summary>Efeitos que acontecem neste cenário (alterações, persistência, eventos).</summary>
    public List<ScenarioEffect> Effects { get; set; } = [];

    /// <summary>Alguma alteração chega ao banco (SaveChanges executado).</summary>
    public bool Persisted { get; set; }
}

public class ScenarioEffect
{
    public string Kind { get; set; } = "";

    public string Target { get; set; } = "";

    public string Description { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Properties { get; set; }

    /// <summary>Depende de condição que não pôde ser avaliada.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Uncertain { get; set; }
}

/// <summary>Título e descrição de um cenário escritos pela IA (Fase 6: só apresentação).</summary>
public class ScenarioLabel
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";
}
