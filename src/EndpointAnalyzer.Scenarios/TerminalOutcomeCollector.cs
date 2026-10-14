using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Fluxo do endpoint já resolvido (regras, saídas, validações e ramos) que alimenta a árvore, as folhas e a cobertura.</summary>
internal sealed record FlowContext(
    string Endpoint,
    InputModel Input,
    VarTable Vars,
    FlowModel Flow,
    IReadOnlyList<ValidationRule> Validations,
    Pred ValidationsOk,
    IReadOnlyList<CodeRule> Rules,
    IReadOnlyList<EarlyExit> Exits,
    IReadOnlyList<Pred> Branches,
    InputField? MismatchField,
    IReadOnlyList<int> LoopIterations);

/// <summary>
/// Fase 2: identifica as folhas distintas do fluxo, cada uma pelo ponto semântico do código (método, tipo de saída,
/// condição normalizada), nunca pela mensagem ou pelo status HTTP:
/// <list type="bullet">
/// <item>cada validação de entrada que encerra a requisição (e o tipo JSON incompatível do model binding);</item>
/// <item>cada regra que encerra com erro: o mesmo ponto alcançado por dois caminhos com a mesma condição é uma folha só;</item>
/// <item>cada retorno antecipado sem erro que deixa de executar efeitos ou regras;</item>
/// <item>a resposta final do caminho principal e as variações de ramo que mudam os efeitos.</item>
/// </list>
/// </summary>
internal static class TerminalOutcomeCollector
{
    public static List<TerminalOutcome> Collect(FlowContext ctx, Func<CodeRule, string> label)
    {
        var leaves = new List<TerminalOutcome>();
        var flow = ctx.Flow;

        foreach (var v in ctx.Validations)
            leaves.Add(new TerminalOutcome
            {
                Key = $"{TerminalKinds.Validation}|{v.Field.Path}|{v.Kind}|{v.Origin}|{ScenarioKey.Code(v.WhenCode ?? v.When?.Text)}",
                Kind = TerminalKinds.Validation,
                Label = $"{v.FieldName}: {v.Text}{(v.When is null ? "" : $" quando {v.WhenCode ?? v.When.Text}")}",
                Validation = v,
                Source = v.Source,
                Status = ValidationStatus(flow),
            });

        if (ctx.MismatchField is { } field)
            leaves.Add(new TerminalOutcome
            {
                Key = $"{TerminalKinds.Binding}|{field.Path}",
                Kind = TerminalKinds.Binding,
                Label = $"{field.Name}: tipo do JSON incompatível com {field.TypeDisplay} (model binding)",
                BindingField = field,
                Status = ValidationStatus(flow),
            });

        // Regras e saídas na ordem de execução; ocorrências do mesmo trecho com a mesma condição viram uma folha só.
        var events = ctx.Rules.Select(r => (Order: r.Site.Order, Item: (object)r))
            .Concat(ctx.Exits.Select(x => (Order: x.Site.Order, Item: (object)x)))
            .OrderBy(e => e.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare))
            .Select(e => e.Item)
            .ToList();
        var groupedRules = ctx.Rules.GroupBy(r => (Where(r.Site), r.Trigger.Key)).ToDictionary(g => g.First(), g => g.ToList());
        var groupedExits = ctx.Exits.GroupBy(x => (Where(x.Site), x.Trigger.Key)).ToDictionary(g => g.First(), g => g.ToList());

        foreach (var item in events)
        {
            switch (item)
            {
                case CodeRule rule when groupedRules.TryGetValue(rule, out var same):
                    leaves.Add(new TerminalOutcome
                    {
                        Key = $"{TerminalKinds.Rule}|{rule.Site.Method.DisplayName}|{rule.Kind}|{ScenarioKey.Code(rule.TriggerCode)}|{rule.Exception}",
                        Kind = TerminalKinds.Rule,
                        Label = label(rule),
                        Rules = same,
                        Source = rule.Site.Source,
                        Status = rule.Status,
                    });
                    break;
                case EarlyExit exit when groupedExits.TryGetValue(exit, out var sameExits):
                    leaves.Add(new TerminalOutcome
                    {
                        Key = $"{TerminalKinds.Exit}|{exit.Site.Method.DisplayName}|{ScenarioKey.Code(exit.TriggerCode)}",
                        Kind = TerminalKinds.Exit,
                        Label = exit.InEntry ? $"Encerra antes, sem erro, quando {exit.TriggerCode}"
                            : $"{exit.Site.Method.Node.MethodName} retorna antes, sem erro, quando {exit.TriggerCode}",
                        Exits = sameExits,
                        Source = exit.Site.Source,
                        Status = exit.Status ?? flow.SuccessStatus,
                    });
                    break;
            }
        }

        leaves.Add(new TerminalOutcome
        {
            Key = TerminalKinds.Success,
            Kind = TerminalKinds.Success,
            Label = "Resposta final: caminho principal (dados válidos, nenhuma regra bloqueia)",
            Status = flow.SuccessStatus,
        });
        foreach (var branch in ctx.Branches)
        {
            var atom = branch is NotPred n ? n.Inner : branch;
            var flipped = Pred.Not(branch);
            var value = flipped is not NotPred;
            leaves.Add(new TerminalOutcome
            {
                Key = $"{TerminalKinds.Success}|ramo|{ScenarioKey.Code(atom.Text)}={(value ? "true" : "false")}",
                Kind = TerminalKinds.Success,
                Label = $"Resposta final com {atom.Text} {(value ? "verdadeiro" : "falso")}",
                Flipped = flipped,
                Status = flow.SuccessStatus,
            });
        }

        return Discriminate(leaves);
    }

    /// <summary>Ocorrências com a mesma chave (pontos diferentes do código, mesma condição): #2, #3... na ordem do fluxo.</summary>
    private static List<TerminalOutcome> Discriminate(List<TerminalOutcome> leaves)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<TerminalOutcome>();
        foreach (var leaf in leaves)
        {
            var count = seen[leaf.Key] = seen.GetValueOrDefault(leaf.Key) + 1;
            result.Add(count == 1 ? leaf : new TerminalOutcome
            {
                Key = $"{leaf.Key}#{count}",
                Kind = leaf.Kind,
                Label = leaf.Label,
                Validation = leaf.Validation,
                Rules = leaf.Rules,
                Exits = leaf.Exits,
                Flipped = leaf.Flipped,
                BindingField = leaf.BindingField,
                Source = leaf.Source,
                Status = leaf.Status,
            });
        }
        return result;
    }

    /// <summary>Trecho do código (arquivo + posição): o mesmo método analisado a partir de duas chamadas tem o mesmo trecho.</summary>
    private static (string, int, int) Where(Site site) => (site.Node.SyntaxTree.FilePath, site.Node.SpanStart, site.Node.Span.End);

    private static int ValidationStatus(FlowModel flow) =>
        flow.ApiController ? 400 : flow.Rules.FirstOrDefault(r => r.IsModelStateCheck)?.Status ?? 400;
}
