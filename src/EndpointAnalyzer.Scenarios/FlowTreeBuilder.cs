using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>
/// Fase 1: monta a visão lógica da execução a partir do <see cref="FlowModel"/> (regras, saídas, efeitos e a ordem de
/// execução interprocedural) e das validações de entrada. A raiz é a entrada HTTP; as validações (model binding,
/// DataAnnotations, FluentValidation) vêm antes da action; regras, retornos antecipados, ramos e efeitos seguem a ordem
/// do código. Ramos que convergem apontam para o mesmo nó seguinte (junção): o restante do fluxo não é duplicado.
/// </summary>
internal static class FlowTreeBuilder
{
    public static FlowTree Build(FlowContext ctx, List<TerminalOutcome> leaves)
    {
        var tree = new FlowTree { Terminals = leaves };
        var keys = new Dictionary<string, int>();

        FlowTreeNode Node(FlowNodeKind kind, string key, string label, SourceReference? source)
        {
            var count = keys[key] = keys.GetValueOrDefault(key) + 1;
            var node = new FlowTreeNode { Id = $"N{tree.Nodes.Count + 1}", Key = count == 1 ? key : $"{key}#{count}", Kind = kind, Label = label, Source = source };
            tree.Nodes.Add(node);
            return node;
        }

        void Edge(FlowTreeNode from, FlowTreeNode to, string sense, Pred? condition = null, string? code = null) =>
            tree.Edges.Add(new FlowTreeEdge { From = from, To = to, Sense = sense, Condition = condition, ConditionCode = code ?? condition?.Text });

        FlowTreeNode Terminal(TerminalOutcome leaf) => leaf.Node ??= Node(FlowNodeKind.Terminal, leaf.Key, leaf.Label, leaf.Source);

        var current = Node(FlowNodeKind.Entry, $"entrada|{ctx.Endpoint}", ctx.Endpoint, null);
        // Como o nó atual continua: "next" (sequência), "false" (a condição não disparou) ou junção dos dois lados de um ramo.
        var sense = "next";
        Pred? junction = null;

        void Connect(FlowTreeNode to, Pred? condition = null, string? code = null)
        {
            if (junction is not null)
            {
                Edge(current, to, "true", junction);
                Edge(current, to, "false", Pred.Not(junction));
            }
            else Edge(current, to, sense, condition, code);
            current = to;
            junction = null;
            sense = "next";
        }

        var input = leaves.Where(l => l.Kind is TerminalKinds.Validation or TerminalKinds.Binding).ToList();
        if (input.Count > 0)
        {
            var gate = Node(FlowNodeKind.Validation, "validacao-entrada", "Validação de entrada antes da action (model binding, DataAnnotations, FluentValidation)", null);
            Connect(gate);
            foreach (var leaf in input)
                Edge(gate, Terminal(leaf), "true", leaf.Validation?.Violation, leaf.Validation?.ViolationCode ?? "tipo do JSON incompatível");
            sense = "false";
        }

        var ruleLeaves = leaves.SelectMany(l => l.Rules.Select(r => (Rule: r, Leaf: l))).ToDictionary(x => x.Rule, x => x.Leaf);
        var exitLeaves = leaves.SelectMany(l => l.Exits.Select(x => (Exit: x, Leaf: l))).ToDictionary(x => x.Exit, x => x.Leaf);
        var steps = ctx.Rules.Select(r => (Order: r.Site.Order, Item: (object)r))
            .Concat(ctx.Exits.Select(x => (Order: x.Site.Order, Item: (object)x)))
            .Concat(ctx.Flow.Effects.Select(e => (Order: e.Site.Order, Item: (object)e)))
            .OrderBy(s => s.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare))
            .Select(s => s.Item)
            .ToList();
        var branchNodes = new HashSet<string>();

        foreach (var step in steps)
        {
            switch (step)
            {
                case CodeRule rule:
                {
                    var leaf = ruleLeaves[rule];
                    var decision = Node(FlowNodeKind.Decision, $"{leaf.Key}|decisao", $"se {rule.TriggerCode}", rule.Site.Source);
                    Connect(decision);
                    Edge(decision, Terminal(leaf), "true", rule.Trigger, rule.TriggerCode);
                    sense = "false";
                    break;
                }
                case EarlyExit exit:
                {
                    var leaf = exitLeaves[exit];
                    var decision = Node(FlowNodeKind.Decision, $"{leaf.Key}|decisao", $"se {exit.TriggerCode}", exit.Site.Source);
                    Connect(decision);
                    Edge(decision, Terminal(leaf), "true", exit.Trigger, exit.TriggerCode);
                    sense = "false";
                    break;
                }
                case EffectSite effect:
                {
                    var conjuncts = Evaluator.Conjuncts(effect.Site.Path).Select(c => c.Key).ToHashSet();
                    var literals = ctx.Branches.Where(b => conjuncts.Contains(b.Key) || conjuncts.Contains(Pred.Not(b).Key)).ToList();
                    foreach (var literal in literals)
                    {
                        var atom = literal is NotPred n ? n.Inner : literal;
                        if (!branchNodes.Add(atom.Key)) continue;
                        Connect(Node(FlowNodeKind.Decision, $"ramo|{ScenarioKey.Code(atom.Text)}", $"ramo: {atom.Text}", effect.Site.Source));
                        junction = atom;
                    }
                    var condition = literals.Count == 0 ? null : Pred.And(literals.Select(l => Evaluator.Conjuncts(effect.Site.Path).First(c => c.Key == l.Key || c.Key == Pred.Not(l).Key)));
                    if (junction is not null)
                    {
                        // O efeito só acontece num dos lados do ramo; o outro lado segue direto para a junção (próximo nó).
                        var node = Node(FlowNodeKind.Effect, $"efeito|{effect.Kind}|{effect.Target}|{ScenarioKey.Code(effect.Description)}", effect.Description, effect.Site.Source);
                        Edge(current, node, "true", condition);
                        current = node;
                        junction = null;
                        sense = "next";
                    }
                    else Connect(Node(FlowNodeKind.Effect, $"efeito|{effect.Kind}|{effect.Target}|{ScenarioKey.Code(effect.Description)}", effect.Description, effect.Site.Source), condition);
                    break;
                }
            }
        }

        // Resposta final: o caminho principal e, do mesmo ponto, as variações de ramo que mudam os efeitos.
        var last = current;
        foreach (var leaf in leaves.Where(l => l is { Kind: TerminalKinds.Success, Flipped: null })) Connect(Terminal(leaf));
        foreach (var leaf in leaves.Where(l => l is { Kind: TerminalKinds.Success, Flipped: not null }))
            Edge(last, Terminal(leaf), "case", leaf.Flipped);

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
