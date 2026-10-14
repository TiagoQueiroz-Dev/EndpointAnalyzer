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

/// <summary>Transição entre dois nós: condição simbólica (quando há) e sentido (true, false ou next).</summary>
public sealed class FlowTreeEdge
{
    public required FlowTreeNode From { get; init; }

    public required FlowTreeNode To { get; init; }

    public string? ConditionCode { get; init; }

    public required string Sense { get; init; }
}

/// <summary>
/// Visão lógica da execução do endpoint, montada a partir dos caminhos explorados. Internamente é um grafo com junções:
/// caminhos que passam pelo mesmo ponto do código compartilham o nó (o restante do fluxo não é duplicado).
/// </summary>
public sealed class FlowTree
{
    public List<FlowTreeNode> Nodes { get; } = [];

    public List<FlowTreeEdge> Edges { get; } = [];

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
/// Ponto real de encerramento da requisição: validação de entrada que devolve 400 antes da action, tipo do JSON
/// incompatível (model binding), regra que encerra com erro (throw, return de erro, notificação, guarda, catch) ou
/// return do endpoint (retorno antecipado ou resposta final). Os returns de métodos internos não são pontos de
/// encerramento: o valor deles é propagado ao chamador e o fluxo segue até um destes pontos.
/// </summary>
public sealed class TerminalPoint
{
    /// <summary>Chave estável pelo ponto semântico do código (método, tipo, condição normalizada), sem linha nem mensagem.</summary>
    public required string Key { get; init; }

    /// <summary>Um de <see cref="TerminalKinds"/>.</summary>
    public required string Kind { get; init; }

    public required string Label { get; init; }

    public bool Success => Kind is TerminalKinds.Exit or TerminalKinds.Success;

    public ValidationRule? Validation { get; init; }

    /// <summary>
    /// Regras do mesmo ponto do código alcançadas por mais de um caminho (o mesmo método chamado de dois ramos com a
    /// mesma condição): um ponto só.
    /// </summary>
    public IReadOnlyList<CodeRule> Rules { get; init; } = [];

    /// <summary>Return antecipado do próprio endpoint.</summary>
    public EarlyExit? Exit { get; init; }

    /// <summary>Campo do body com tipo JSON incompatível (model binding).</summary>
    public InputField? BindingField { get; init; }

    public SourceReference? Source { get; init; }

    public int? Status { get; init; }
}

/// <summary>Um nó percorrido num caminho e a transição que leva até ele (sentido e condição assumida).</summary>
public sealed record PathStep(string NodeKey, FlowNodeKind Kind, string Label, SourceReference? Source, string Sense, string? Condition);

/// <summary>
/// Caminho de execução do endpoint até um ponto de encerramento: as restrições acumuladas (guardas anteriores que
/// passam, ramos escolhidos e a condição que dispara o encerramento), os efeitos que executam e a viabilidade.
/// </summary>
public sealed class ExecutionPath
{
    public required TerminalPoint Terminal { get; init; }

    /// <summary>Restrições exatas do caminho, na ordem em que o fluxo as avalia.</summary>
    public required IReadOnlyList<PathConstraint> Constraints { get; init; }

    /// <summary>Efeitos que executam com certeza no caminho, na ordem.</summary>
    public required IReadOnlyList<EffectSite> Effects { get; init; }

    /// <summary>Returns de métodos internos que o caminho toma (deixam de executar o resto do método).</summary>
    public required IReadOnlyList<EarlyExit> InnerExits { get; init; }

    public required IReadOnlyList<PathStep> Steps { get; init; }

    /// <summary>Condições que distinguem o desfecho (gatilho do encerramento, ramos de efeito, returns internos).</summary>
    public required IReadOnlyList<Decision> Decisive { get; init; }

    public required SolveResult Result { get; init; }

    /// <summary>Validação que só falha junto com outra do mesmo campo (os erros são devolvidos juntos).</summary>
    public bool Aggregated { get; init; }

    /// <summary>Por que o caminho é UNKNOWN ou como ele foi montado (validação agregada).</summary>
    public string? Reason { get; init; }
}

/// <summary>Condição decisiva de um caminho: o predicado, como no código, e o valor que o caminho assume.</summary>
public sealed record Decision(Pred Pred, string Code, bool Value);

/// <summary>Restrição de um caminho: a exata e, quando houver, uma forma mais legível para o payload (¬gatilho em vez de ¬(alcançada ∧ gatilho)).</summary>
public sealed record PathConstraint(Pred Exact, Pred? Preferred = null);

/// <summary>
/// Desfecho distinto do endpoint: um ponto de encerramento e, nas respostas de sucesso, o conjunto de efeitos (inseriu
/// vs. não inseriu). Caminhos diferentes que chegam ao mesmo ponto com os mesmos efeitos são o mesmo desfecho: um
/// cenário só. Distinguido pelo ponto semântico do código, nunca pela mensagem ou pelo status HTTP.
/// </summary>
public sealed class TerminalOutcome
{
    /// <summary>Chave estável (<see cref="Scenario.Terminal"/>).</summary>
    public required string Key { get; init; }

    public required TerminalPoint Point { get; init; }

    public required string Label { get; init; }

    public string Kind => Point.Kind;

    public bool Success => Point.Success;

    /// <summary>Assinatura dos efeitos (entidade + operação, eventos, envios) que distingue as respostas de sucesso.</summary>
    public IReadOnlyList<string> Effects { get; init; } = [];

    /// <summary>Caminhos viáveis (ou não decididos) até o desfecho, o mais simples primeiro.</summary>
    public List<ExecutionPath> Paths { get; } = [];

    /// <summary>Efeitos do desfecho principal do mesmo ponto que este não executa (ex.: "ItemRemessa INSERT").</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>Efeitos que só este desfecho executa, em relação ao principal do mesmo ponto.</summary>
    public IReadOnlyList<string> Extra { get; init; } = [];

    /// <summary>Variação de efeitos de um return de sucesso (não é o desfecho principal daquele return).</summary>
    public bool IsVariant { get; init; }

    public FlowTreeNode? Node { get; set; }

    public Feasibility Feasibility { get; set; } = Feasibility.Unknown;

    public string? Reason { get; set; }

    /// <summary>Validação que só falha junto com outra do mesmo campo (não há valor que viole só ela).</summary>
    public bool Aggregated { get; set; }
}

/// <summary>Identidade estável do cenário: hash do endpoint e do desfecho (ponto de encerramento + efeitos).</summary>
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
