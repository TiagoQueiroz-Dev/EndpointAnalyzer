using System.Text;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Gera a saída legível: o resumo da análise estática e a documentação Markdown do endpoint.
/// </summary>
public static class ReportRenderer
{
    /// <summary>
    /// Análise estática sem IA. view: "negocio" (padrão, grafo de negócio), "tecnico" (grafo resolvido,
    /// infraestrutura colapsada) ou "completo" (call graph bruto).
    /// </summary>
    public static string StaticSummary(EndpointAnalysisContext context, string view = "negocio")
    {
        var sb = new StringBuilder();
        sb.AppendLine(context.Endpoint.Id);
        sb.AppendLine();

        var tree = view switch
        {
            "tecnico" or "completo" => context.CallTree,
            _ => context.BusinessGraph ?? context.CallTree,
        };
        if (tree is not null)
            AppendTree(sb, tree, 0, collapseInfrastructure: view == "tecnico");

        var s = context.Stats;
        sb.AppendLine();
        sb.AppendLine($"Grafo de negócio: {s.BusinessNodes} nó(s) | técnico: {s.TechnicalNodes} | branches impossíveis removidos: {s.PrunedBranches} | " +
                      $"infraestrutura colapsada: {s.CollapsedInfrastructure} | helpers ocultos: {s.HiddenHelpers} | chamadas ambíguas: {s.AmbiguousCalls}");

        if (context.ConditionRegistry.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Condições:");
            foreach (var (id, text) in context.ConditionRegistry)
                sb.AppendLine($"  {id,-4} {text}");
        }

        sb.AppendLine();
        sb.AppendLine($"Regras ({context.Conditions.Count} de {s.TechnicalConditions} encontradas no grafo técnico):");
        sb.AppendLine();
        foreach (var condition in context.Conditions)
        {
            sb.AppendLine($"  [{condition.Kind}] {condition.Expression}");
            sb.AppendLine($"      → {condition.Action}{(condition.Message is null ? "" : $" \"{condition.Message}\"")}  ({condition.SourceFile}:{condition.SourceLine})");
        }

        sb.AppendLine();
        sb.AppendLine("Efeitos:");
        foreach (var effect in context.Effects.Where(e => e.Kind is not (SinkKinds.Throw or SinkKinds.Return)))
            sb.AppendLine($"  [{effect.Class}] {effect.Description}{Ids(effect.ConditionIds)}  ({effect.Source})");

        sb.AppendLine();
        sb.AppendLine("Changes:");
        foreach (var change in context.EntityChanges)
        {
            sb.AppendLine();
            sb.AppendLine($"  {change.Entity}");
            sb.AppendLine($"  {change.Operation}{(change.EffectClass == EffectClasses.Primary ? "" : $" ({change.EffectClass})")}{Ids(change.ConditionIds)}");
            foreach (var property in change.PropertyChanges)
                sb.AppendLine($"    {change.Entity}.{property.Property} = {property.Value}{Ids(property.ConditionIds)}");
        }

        if (context.PersistencePoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Persistência (SaveChanges):");
            foreach (var point in context.PersistencePoints)
                sb.AppendLine($"  {point.Method} ({point.File}:{point.Line})");
        }

        return sb.ToString();
    }

    private static string Ids(List<string>? ids) => ids is { Count: > 0 } ? $"   [{string.Join(", ", ids)}]" : "";

    /// <summary>Documentação final do endpoint em Markdown (usa a resposta da IA quando existir).</summary>
    public static string Markdown(EndpointAnalysisReport report)
    {
        var context = report.Context;
        var ai = report.Ai;
        var sb = new StringBuilder();

        sb.AppendLine($"# {context.Endpoint.Id}");
        sb.AppendLine();
        sb.AppendLine($"`{context.Endpoint.Controller}.{context.Endpoint.Action}` — `{context.Endpoint.SourceFile}:{context.Endpoint.SourceLine}`");
        sb.AppendLine();

        if (ai is not null)
        {
            sb.AppendLine("## Objetivo");
            sb.AppendLine();
            sb.AppendLine(ai.Summary);
            sb.AppendLine();

            AppendRules(sb, "Regras de negócio", ai.BusinessRules);
            AppendRules(sb, "Validações", ai.Validations);

            sb.AppendLine("## Entidades alteradas");
            sb.AppendLine();
            foreach (var change in ai.EntityChanges)
            {
                sb.AppendLine($"### {change.Entity} — {change.Operation}");
                sb.AppendLine();
                sb.AppendLine("| Campo | Condição |");
                sb.AppendLine("|---|---|");
                foreach (var property in change.Properties)
                    sb.AppendLine($"| {property.Name} | {(string.IsNullOrWhiteSpace(property.Condition) ? "sempre" : Escape(property.Condition))} |");
                sb.AppendLine();
            }

            if (ai.Uncertainties.Count > 0)
            {
                sb.AppendLine("## Pontos não determinados");
                sb.AppendLine();
                foreach (var item in ai.Uncertainties)
                    sb.AppendLine($"- {item}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("> Análise estática apenas (sem IA).");
            sb.AppendLine();
            sb.AppendLine("```text");
            sb.Append(StaticSummary(context));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (context.Scenarios is { } matrix)
            AppendScenarios(sb, matrix, ai?.Scenarios);

        sb.AppendLine("## Fluxo");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.AppendLine(string.Join("\n→ ", context.CallGraph));
        sb.AppendLine("```");
        sb.AppendLine();

        var v = report.Version;
        sb.AppendLine("---");
        sb.AppendLine($"_Analisador {v.AnalyzerVersion} · {v.Date:yyyy-MM-dd HH:mm} UTC" +
                      (v.Commit is null ? "" : $" · commit {v.Commit} ({v.Branch})") +
                      (v.AiModel is null ? "" : $" · modelo {v.AiModel}") +
                      (report.FromCache ? " · cache" : "") + "_");

        return sb.ToString();
    }

    /// <summary>Matriz de cenários: payload + pré-condições + resultado esperado (título da IA quando houver).</summary>
    private static void AppendScenarios(StringBuilder sb, ScenarioMatrix matrix, List<ScenarioLabel>? labels)
    {
        sb.AppendLine("## Matriz de cenários");
        sb.AppendLine();
        if (matrix.Scenarios.Count == 0)
        {
            sb.AppendLine("_Nenhum cenário gerado._");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("| Id | Cenário | Payload | Pré-condições | Resultado esperado |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var s in matrix.Scenarios)
            {
                var label = labels?.FirstOrDefault(l => l.Id == s.Id);
                var payload = $"`{s.Request.Method} {s.Request.Url}`" + (s.Request.Body is { } body ? $"<br>`{body.ToJsonString()}`" : "");
                var preconditions = s.Preconditions.Count == 0 ? "—" : string.Join("<br>", s.Preconditions.Select(p => p.Description));
                var expected = $"**{s.Expected.HttpStatus?.ToString() ?? "?"}** {s.Expected.Outcome}"
                    + string.Concat(s.Expected.Messages.Select(m => $"<br>\"{m}\""))
                    + string.Concat(s.Expected.Effects.Select(e => $"<br>{e.Description}{(e.Properties is { Count: > 0 } p ? $" ({string.Join(", ", p)})" : "")}"))
                    + (s.Expected.Outcome == "erro" && !s.Expected.Persisted ? "<br>nada é gravado" : "");
                sb.AppendLine($"| {s.Id} | {Escape(label?.Title ?? s.Title)} <br>_{s.Kind} · {s.Technique}_ | {Escape(payload)} | {Escape(preconditions)} | {Escape(expected)} |");
            }
            sb.AppendLine();
        }

        if (matrix.Notes.Count > 0)
        {
            foreach (var note in matrix.Notes) sb.AppendLine($"> {note}");
            sb.AppendLine();
        }
    }

    private static void AppendRules(StringBuilder sb, string title, List<BusinessRule> rules)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        if (rules.Count == 0)
        {
            sb.AppendLine("_Nenhuma encontrada._");
            sb.AppendLine();
            return;
        }

        foreach (var rule in rules)
        {
            sb.AppendLine($"### {rule.Id}");
            sb.AppendLine();
            sb.AppendLine(rule.Description);
            sb.AppendLine();
            sb.AppendLine($"Confiança: {rule.Confidence:0.00}");
            if (rule.Evidence is { } e)
            {
                sb.AppendLine();
                sb.AppendLine($"Evidência: `{e.File}:{e.Line}` — método `{e.Method}`");
                if (!string.IsNullOrWhiteSpace(e.Code))
                {
                    sb.AppendLine();
                    sb.AppendLine("```csharp");
                    sb.AppendLine(e.Code.TrimEnd());
                    sb.AppendLine("```");
                }
            }
            sb.AppendLine();
        }
    }

    private static void AppendTree(StringBuilder sb, CallNode node, int depth, bool collapseInfrastructure = false)
    {
        var prefix = depth == 0 ? "" : new string(' ', (depth - 1) * 2) + "→ ";
        var notes = new List<string>();
        if (node.Category is { } category && depth > 0) notes.Add(category);
        if (node.ResolvedFrom is not null) notes.Add($"via {node.ResolvedFrom}");
        if (node.ConditionIds is { Count: > 0 } ids) notes.Add($"se {string.Join(" ∧ ", ids)}");
        else if (node.Condition is not null) notes.Add($"se {node.Condition}");
        if (node.Ambiguous) notes.Add($"resolução ambígua: {string.Join(", ", node.Candidates ?? [])}");
        if (node.AlreadyVisited) notes.Add("já visitado");
        if (node.DepthLimitReached) notes.Add("limite de profundidade");
        if (node.Helpers is { Count: > 0 } helpers) notes.Add($"usa {string.Join(", ", helpers)}");

        var collapsed = collapseInfrastructure && node.Category == NodeCategories.Infrastructure;
        if (collapsed) notes.Add($"+{node.Flatten().Count() - 1} chamadas de infraestrutura");

        sb.AppendLine($"{prefix}{node.FullName}{(notes.Count == 0 ? "" : $"   [{string.Join("; ", notes)}]")}");
        if (collapsed) return;
        foreach (var child in node.Children)
            AppendTree(sb, child, depth + 1, collapseInfrastructure);
    }

    private static string Escape(string text) => text.Replace("|", "\\|");
}
