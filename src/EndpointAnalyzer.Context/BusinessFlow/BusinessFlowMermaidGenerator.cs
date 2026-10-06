using System.Text;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Context.BusinessFlow;

/// <summary>
/// Fase 6: fluxograma Mermaid gerado do <see cref="BusinessFlowResult"/> já validado (nunca do texto da IA).
/// O id de cada nó é o id do passo (B1, B2...), para o clique no fluxograma achar a rastreabilidade.
/// </summary>
public static class BusinessFlowMermaidGenerator
{
    private const int MaxCollapsedMethods = 12;

    private static readonly (string Class, string Style)[] Classes =
    [
        ("entrada", "fill:#eceff1,stroke:#455a64"),
        ("validacao", "fill:#fff3e0,stroke:#ef6c00"),
        ("regra", "fill:#e3f2fd,stroke:#1565c0"),
        ("consulta", "fill:#e8f5e9,stroke:#2e7d32"),
        ("transformacao", "fill:#e0f7fa,stroke:#00838f"),
        ("persistencia", "fill:#f3e5f5,stroke:#7b1fa2"),
        ("integracao", "fill:#ffebee,stroke:#c62828"),
        ("erro", "fill:#ffcdd2,stroke:#b71c1c"),
        ("resultado", "fill:#c8e6c9,stroke:#1b5e20"),
        ("colapsado", "fill:#f5f5f5,stroke:#9e9e9e,stroke-dasharray:4 3,color:#616161"),
        ("incerto", "stroke:#f9a825,stroke-width:2px,stroke-dasharray:6 3"),
        ("inconsistente", "stroke:#d50000,stroke-width:2px,stroke-dasharray:6 3"),
    ];

    /// <summary>As quatro combinações das opções de exibição (detalhes técnicos × nós colapsados).</summary>
    public static List<BusinessFlowDiagram> All(BusinessFlowResult result, CallGraphResult graph) =>
        [.. from details in new[] { false, true }
            from collapsed in new[] { false, true }
            select new BusinessFlowDiagram { ShowTechnicalDetails = details, ShowCollapsedNodes = collapsed, Mermaid = Generate(result, graph, details, collapsed) }];

    public static string Generate(BusinessFlowResult result, CallGraphResult graph, bool showTechnicalDetails, bool showCollapsedNodes)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.Id);
        var uncertain = result.UncertainSteps.Select(u => u.StepId).ToHashSet();
        var classes = new Dictionary<string, List<string>>();
        void Tag(string cls, string id) => (classes.TryGetValue(cls, out var list) ? list : classes[cls] = []).Add(id);

        var sb = new StringBuilder("flowchart TD\n");
        foreach (var step in result.Steps)
        {
            var label = Text(step.Description, step.Branches.Count > 0 ? 26 : 36);
            if (step.Inconsistencies is { Count: > 0 }) label = "⚠ " + label;
            if (uncertain.Contains(step.Id)) label += " (?)";
            if (showTechnicalDetails && step.Trace is { } trace)
            {
                var origin = string.Join(" · ", new[] { trace.Method, trace.File is null ? null : $"{Path.GetFileName(trace.File)}:{trace.Line}" }.Where(s => !string.IsNullOrEmpty(s)));
                if (origin.Length > 0) label += $"<br/><i>{Escape(origin)}</i>";
            }

            sb.AppendLine($"    {step.Id}{Shape(step, label)}");
            Tag(ClassOf(step), step.Id);
            if (step.Inconsistencies is { Count: > 0 }) Tag("inconsistente", step.Id);
            else if (uncertain.Contains(step.Id)) Tag("incerto", step.Id);
        }
        sb.AppendLine();

        foreach (var step in result.Steps)
        {
            if (step.Next.Length > 0) sb.AppendLine($"    {step.Id} --> {step.Next}");
            foreach (var branch in step.Branches)
                sb.AppendLine($"    {step.Id} -->|\"{Escape(branch.Condition)}\"| {branch.Target}");
        }

        if (showCollapsedNodes)
        {
            sb.AppendLine();
            var i = 0;
            foreach (var group in result.CollapsedNodes)
            {
                var id = $"K{++i}";
                // Abstração aparece como "ServiceBase<TEntity, TKey>.ObterTodos (Hierarquia)": o que foi usado e sobre qual entidade.
                var methods = group.NodeIds.Select(n => !nodes.TryGetValue(n, out var node) ? n
                    : node.Via is null ? node.Method : $"{node.Via} ({node.Entity ?? "?"})").ToList();
                var lines = methods.Take(MaxCollapsedMethods).Select(m => Escape(m)).ToList();
                if (methods.Count > MaxCollapsedMethods) lines.Add($"+{methods.Count - MaxCollapsedMethods}");
                var title = group.StepId.Length > 0 ? "Agrupou:" : "Removidos do fluxo:";
                sb.AppendLine($"    {id}[\"<b>{title}</b><br/>{string.Join("<br/>", lines)}\"]");
                if (group.StepId.Length > 0) sb.AppendLine($"    {group.StepId} -.- {id}");
                Tag("colapsado", id);
            }
        }

        sb.AppendLine();
        foreach (var (cls, style) in Classes.Where(c => classes.ContainsKey(c.Class)))
        {
            sb.AppendLine($"    classDef {cls} {style}");
            sb.AppendLine($"    class {string.Join(",", classes[cls])} {cls}");
        }
        return sb.ToString();
    }

    /// <summary>Decisão em losango; início, fim e erro em terminal; persistência em cilindro; integração em sub-rotina.</summary>
    private static string Shape(BusinessFlowStep step, string label) => step switch
    {
        { Branches.Count: > 0 } => $"{{\"{label}\"}}",
        { Type: BusinessFlowStepTypes.Entry or BusinessFlowStepTypes.Result or BusinessFlowStepTypes.Error } => $"([\"{label}\"])",
        { Type: BusinessFlowStepTypes.Persistence } => $"[(\"{label}\")]",
        { Type: BusinessFlowStepTypes.ExternalIntegration } => $"[[\"{label}\"]]",
        _ => $"[\"{label}\"]",
    };

    private static string ClassOf(BusinessFlowStep step) => step.Type switch
    {
        BusinessFlowStepTypes.Entry => "entrada",
        BusinessFlowStepTypes.Validation => "validacao",
        BusinessFlowStepTypes.Query => "consulta",
        BusinessFlowStepTypes.Transformation => "transformacao",
        BusinessFlowStepTypes.Persistence => "persistencia",
        BusinessFlowStepTypes.ExternalIntegration => "integracao",
        BusinessFlowStepTypes.Error => "erro",
        BusinessFlowStepTypes.Result => "resultado",
        _ => "regra",
    };

    /// <summary>Quebra em linhas curtas (o losango cresce com a largura) e escapa para rótulo entre aspas.</summary>
    private static string Text(string text, int max)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > max)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join("<br/>", lines.Select(Escape));
    }

    /// <summary>Entidades HTML dentro de rótulos entre aspas; "|" usa a entidade do próprio Mermaid.</summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("|", "#124;");
}
