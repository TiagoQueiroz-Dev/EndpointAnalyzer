using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

public enum FlowNodeKind { Entry, Validation, Decision, Effect, Terminal }

/// <summary>Nó da visão lógica de execução: entrada, validação de entrada, decisão, efeito ou folha.</summary>
public sealed class FlowTreeNode
{
    /// <summary>N1, N2... na ordem de criação (ordem de execução).</summary>
    public required string Id { get; init; }

    /// <summary>Chave lógica: tipo + âncora semântica no código (método, condição normalizada), sem linha.</summary>
    public required string Key { get; init; }

    public required FlowNodeKind Kind { get; init; }

    public required string Label { get; init; }

    public SourceReference? Source { get; init; }
}

/// <summary>Transição entre dois nós: condição simbólica (quando há) e sentido (true, false, case ou next).</summary>
public sealed class FlowTreeEdge
{
    public required FlowTreeNode From { get; init; }

    public required FlowTreeNode To { get; init; }

    public Pred? Condition { get; init; }

    public string? ConditionCode { get; init; }

    public required string Sense { get; init; }
}

/// <summary>
/// Visão lógica da execução do endpoint. Internamente é um grafo com junções: ramos que convergem para a mesma
/// validação ou para o mesmo trecho não duplicam o restante do fluxo.
/// </summary>
public sealed class FlowTree
{
    public List<FlowTreeNode> Nodes { get; } = [];

    public List<FlowTreeEdge> Edges { get; } = [];

    public List<TerminalOutcome> Terminals { get; init; } = [];

    /// <summary>Lacunas de análise: condições não traduzidas (opacas) e trechos que o modelo não consegue representar.</summary>
    public List<string> Gaps { get; } = [];

    public FlowTreeInfo ToInfo() => new()
    {
        Nodes = Nodes.Select(n => new FlowNodeInfo
        {
            Id = n.Id,
            Key = n.Key,
            Kind = n.Kind switch
            {
                FlowNodeKind.Entry => "entry",
                FlowNodeKind.Validation => "validation",
                FlowNodeKind.Decision => "decision",
                FlowNodeKind.Effect => "effect",
                _ => "terminal",
            },
            Label = n.Label,
            Source = n.Source,
        }).ToList(),
        Edges = Edges.Select(e => new FlowEdgeInfo { From = e.From.Id, To = e.To.Id, Condition = e.ConditionCode, Sense = e.Sense }).ToList(),
    };
}

/// <summary>
/// Folha da árvore: um desfecho distinto do endpoint (validação que falha, regra que encerra, retorno antecipado ou
/// resposta final com um conjunto de efeitos). Distinguida pelo ponto semântico do código, nunca pela mensagem HTTP.
/// </summary>
public sealed class TerminalOutcome
{
    /// <summary>Chave estável (<see cref="Scenario.Terminal"/>), com discriminador quando há ocorrências equivalentes.</summary>
    public required string Key { get; init; }

    /// <summary>Um de <see cref="TerminalKinds"/>.</summary>
    public required string Kind { get; init; }

    public required string Label { get; init; }

    public bool Success => Kind is TerminalKinds.Exit or TerminalKinds.Success;

    public ValidationRule? Validation { get; init; }

    /// <summary>
    /// Regras do mesmo ponto do código alcançadas por mais de um caminho (o mesmo método chamado de dois ramos com a
    /// mesma condição): uma folha só, com um caminho alternativo por ocorrência.
    /// </summary>
    public IReadOnlyList<CodeRule> Rules { get; init; } = [];

    public IReadOnlyList<EarlyExit> Exits { get; init; } = [];

    /// <summary>Variação de sucesso: o literal do ramo no valor oposto ao do caminho principal.</summary>
    public Pred? Flipped { get; init; }

    /// <summary>Campo do body com tipo JSON incompatível (model binding).</summary>
    public InputField? BindingField { get; init; }

    public SourceReference? Source { get; init; }

    public int? Status { get; init; }

    public FlowTreeNode? Node { get; set; }

    // ---- Fase 3: viabilidade ----

    public Feasibility Feasibility { get; set; } = Feasibility.Unknown;

    public string? Reason { get; set; }

    /// <summary>Valores que satisfazem o caminho (prova do SAT, ou hipótese quando UNKNOWN por condição opaca).</summary>
    public Assignment? Witness { get; set; }

    /// <summary>Validação que só falha junto com outra do mesmo campo (não há valor que viole só ela).</summary>
    public bool Aggregated { get; set; }

    /// <summary>Mesmo desfecho (resposta e efeitos) de outra folha: a linha desta folha reaproveita o payload da outra.</summary>
    public TerminalOutcome? EquivalentTo { get; set; }
}

/// <summary>Caminho até uma folha: sequência de decisões (restrições acumuladas) e a viabilidade.</summary>
public sealed class PathCandidate
{
    public required TerminalOutcome Leaf { get; init; }

    /// <summary>Restrições do caminho: guardas anteriores que passam + condição que dispara a folha.</summary>
    public required IReadOnlyList<Pred> Constraints { get; init; }

    /// <summary>Condição-alvo (gatilho da regra, violação, literal do ramo): onde uma condição opaca torna a folha UNKNOWN.</summary>
    public Pred? Target { get; init; }

    public required SolveResult Result { get; init; }
}

/// <summary>
/// Obrigação de cobertura: atingir uma folha ou exercitar uma classe/limite/ramo relevante que a atinge.
/// Identificar uma obrigação não implica um novo teste: um cenário já escolhido pode cobrir várias.
/// </summary>
public sealed class CoverageTarget
{
    public string Id { get; set; } = "";

    /// <summary>Chave estável: folha-âncora + discriminador da obrigação.</summary>
    public required string Key { get; init; }

    /// <summary>Um de <see cref="CoverageTargetKinds"/>.</summary>
    public required string Kind { get; init; }

    public required string Label { get; init; }

    /// <summary>Folha de onde a obrigação vem (regra, validação, caminho principal).</summary>
    public required TerminalOutcome Anchor { get; init; }

    /// <summary>Folha que o cenário precisa atingir; nula quando qualquer desfecho de sucesso serve.</summary>
    public TerminalOutcome? Leaf { get; init; }

    /// <summary>Precisa ser verdadeiro com os valores do cenário (classe, limite, ramo, iterações).</summary>
    public Pred? Predicate { get; init; }

    public Probe? Probe { get; init; }

    public CodeRule? Rule { get; init; }

    public InputField? Field { get; init; }

    public int? Iterations { get; init; }

    public string Status { get; set; } = "";

    public string? Reason { get; set; }

    /// <summary>Gerada para ela: nenhum cenário anterior a cobria.</summary>
    public bool Selected { get; set; }
}

/// <summary>Identidade estável do cenário: hash do endpoint e da obrigação (folha + classe necessária).</summary>
public static partial class ScenarioKey
{
    public static string Of(string endpoint, string target)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{target}"));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bawait\s+")]
    private static partial Regex Await();

    /// <summary>Condição normalizada para a chave: sem formatação, await, ?. e ! (não mudam o comportamento).</summary>
    public static string Code(string? code) => string.IsNullOrWhiteSpace(code) ? ""
        : Spaces().Replace(Await().Replace(code, "").Replace("?.", ".").Replace("!.", "."), "");
}
