using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>Resultado trivalente da viabilidade de um caminho (restrições do caminho até a folha).</summary>
public static class PathFeasibilities
{
    /// <summary>Existe combinação simbólica que satisfaz o caminho (não comprova que o banco real tem os dados).</summary>
    public const string Sat = "sat";

    /// <summary>Caminho provadamente contraditório, na semântica suportada pelo solver.</summary>
    public const string Unsat = "unsat";

    /// <summary>Não foi possível decidir: condição opaca, chamada não resolvida, limite do solver. Nunca é "inalcançável".</summary>
    public const string Unknown = "unknown";
}

/// <summary>Tipos de desfecho (folha) do fluxo.</summary>
public static class TerminalKinds
{
    /// <summary>Validação de entrada que falha antes da action (DataAnnotations, FluentValidation, [Required] implícito).</summary>
    public const string Validation = "validacao";

    /// <summary>Model binding: tipo do JSON incompatível com o campo.</summary>
    public const string Binding = "tipo";

    /// <summary>Regra do código que encerra com erro (throw, return de erro, notificação, guarda, catch).</summary>
    public const string Rule = "regra";

    /// <summary>Retorno antecipado sem erro, que deixa de executar efeitos ou regras seguintes.</summary>
    public const string Exit = "saida";

    /// <summary>Resposta final de sucesso (o caminho principal e as variações de ramo que mudam os efeitos).</summary>
    public const string Success = "sucesso";
}

/// <summary>Tipos de obrigação de cobertura que guiam a geração dos cenários.</summary>
public static class CoverageTargetKinds
{
    /// <summary>Atingir o desfecho (folha).</summary>
    public const string Outcome = "desfecho";

    /// <summary>Classe de equivalência com semântica própria (null, só espaços, fora do enum...).</summary>
    public const string Class = "classe";

    /// <summary>Um lado de um limite (comprimento, faixa, comparação do código com constante).</summary>
    public const string Boundary = "limite";

    /// <summary>Ramo que muda os efeitos (verdadeiro e falso).</summary>
    public const string Branch = "ramo";

    /// <summary>Iterações de um laço (zero, uma, várias).</summary>
    public const string Loop = "laco";

    /// <summary>Campo ausente que chega com o valor padrão do tipo ([Required] sem efeito em value type).</summary>
    public const string Default = "padrao";
}

/// <summary>Situação de uma obrigação durante a geração.</summary>
public static class CoverageStatuses
{
    /// <summary>Algum cenário da matriz exercita a obrigação (a simulação do payload atinge a folha-alvo).</summary>
    public const string Covered = "coberta";

    /// <summary>Viável, mas nenhum payload gerado atingiu a folha-alvo.</summary>
    public const string Uncovered = "nao-coberta";

    /// <summary>Caminho contraditório (UNSAT): não existe entrada que a exercite.</summary>
    public const string Infeasible = "inviavel";

    /// <summary>Viabilidade não decidida (UNKNOWN): fica como pendência, nunca como inalcançável.</summary>
    public const string Pending = "pendente";

    /// <summary>Não gerada porque o limite de cenários foi atingido (nunca a folha: toda folha tem linha na matriz).</summary>
    public const string Truncated = "limite";

    /// <summary>Mesmo desfecho de outra folha (mesma resposta e mesmos efeitos): a linha da folha reaproveita o payload da outra.</summary>
    public const string Equivalent = "equivalente";
}

/// <summary>
/// Visão lógica da execução do endpoint: raiz na entrada HTTP, validações antes da action, decisões e efeitos na ordem
/// do código e as folhas. Grafo com junções (ramos que convergem não duplicam o restante do fluxo).
/// </summary>
public class FlowTreeInfo
{
    public List<FlowNodeInfo> Nodes { get; set; } = [];

    public List<FlowEdgeInfo> Edges { get; set; } = [];
}

public class FlowNodeInfo
{
    /// <summary>N1, N2... na ordem de execução.</summary>
    public string Id { get; set; } = "";

    /// <summary>Chave lógica (sem linha): base das chaves das folhas.</summary>
    public string Key { get; set; } = "";

    /// <summary>entry, validation, decision, effect ou terminal.</summary>
    public string Kind { get; set; } = "";

    public string Label { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }
}

public class FlowEdgeInfo
{
    public string From { get; set; } = "";

    public string To { get; set; } = "";

    /// <summary>Condição como no código, quando a transição depende dela.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    /// <summary>true, false, case ou next (sequência).</summary>
    public string Sense { get; set; } = "";
}
