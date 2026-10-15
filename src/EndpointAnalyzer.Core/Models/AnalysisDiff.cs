using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>Classificação de cada item comparado entre duas versões da análise.</summary>
public static class DiffKinds
{
    public const string Added = "adicionado";
    public const string Removed = "removido";
    public const string Changed = "alterado";

    /// <summary>A correspondência entre as versões é ambígua (ou só pela origem no código): nunca vira inclusão/remoção.</summary>
    public const string Uncertain = "incerto";
}

/// <summary>Natureza de uma diferença: só comportamento e runtime indicam mudança do endpoint.</summary>
public static class DiffImpacts
{
    /// <summary>Regra, validação, condição, limite, resultado esperado, efeito ou persistência.</summary>
    public const string Behavior = "comportamento";

    /// <summary>Observação da validação em runtime: status, HTTP observado, divergência esperado × observado.</summary>
    public const string Runtime = "runtime";

    /// <summary>Texto escrito pela IA (objetivo, títulos, contexto, mensagens) ou descritivo: não prova mudança de comportamento.</summary>
    public const string Documentation = "documentacao";

    /// <summary>Valores do payload, dados reais e sintéticos usados no cenário (ex.: datas relativas a hoje).</summary>
    public const string TestData = "dados-teste";

    /// <summary>Arquivo/método onde a regra está: o código mudou de lugar.</summary>
    public const string Origin = "origem";

    /// <summary>Tentativas, duração, solver, observações e a execução da validação.</summary>
    public const string Environment = "ambiente";

    /// <summary>Impactos que contam como diferença (os demais são variações exibidas sob demanda).</summary>
    public static bool IsPrimary(string impact) => impact is Behavior or Runtime or Documentation;

    /// <summary>Ordem de relevância: o item assume o impacto mais relevante entre os campos alterados.</summary>
    public static int Rank(string impact) => impact switch
    {
        Behavior => 0, Runtime => 1, Documentation => 2, TestData => 3, Origin => 4, _ => 5,
    };
}

/// <summary>
/// Comparação entre duas versões salvas da análise do mesmo endpoint (calculada sob demanda, sem IA e sem executar o
/// endpoint): o que foi adicionado, removido ou alterado nas abas Resumo e Cenários.
/// </summary>
public class AnalysisDiffResult
{
    public string AnalysisId { get; set; } = "";

    /// <summary>Versão usada como base (a anterior, salvo base explícita); nula na primeira análise do endpoint.</summary>
    public AnalysisDiffVersion? Base { get; set; }

    public AnalysisDiffVersion Current { get; set; } = new();

    public bool HasBase { get; set; }

    /// <summary>Alguma diferença de comportamento, runtime ou documentação nas abas comparáveis.</summary>
    public bool HasDifferences { get; set; }

    /// <summary>Estado em uma frase: sem anterior, sem diferenças ou o total encontrado.</summary>
    public string Message { get; set; } = "";

    public AnalysisDiffCounts Counts { get; set; } = new();

    public List<AnalysisDiffSection> Sections { get; set; } = [];

    /// <summary>Contexto da comparação: versões do analisador, IA diferente, mesmo código-fonte...</summary>
    public List<string> Notes { get; set; } = [];
}

public class AnalysisDiffVersion
{
    public string AnalysisId { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Commit { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; set; }

    public string AnalyzerVersion { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AiProvider { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AiModel { get; set; }

    public List<string> Sections { get; set; } = [];

    public bool HasAi { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeStatus { get; set; }
}

public class AnalysisDiffCounts
{
    public int Added { get; set; }

    public int Removed { get; set; }

    public int Changed { get; set; }

    public int Uncertain { get; set; }

    /// <summary>Abas ou partes (documentação da IA, runtime) ausentes em uma das versões.</summary>
    public int NotComparable { get; set; }

    /// <summary>Itens alterados só em dados de teste, origem no código ou ambiente (fora dos totais acima).</summary>
    public int Minor { get; set; }

    public int Unchanged { get; set; }

    public int Total => Added + Removed + Changed + Uncertain;

    public void Add(AnalysisDiffCounts other)
    {
        Added += other.Added;
        Removed += other.Removed;
        Changed += other.Changed;
        Uncertain += other.Uncertain;
        NotComparable += other.NotComparable;
        Minor += other.Minor;
        Unchanged += other.Unchanged;
    }
}

/// <summary>Aba comparada: summary (Resumo) ou scenarios (Cenários).</summary>
public class AnalysisDiffSection
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public bool Comparable { get; set; } = true;

    /// <summary>Por que a aba não foi comparada (aba ausente em uma das versões).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    public AnalysisDiffCounts Counts { get; set; } = new();

    public List<AnalysisDiffGroup> Groups { get; set; } = [];
}

/// <summary>Parte da aba: validações, regras, efeitos, entidades, documentação, matriz estática, matriz validada...</summary>
public class AnalysisDiffGroup
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public bool Comparable { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    public AnalysisDiffCounts Counts { get; set; } = new();

    public List<AnalysisDiffItem> Items { get; set; } = [];

    public List<string> Notes { get; set; } = [];
}

public class AnalysisDiffItem
{
    /// <summary>Um de <see cref="DiffKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Um de <see cref="DiffImpacts"/>: o mais relevante entre os campos alterados.</summary>
    public string Impact { get; set; } = DiffImpacts.Behavior;

    /// <summary>Identificador semântico (não depende de CEN-xx, Dx, Cx nem de número de linha).</summary>
    public string Key { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Id na versão anterior (CEN-03, D2...), quando existe.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseRef { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentRef { get; set; }

    /// <summary>Campos diferentes (alterado/incerto) ou todos os campos (adicionado/removido), com antes e depois.</summary>
    public List<AnalysisDiffField> Fields { get; set; } = [];

    /// <summary>Estrutura canônica completa de cada lado, para os detalhes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Before { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? After { get; set; }

    /// <summary>Alerta que pede atenção (ex.: cenário confirmado que ficou inconclusivo), sem ser prova de regressão.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Alert { get; set; }

    /// <summary>Só em "Entidades alteradas": a entidade, a operação e a situação de cada propriedade.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnalysisDiffEntity? Entity { get; set; }

    public List<string> Notes { get; set; } = [];
}

/// <summary>Situação de uma propriedade gravada (INSERT/UPDATE) entre as duas versões.</summary>
public static class EntityPropertyChanges
{
    /// <summary>Passou a ser gravada.</summary>
    public const string Added = "adicionada";

    /// <summary>Deixou de ser gravada.</summary>
    public const string Removed = "removida";

    /// <summary>Continua gravada, com outro valor ou outra condição.</summary>
    public const string Changed = "alterada";

    public const string Unchanged = "igual";
}

/// <summary>
/// Entidade comparada: identificada pela entidade e pela operação (INSERT, UPDATE, DELETE). Passar a gravar é
/// <see cref="DiffKinds.Added"/>, deixar de gravar é <see cref="DiffKinds.Removed"/> e gravar outras propriedades, valores
/// ou condições é <see cref="DiffKinds.Changed"/>.
/// </summary>
public class AnalysisDiffEntity
{
    public string Entity { get; set; } = "";

    /// <summary>Um de <see cref="EntityOperations"/>.</summary>
    public string Operation { get; set; } = "";

    /// <summary>
    /// Condição própria da operação (lado atual; o anterior quando foi removida), em cláusulas: sem as cláusulas comuns a
    /// todas as gravações do fluxo (validações e guardas do caminho de sucesso). Vazia: sempre que o fluxo chega até aqui.
    /// </summary>
    public List<string> Conditions { get; set; } = [];

    /// <summary>Cláusulas que entraram na condição da operação (só quando a entidade existe nas duas versões).</summary>
    public List<string> ConditionsAdded { get; set; } = [];

    public List<string> ConditionsRemoved { get; set; } = [];

    /// <summary>União das propriedades das duas versões, em ordem alfabética.</summary>
    public List<AnalysisDiffEntityProperty> Properties { get; set; } = [];
}

public class AnalysisDiffEntityProperty
{
    public string Name { get; set; } = "";

    /// <summary>Um de <see cref="EntityPropertyChanges"/>.</summary>
    public string Change { get; set; } = EntityPropertyChanges.Unchanged;

    /// <summary>Atribuições na versão anterior; vazia quando a propriedade não era gravada (ou não tem valor conhecido).</summary>
    public List<AnalysisDiffAssignment> Before { get; set; } = [];

    public List<AnalysisDiffAssignment> After { get; set; } = [];

    /// <summary>Só o que mudou numa propriedade alterada: atribuições (valor + condição) que saíram.</summary>
    public List<AnalysisDiffAssignment> Removed { get; set; } = [];

    /// <summary>Atribuições (valor + condição) que entraram.</summary>
    public List<AnalysisDiffAssignment> Added { get; set; } = [];
}

/// <summary>Valor atribuído à propriedade e a condição própria em que é atribuído.</summary>
public class AnalysisDiffAssignment
{
    public string Value { get; set; } = "";

    /// <summary>
    /// Cláusulas próprias da atribuição: sem as do fluxo e as da operação. Nula quando é atribuída sempre que a operação acontece.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Conditions { get; set; }
}

public class AnalysisDiffField
{
    /// <summary>Caminho estável do campo: "esperado.http", "foco.expressao".</summary>
    public string Path { get; set; } = "";

    public string Label { get; set; } = "";

    public string Impact { get; set; } = DiffImpacts.Behavior;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Before { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? After { get; set; }
}
