using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>Classificação de cada cenário depois da validação em runtime (MatrixReconciler).</summary>
public static class ScenarioValidationStatuses
{
    /// <summary>Comportamento reproduzido em runtime: status e mensagem (ou campo) conferem com o esperado.</summary>
    public const string Confirmed = "confirmado";

    /// <summary>Comportamento observado em runtime que a análise estática não previu.</summary>
    public const string Discovered = "descoberto";

    /// <summary>O cenário parece válido, mas não foi possível obter o estado (dados) necessário.</summary>
    public const string NotMaterialized = "nao-materializado";

    /// <summary>
    /// Só quando um limite de segurança (requisições, rodadas, execuções por cenário) ou a falta de resposta da IA
    /// interrompeu a exploração: sem limite, todo cenário executado termina confirmado ou inalcançável.
    /// </summary>
    public const string Inconclusive = "inconclusivo";

    /// <summary>Código e runtime demonstram que o cenário não pode ocorrer (sai da matriz, com a evidência).</summary>
    public const string Unreachable = "inalcancavel";

    public static readonly IReadOnlyList<string> All = [Confirmed, Discovered, NotMaterialized, Inconclusive, Unreachable];
}

public static class RuntimeValidationStatuses
{
    public const string Completed = "concluida";

    /// <summary>Concluída, mas algum limite (requests, tentativas, rodadas da IA) foi atingido.</summary>
    public const string Partial = "parcial";

    /// <summary>A API não subiu ou a validação foi interrompida: a matriz exibida é a estática.</summary>
    public const string Failed = "falhou";
}

public static class RuntimePhases
{
    public const string Acquisition = "aquisicao";
    public const string Baseline = "baseline";
    public const string Scenario = "cenario";
    public const string Exploration = "exploracao";
}

/// <summary>
/// Validação dinâmica da matriz (analise-ia-validacao-dinamica-cenarios.md): a API analisada roda de verdade,
/// a IA busca dados reais pelos endpoints de leitura, monta payloads válidos e cada cenário é executado e
/// comparado com o resultado esperado da análise estática. Só existe na análise com IA.
/// </summary>
public class RuntimeValidation
{
    /// <summary>Um de <see cref="RuntimeValidationStatuses"/>.</summary>
    public string Status { get; set; } = RuntimeValidationStatuses.Completed;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>Endereço local em que o serviço foi executado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; set; }

    /// <summary>POST/PUT/PATCH/DELETE permitidos (Runtime:AllowWrites).</summary>
    public bool WritesAllowed { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public long DurationMs { get; set; }

    /// <summary>Matriz final validada (nula quando a validação falhou antes de executar).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValidatedMatrix? Matrix { get; set; }

    /// <summary>Requisitos de payload (P1, P2...) e de cenário (R1, R2...).</summary>
    public List<DataRequirement> Requirements { get; set; } = [];

    /// <summary>Payload base: valor de cada campo do payload, com a origem (dado real, derivado, sintético ou gerador).</summary>
    public List<PayloadFieldValue> Payload { get; set; } = [];

    /// <summary>ScenarioContext: dados reais encontrados e reutilizados entre os cenários.</summary>
    public List<ContextItem> Context { get; set; } = [];

    /// <summary>Todas as requisições feitas à API (aquisição de dados e cenários).</summary>
    public List<RuntimeExecution> Executions { get; set; } = [];

    public RuntimeStats Stats { get; set; } = new();

    /// <summary>Linha do tempo das etapas.</summary>
    public List<string> Log { get; set; } = [];

    /// <summary>Limites atingidos e outras observações.</summary>
    public List<string> Notes { get; set; } = [];
}

public class RuntimeStats
{
    public int Requests { get; set; }

    public int AiCalls { get; set; }

    /// <summary>Cenários da matriz candidata enviados para validação.</summary>
    public int Scenarios { get; set; }
}

public class ValidatedMatrix
{
    /// <summary>Matriz final: cenários estáticos classificados + descobertos em runtime (sem os inalcançáveis).</summary>
    public List<ValidatedScenario> Scenarios { get; set; } = [];

    /// <summary>Cenários retirados da matriz por evidência de que são inalcançáveis.</summary>
    public List<ValidatedScenario> Removed { get; set; } = [];

    /// <summary>Quantidade por status (<see cref="ScenarioValidationStatuses"/>).</summary>
    public Dictionary<string, int> Counts { get; set; } = [];
}

public class ValidatedScenario
{
    /// <summary>CEN-01 (da matriz estática) ou RT-01 (descoberto em runtime).</summary>
    public string Id { get; set; } = "";

    /// <summary>Um de <see cref="ScenarioValidationStatuses"/>.</summary>
    public string Status { get; set; } = "";

    /// <summary>Um de <see cref="ScenarioKinds"/>.</summary>
    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Resultado esperado (recalculado pela simulação com os valores reais do payload).</summary>
    public ScenarioExpectation Expected { get; set; } = new();

    /// <summary>Payload usado na execução que decidiu o status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioRequest? Request { get; set; }

    /// <summary>Estado necessário com os valores reais usados.</summary>
    public List<ScenarioPrecondition> Preconditions { get; set; } = [];

    /// <summary>Valores reais ligados às variáveis do cenário (payload e estado).</summary>
    public List<RuntimeBinding> Bindings { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservedResult? Observed { get; set; }

    /// <summary>Execução (EX-..) que decidiu o status.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; set; }

    public int Attempts { get; set; }

    /// <summary>O que foi verificado: status, mensagem, campo, estado real.</summary>
    public List<string> Evidence { get; set; } = [];

    /// <summary>Por que o cenário não foi confirmado.</summary>
    public List<string> Reasons { get; set; } = [];
}

public class RuntimeBinding
{
    /// <summary>Variável do cenário: "request.VeiculoId", "veiculo.Disponivel".</summary>
    public string Variable { get; set; } = "";

    /// <summary>Valor em JSON: 37, "ABC1D23", true, null.</summary>
    public string Value { get; set; } = "";

    /// <summary>ia (escolhido pela IA a partir do contexto), baseline (mantido do payload confirmado) ou fato (estado real já observado).</summary>
    public string Origin { get; set; } = "";

    /// <summary>Item do contexto ou execução de onde veio o valor.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}

public class ObservedResult
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    /// <summary>Exceção informada na resposta (ProblemDetails, página de erro) ou falha de transporte.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Exception { get; set; }

    public long ElapsedMs { get; set; }
}

public static class DataRequirementKinds
{
    /// <summary>Dados para preencher o payload completo com valores reais (todos os cenários partem dele).</summary>
    public const string Payload = "payload";

    /// <summary>Estado específico de que um cenário depende ("hierarquia ativa", "id que não existe").</summary>
    public const string Scenario = "cenario";
}

/// <summary>
/// Dado que precisa existir (ou não existir) para materializar os cenários (ScenarioRequirementPlanner): os campos do
/// payload completo (<see cref="DataRequirementKinds.Payload"/>) ou o estado de um cenário (<see cref="DataRequirementKinds.Scenario"/>).
/// </summary>
public class DataRequirement
{
    /// <summary>P1, P2... (payload) ou R1, R2... (cenário).</summary>
    public string Id { get; set; } = "";

    /// <summary>Um de <see cref="DataRequirementKinds"/>.</summary>
    public string Kind { get; set; } = DataRequirementKinds.Scenario;

    public string Description { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; set; }

    public List<string> Constraints { get; set; } = [];

    public List<string> Scenarios { get; set; } = [];

    /// <summary>Campos do payload preenchidos com o dado.</summary>
    public List<string> Fields { get; set; } = [];

    /// <summary>pendente, atendido ou insatisfeito.</summary>
    public string Status { get; set; } = "pendente";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

/// <summary>Origem do valor de um campo do payload base, na ordem de prioridade.</summary>
public static class PayloadValueOrigins
{
    /// <summary>Valor que aparece numa resposta real da API.</summary>
    public const string Real = "real";

    /// <summary>Derivado de um dado real (ex.: código real com sufixo para não duplicar, maior id + 100000).</summary>
    public const string Derived = "derivado";

    /// <summary>Sintético, mas semanticamente válido (CNPJ com dígito verificador, nome plausível).</summary>
    public const string Synthetic = "sintetico";

    /// <summary>Valor genérico do gerador estático (último recurso).</summary>
    public const string Generator = "gerador";

    public static readonly IReadOnlyList<string> All = [Real, Derived, Synthetic, Generator];
}

/// <summary>Valor de um campo no payload base, reutilizado por todos os cenários que não precisam mudá-lo.</summary>
public class PayloadFieldValue
{
    /// <summary>Variável do payload: "request.Email".</summary>
    public string Field { get; set; } = "";

    /// <summary>Valor em JSON.</summary>
    public string Value { get; set; } = "";

    /// <summary>Um de <see cref="PayloadValueOrigins"/>.</summary>
    public string Origin { get; set; } = "";

    /// <summary>Item do contexto ou execução (EX-..) de onde veio o valor.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>Valor real comprovado no item do contexto ou na execução citada.</summary>
    public bool Verified { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}

/// <summary>Dado real guardado no ScenarioContext.</summary>
public class ContextItem
{
    public string Key { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Valor em JSON.</summary>
    public string Value { get; set; } = "";

    public List<string> Requirements { get; set; } = [];

    /// <summary>Execução (EX-..) cuja resposta contém o dado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; set; }

    /// <summary>Os valores aparecem na resposta da execução citada (não é suposição da IA).</summary>
    public bool Verified { get; set; }

    /// <summary>aquisicao, execucao (payload confirmado) ou exploracao.</summary>
    public string Origin { get; set; } = "";
}

/// <summary>Requisição feita à API em execução (ScenarioExecutor).</summary>
public class RuntimeExecution
{
    /// <summary>EX-01, EX-02...</summary>
    public string Id { get; set; } = "";

    /// <summary>Um de <see cref="RuntimePhases"/>.</summary>
    public string Phase { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScenarioId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Attempt { get; set; }

    public string Method { get; set; } = "";

    public string Url { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Headers { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? RequestBody { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Status { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseBody { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Exception { get; set; }

    public long ElapsedMs { get; set; }

    /// <summary>Não executada (escrita fora do ambiente de análise, rota fora do catálogo, limite de requests).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Blocked { get; set; }

    /// <summary>Motivo da requisição (aquisição) ou do bloqueio.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
}
