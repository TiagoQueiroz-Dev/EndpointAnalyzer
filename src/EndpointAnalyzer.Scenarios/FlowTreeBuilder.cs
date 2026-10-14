using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Visão lógica da execução a partir dos caminhos explorados: raiz na entrada HTTP, validações antes da action,
/// decisões (regras, returns, ramos de efeito) e efeitos na ordem do código, e uma folha por desfecho. Cada ponto do
/// código é um nó só: caminhos que passam por ele convergem (junção) e o restante do fluxo não é duplicado.
/// </summary>
internal static class FlowTreeBuilder
{
    public static FlowTree Build(FlowContext ctx, IReadOnlyList<TerminalOutcome> outcomes)
    {
        var tree = new FlowTree();
        var nodes = new Dictionary<string, FlowTreeNode>();
        var edges = new HashSet<(string, string, string)>();

        FlowTreeNode Node(string key, FlowNodeKind kind, string label, SourceReference? source)
        {
            if (nodes.TryGetValue(key, out var existing)) return existing;
            var node = nodes[key] = new FlowTreeNode { Id = $"N{tree.Nodes.Count + 1}", Key = key, Kind = kind, Label = key == "entrada" ? ctx.Endpoint : label, Source = source };
            tree.Nodes.Add(node);
            return node;
        }

        foreach (var outcome in outcomes)
            foreach (var path in outcome.Paths)
            {
                FlowTreeNode? previous = null;
                foreach (var step in path.Steps)
                {
                    var node = step.Kind == FlowNodeKind.Terminal
                        ? outcome.Node ??= Node(outcome.Key, FlowNodeKind.Terminal, outcome.Label, outcome.Point.Source)
                        : Node(step.NodeKey, step.Kind, step.Label, step.Source);
                    if (previous is not null && edges.Add((previous.Id, node.Id, step.Sense)))
                        tree.Edges.Add(new FlowTreeEdge { From = previous, To = node, Sense = step.Sense, ConditionCode = step.Condition });
                    previous = node;
                }
            }

        // Desfecho sem caminho (UNSAT ou não decidido): a folha existe no código, sem transição que chegue nela.
        foreach (var outcome in outcomes.Where(o => o.Node is null))
            outcome.Node = Node(outcome.Key, FlowNodeKind.Terminal, outcome.Label, outcome.Point.Source);

        tree.Gaps.AddRange(Gaps(ctx));
        return tree;
    }

    /// <summary>Condições que não foram traduzidas (chamadas não resolvidas, padrões não suportados): o modelo não as decide.</summary>
    private static IEnumerable<string> Gaps(FlowContext ctx)
    {
        var preds = ctx.Rules.SelectMany(r => new[] { r.Site.Path, r.Trigger })
            .Concat(ctx.Exits.SelectMany(x => new[] { x.Site.Path, x.Trigger }))
            .Concat(ctx.Flow.Effects.Select(e => e.Site.Path))
            .Concat(ctx.Validations.Select(v => v.Violation));
        return preds.SelectMany(Evaluator.Atoms).OfType<OpaquePred>().DistinctBy(o => o.Key)
            .Select(o => $"Condição não traduzida (o solver só pode supor o valor): {o.Text}");
    }
}
