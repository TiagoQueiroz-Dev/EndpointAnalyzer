using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Fluxo do endpoint já resolvido (regras, saídas, validações e efeitos) que alimenta os caminhos e os desfechos.</summary>
internal sealed record FlowContext(
    string Endpoint,
    InputModel Input,
    VarTable Vars,
    FlowModel Flow,
    IReadOnlyList<ValidationRule> Validations,
    Pred ValidationsOk,
    IReadOnlyList<CodeRule> Rules,
    IReadOnlyList<EarlyExit> Exits,
    InputField? MismatchField,
    int MaxPaths);

/// <summary>
/// Pontos reais de encerramento e desfechos distintos do endpoint.
/// <list type="number">
/// <item><see cref="Points"/>: onde a requisição termina — cada validação de entrada (400 antes da action), o tipo do JSON
/// incompatível, cada regra que encerra com erro (o mesmo trecho alcançado por dois caminhos com a mesma condição é um
/// ponto só), cada return antecipado do endpoint e a resposta final. Returns de métodos internos não encerram nada: o
/// valor deles é propagado ao chamador (<see cref="SymbolicResolver"/>) e o caminho segue.</item>
/// <item><see cref="Group"/>: os caminhos explorados (<see cref="ExecutionPathExplorer"/>) agrupados por ponto e, nas
/// respostas de sucesso, pelo conjunto de efeitos executados. Caminhos que chegam ao mesmo ponto com os mesmos efeitos
/// são o mesmo desfecho.</item>
/// </list>
/// </summary>
internal static class TerminalOutcomeCollector
{
    public static List<TerminalPoint> Points(FlowContext ctx, Func<CodeRule, string> label)
    {
        var points = new List<TerminalPoint>();
        var flow = ctx.Flow;

        foreach (var v in ctx.Validations)
            points.Add(new TerminalPoint
            {
                Key = $"{TerminalKinds.Validation}|{v.Field.Path}|{v.Kind}|{v.Origin}|{ScenarioKey.Code(v.WhenCode ?? v.When?.Text)}",
                Kind = TerminalKinds.Validation,
                Label = $"{v.FieldName}: {v.Text}{(v.When is null ? "" : $" quando {v.WhenCode ?? v.When.Text}")}",
                Validation = v,
                Source = v.Source,
                Status = ValidationStatus(flow),
            });

        if (ctx.MismatchField is { } field)
            points.Add(new TerminalPoint
            {
                Key = $"{TerminalKinds.Binding}|{field.Path}",
                Kind = TerminalKinds.Binding,
                Label = $"{field.Name}: tipo do JSON incompatível com {field.TypeDisplay} (model binding)",
                BindingField = field,
                Status = ValidationStatus(flow),
            });

        // Regras e returns do endpoint na ordem de execução; ocorrências do mesmo trecho com a mesma condição viram um ponto só.
        var events = ctx.Rules.Select(r => (Order: r.Site.Order, Item: (object)r))
            .Concat(ctx.Exits.Where(x => x.InEntry).Select(x => (Order: x.Site.Order, Item: (object)x)))
            .OrderBy(e => e.Order, Comparer<IReadOnlyList<int>>.Create(FlowModel.Compare))
            .Select(e => e.Item)
            .ToList();
        var groupedRules = ctx.Rules.GroupBy(r => (Where(r.Site), r.Trigger.Key)).ToDictionary(g => g.First(), g => g.ToList());

        foreach (var item in events)
        {
            switch (item)
            {
                case CodeRule rule when groupedRules.TryGetValue(rule, out var same):
                    points.Add(new TerminalPoint
                    {
                        Key = $"{TerminalKinds.Rule}|{rule.Site.Method.DisplayName}|{rule.Kind}|{ScenarioKey.Code(rule.TriggerCode)}|{rule.Exception}",
                        Kind = TerminalKinds.Rule,
                        Label = label(rule),
                        Rules = same,
                        Source = rule.Site.Source,
                        Status = rule.Status,
                    });
                    break;
                case EarlyExit exit:
                    points.Add(new TerminalPoint
                    {
                        Key = $"{TerminalKinds.Exit}|{exit.Site.Method.DisplayName}|{ScenarioKey.Code(exit.TriggerCode)}",
                        Kind = TerminalKinds.Exit,
                        Label = $"Encerra antes, sem erro, quando {exit.TriggerCode}",
                        Exit = exit,
                        Source = exit.Site.Source,
                        Status = exit.Status ?? flow.SuccessStatus,
                    });
                    break;
            }
        }

        points.Add(new TerminalPoint
        {
            Key = TerminalKinds.Success,
            Kind = TerminalKinds.Success,
            Label = "Resposta final do endpoint",
            Status = flow.SuccessStatus,
        });
        return Discriminate(points);
    }

    /// <summary>
    /// Um desfecho por ponto de encerramento; nos de sucesso, um por conjunto de efeitos. O conjunto com mais efeitos é o
    /// desfecho principal do ponto (chave = a do ponto); os outros levam na chave o que falta/sobra em relação a ele.
    /// </summary>
    public static List<TerminalOutcome> Group(IReadOnlyList<TerminalPoint> points, IReadOnlyList<ExecutionPath> paths)
    {
        var outcomes = new List<TerminalOutcome>();
        foreach (var point in points)
        {
            var own = paths.Where(p => p.Terminal == point).ToList();
            if (!point.Success || own.Count == 0)
            {
                var outcome = new TerminalOutcome { Key = point.Key, Point = point, Label = point.Label, Effects = own.Count > 0 ? Signature(own[0].Effects, false) : [] };
                outcome.Paths.AddRange(own);
                outcomes.Add(outcome);
                continue;
            }

            var groups = own.GroupBy(p => string.Join("\n", Signature(p.Effects, true))).Select(g => g.ToList()).ToList();
            var primary = groups.OrderByDescending(g => Signature(g[0].Effects, true).Count).First();
            var main = Signature(primary[0].Effects, true);
            foreach (var group in groups)
            {
                var effects = Signature(group[0].Effects, true);
                var missing = main.Except(effects).ToList();
                var extra = effects.Except(main).ToList();
                var variant = group != primary;
                var outcome = new TerminalOutcome
                {
                    Key = variant ? $"{point.Key}|efeitos:{string.Join(",", missing.Select(m => "-" + m).Concat(extra.Select(e => "+" + e)))}" : point.Key,
                    Point = point,
                    Label = variant ? $"{point.Label} {Describe(missing, extra, effects.Count)}" : point.Label,
                    Effects = effects,
                    Missing = missing,
                    Extra = extra,
                    IsVariant = variant,
                };
                // O caminho mais simples primeiro (menos decisões).
                outcome.Paths.AddRange(group.OrderBy(p => p.Decisive.Count));
                outcomes.Add(outcome);
            }
        }
        return outcomes;
    }

    /// <summary>"sem ItemRemessa INSERT", "sem efeitos", "com Veiculo UPDATE".</summary>
    public static string Describe(IReadOnlyList<string> missing, IReadOnlyList<string> extra, int effects)
    {
        if (effects == 0 && missing.Count > 0) return "sem efeitos (nada é gravado)";
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"sem {string.Join(", ", missing)}");
        if (extra.Count > 0) parts.Add($"com {string.Join(", ", extra)}");
        return parts.Count == 0 ? "" : string.Join(" e ", parts);
    }

    /// <summary>
    /// Efeitos observáveis de um caminho: entidade + operação (alterações no contexto só contam no sucesso ou quando o
    /// SaveChanges executa), eventos, envios e o próprio SaveChanges. Campos alterados não distinguem desfechos.
    /// </summary>
    public static IReadOnlyList<string> Signature(IEnumerable<EffectSite> effects, bool success)
    {
        var list = effects.ToList();
        var persisted = list.Any(e => e.Kind == SinkKinds.SaveChanges);
        return list.Where(e => e.Change is null || success || persisted)
            .Select(e => e.Change is { } change ? $"{change.Entity} {change.Operation}" : e.Description)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Ocorrências com a mesma chave (pontos diferentes do código, mesma condição): #2, #3... na ordem do fluxo.</summary>
    private static List<TerminalPoint> Discriminate(List<TerminalPoint> points)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<TerminalPoint>();
        foreach (var point in points)
        {
            var count = seen[point.Key] = seen.GetValueOrDefault(point.Key) + 1;
            result.Add(count == 1 ? point : new TerminalPoint
            {
                Key = $"{point.Key}#{count}",
                Kind = point.Kind,
                Label = point.Label,
                Validation = point.Validation,
                Rules = point.Rules,
                Exit = point.Exit,
                BindingField = point.BindingField,
                Source = point.Source,
                Status = point.Status,
            });
        }
        return result;
    }

    /// <summary>Trecho do código (arquivo + posição): o mesmo método analisado a partir de duas chamadas tem o mesmo trecho.</summary>
    private static (string, int, int) Where(Site site) => (site.Node.SyntaxTree.FilePath, site.Node.SpanStart, site.Node.Span.End);

    private static int ValidationStatus(FlowModel flow) =>
        flow.ApiController ? 400 : flow.Rules.FirstOrDefault(r => r.IsModelStateCheck)?.Status ?? 400;
}
