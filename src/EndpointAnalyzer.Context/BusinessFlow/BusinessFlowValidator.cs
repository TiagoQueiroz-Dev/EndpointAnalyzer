using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Context.BusinessFlow;

/// <summary>
/// Fase 5: confere a resposta da IA contra as evidências da análise estática. A IA pode interpretar e resumir,
/// mas não pode criar comportamento: passo sem evidência válida sai do fluxo (quem apontava para ele segue para o
/// passo seguinte), ids inexistentes são descartados, ramos para passos inexistentes caem e persistência sem
/// evidência de persistência fica marcada como inconsistente. Também preenche a rastreabilidade de cada passo.
/// </summary>
public static partial class BusinessFlowValidator
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial System.Text.RegularExpressions.Regex StepId();

    [System.Text.RegularExpressions.GeneratedRegex("^K[0-9]+$")]
    private static partial System.Text.RegularExpressions.Regex CollapsedId();

    private static readonly HashSet<string> NoiseKinds =
        [TechnicalNodeKinds.Abstraction, TechnicalNodeKinds.Infrastructure, TechnicalNodeKinds.Helper, TechnicalNodeKinds.Mapping];

    private static readonly HashSet<string> EndSteps = [BusinessFlowStepTypes.Entry, BusinessFlowStepTypes.Result, BusinessFlowStepTypes.Error];

    public static List<string> Validate(BusinessFlowResult result, CallGraphResult graph, EvidenceCollection evidence)
    {
        var issues = new List<string>();
        var known = Index(graph, evidence);

        // Id do passo vira id do nó no Mermaid: só letras, dígitos e _, sem repetir e sem colidir com as evidências.
        var stepIds = new HashSet<string>();
        foreach (var step in result.Steps.ToList())
        {
            // K1, K2... são os grupos de nós colapsados no fluxograma.
            if (StepId().IsMatch(step.Id) && !CollapsedId().IsMatch(step.Id) && !known.ContainsKey(step.Id) && stepIds.Add(step.Id)) continue;
            result.Steps.Remove(step);
            issues.Add($"Passo \"{step.Description}\" descartado: id \"{step.Id}\" inválido ou repetido.");
        }

        // Ids de evidência inexistentes saem; passo que fica sem nenhuma evidência é removido.
        var removed = new Dictionary<string, BusinessFlowStep>();
        foreach (var step in result.Steps)
        {
            var invalid = step.EvidenceIds.Where(id => !known.ContainsKey(id)).ToList();
            if (invalid.Count > 0)
            {
                issues.Add($"{step.Id}: evidência(s) inexistente(s) descartada(s): {string.Join(", ", invalid)}.");
                step.EvidenceIds = step.EvidenceIds.Where(known.ContainsKey).Distinct().ToList();
            }
            if (step.EvidenceIds.Count == 0)
            {
                removed[step.Id] = step;
                issues.Add($"{step.Id} removido: \"{step.Description}\" não tem evidência na análise estática.");
            }
            // Passo que só se apoia em abstração, infraestrutura, helper ou mapeamento é ruído técnico, não etapa de negócio.
            else if (step.Branches.Count == 0 && !EndSteps.Contains(step.Type) && step.EvidenceIds.All(id => known[id] is CallGraphNode { Kind: var kind } && NoiseKinds.Contains(kind)))
            {
                removed[step.Id] = step;
                issues.Add($"{step.Id} removido: \"{step.Description}\" só cita abstrações ou infraestrutura ({string.Join(", ", step.EvidenceIds)}).");
            }
        }
        result.Steps.RemoveAll(s => removed.ContainsKey(s.Id));

        // Ligações para passos removidos seguem para o próximo passo dele; para passos inexistentes, caem.
        var ids = result.Steps.Select(s => s.Id).ToHashSet();
        string Resolve(string target)
        {
            var seen = new HashSet<string>();
            while (removed.TryGetValue(target, out var gone) && seen.Add(target))
                target = gone.Next is { Length: > 0 } next ? next : gone.Branches.FirstOrDefault()?.Target ?? "";
            return target;
        }
        foreach (var step in result.Steps)
        {
            if (step.Next.Length > 0)
            {
                var next = Resolve(step.Next);
                if (!ids.Contains(next))
                {
                    issues.Add($"{step.Id}: próximo passo {step.Next} não existe.");
                    next = "";
                }
                step.Next = next;
            }
            foreach (var branch in step.Branches)
            {
                var target = Resolve(branch.Target);
                if (!ids.Contains(target)) issues.Add($"{step.Id}: o ramo \"{branch.Condition}\" aponta para {branch.Target}, que não existe.");
                branch.Target = ids.Contains(target) ? target : "";
            }
            step.Branches.RemoveAll(b => b.Target.Length == 0);
        }

        // Persistência citada precisa existir na análise estática.
        foreach (var step in result.Steps.Where(s => s.Type == BusinessFlowStepTypes.Persistence && !s.EvidenceIds.Any(id => known[id] is PersistenceEvidence)))
            Inconsistent(step, "Persistência sem evidência de INSERT/UPDATE/DELETE/SaveChanges na análise estática.", issues);
        foreach (var step in result.Steps.Where(s => s.Type == BusinessFlowStepTypes.ExternalIntegration && !s.EvidenceIds.Any(id => known[id] is ExternalCallEvidence)))
            Inconsistent(step, "Integração externa sem evidência de chamada externa na análise estática.", issues);

        // O que precisa continuar visível: persistências e integrações que nenhum passo cita.
        var cited = result.Steps.SelectMany(s => s.EvidenceIds).ToHashSet();
        foreach (var p in evidence.Persistence.Where(p => !cited.Contains(p.Id)))
            issues.Add($"{p.Id} ({p.Type} {p.Entity}, {p.Source}) não aparece no fluxo de negócio.");
        foreach (var x in evidence.ExternalCalls.Where(x => !cited.Contains(x.Id)))
            issues.Add($"{x.Id} ({x.Type} {x.Service}, {x.Source}) não aparece no fluxo de negócio.");

        // Agrupamentos e incertezas só valem para passos e nós que existem.
        result.CollapsedNodes.RemoveAll(c => c.StepId.Length > 0 && !ids.Contains(c.StepId) && !removed.ContainsKey(c.StepId));
        foreach (var group in result.CollapsedNodes)
        {
            if (removed.ContainsKey(group.StepId)) group.StepId = "";
            group.NodeIds = group.NodeIds.Where(id => known.GetValueOrDefault(id) is CallGraphNode).Distinct().ToList();
        }
        result.CollapsedNodes.RemoveAll(c => c.NodeIds.Count == 0);
        result.UncertainSteps.RemoveAll(u => !ids.Contains(u.StepId));

        foreach (var step in result.Steps) step.Trace = TraceOf(step, known);
        return issues;
    }

    /// <summary>Todos os ids citáveis: nós (N*) e evidências (C*, R*, E*, P*, Q*, X*).</summary>
    public static Dictionary<string, object> Index(CallGraphResult graph, EvidenceCollection evidence)
    {
        var index = new Dictionary<string, object>();
        foreach (var node in graph.Nodes) index[node.Id] = node;
        foreach (var item in evidence.Conditions.Cast<EvidenceBase>().Concat(evidence.Exceptions).Concat(evidence.Persistence)
                     .Concat(evidence.Queries).Concat(evidence.ExternalCalls))
            index[item.Id] = item;
        return index;
    }

    private static void Inconsistent(BusinessFlowStep step, string reason, List<string> issues)
    {
        (step.Inconsistencies ??= []).Add(reason);
        issues.Add($"{step.Id} inconsistente: {reason}");
    }

    /// <summary>Origem do passo: a evidência mais específica (condição, exceção, persistência, consulta, integração) e depois o nó.</summary>
    private static BusinessFlowTrace? TraceOf(BusinessFlowStep step, Dictionary<string, object> known)
    {
        var items = step.EvidenceIds.Select(id => known[id]).ToList();
        var condition = items.OfType<ConditionEvidence>().FirstOrDefault();
        var located = items.OfType<EvidenceBase>().OrderBy(e => e switch
            {
                ConditionEvidence => 0, ExceptionEvidence => 1, PersistenceEvidence => 2, QueryEvidence => 3, _ => 4,
            })
            .FirstOrDefault(e => e.File.Length > 0);
        var node = items.OfType<CallGraphNode>().FirstOrDefault(n => n.File.Length > 0);

        if (located is null && node is null && condition is null) return null;
        return new BusinessFlowTrace
        {
            Method = located?.Method is { Length: > 0 } method ? method : node?.Caller ?? node?.Method,
            Condition = condition?.Expression,
            File = located?.File ?? node?.File,
            Line = located?.Line ?? node?.Line ?? 0,
        };
    }
}
