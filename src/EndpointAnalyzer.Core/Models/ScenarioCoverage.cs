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

/// <summary>Tipos de ponto de encerramento (folha) do fluxo: onde a requisição realmente termina.</summary>
public static class TerminalKinds
{
    /// <summary>Validação de entrada que falha antes da action (DataAnnotations, FluentValidation, [Required] implícito).</summary>
    public const string Validation = "validacao";

    /// <summary>Model binding: tipo do JSON incompatível com o campo.</summary>
    public const string Binding = "tipo";

    /// <summary>Regra do código que encerra com erro (throw, return de erro, notificação, guarda, catch).</summary>
    public const string Rule = "regra";

    /// <summary>Return antecipado do próprio endpoint (os returns de métodos internos são propagados ao chamador).</summary>
    public const string Exit = "saida";

    /// <summary>Resposta final do endpoint; um desfecho por conjunto de efeitos executados (inseriu vs. não inseriu).</summary>
    public const string Success = "sucesso";
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
