using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Context.BusinessFlow;

/// <summary>
/// Fases 1 a 3 do fluxo de negócio por IA, sem IA: converte o call graph técnico (Roslyn) em nós e arestas,
/// classifica cada nó e extrai as evidências que a IA pode citar (condições, exceções, persistências,
/// consultas e integrações externas), sempre com a origem no código.
/// Abstrações genéricas (ServiceBase&lt;TEntity&gt;.ObterTodos, Repository&lt;T&gt;.Adicionar...) viram um único nó no
/// ponto em que a entidade as usa: o que executam por dentro não entra no fluxo, e o que acontece lá dentro
/// (persistência, exceção) aponta para esse uso.
/// </summary>
public static partial class BusinessFlowEvidenceBuilder
{
    private const int MaxUsageChars = 300;

    // Dentro de uma abstração só continua no fluxo o que tem comportamento próprio.
    private static readonly HashSet<string> KeptInsideAbstraction =
        [TechnicalNodeKinds.BusinessRule, TechnicalNodeKinds.Validation, TechnicalNodeKinds.Integration];

    private static readonly string[] WriteVerbs =
    [
        "Adicionar", "Inserir", "Incluir", "Cadastrar", "Criar", "Atualizar", "Alterar", "Editar", "Excluir", "Remover", "Deletar",
        "Salvar", "Gravar", "Add", "Insert", "Create", "Update", "Delete", "Remove", "Save", "Commit",
    ];

    /// <summary>Abstração no fluxo e onde está o código dela (o próprio método e o que ela chama por dentro).</summary>
    private sealed record AbstractionUse(CallGraphNode Info, HashSet<(string File, string Method)> Declarations);

    public static (CallGraphResult CallGraph, EvidenceCollection Evidence) Build(EndpointAnalysisContext context)
    {
        var graph = BuildCallGraph(context, out var nodes, out var abstractions);
        return (graph, BuildEvidence(context, nodes, abstractions));
    }

    /// <summary>N1 é a action; os demais seguem a ordem do grafo técnico (pré-ordem, na ordem das chamadas).</summary>
    private static CallGraphResult BuildCallGraph(EndpointAnalysisContext context,
        out List<(CallNode Node, CallGraphNode Info)> nodes, out List<AbstractionUse> abstractions)
    {
        var result = new CallGraphResult { Endpoint = context.Endpoint.Id };
        var collected = new List<(CallNode, CallGraphNode)>();
        var uses = new List<AbstractionUse>();
        var relevant = (context.BusinessGraph?.Flatten() ?? []).Where(n => n.Id != 0).Select(n => n.Id).ToHashSet();

        CallGraphNode Add(CallNode node, CallGraphNode info, string? parent)
        {
            info.Id = $"N{collected.Count + 1}";
            info.Method = $"{node.TypeName}.{node.MethodName}";
            info.Description = node.Description;
            info.Summary = node.Summary;
            info.Helpers = node.Helpers;
            info.Source = Location(info.File, info.Line);
            result.Nodes.Add(info);
            collected.Add((node, info));
            if (parent is not null)
                result.Edges.Add(new CallGraphEdge { From = parent, To = info.Id, Condition = node.Condition, ConditionIds = node.ConditionIds });
            return info;
        }

        void Walk(CallNode node, CallNode? caller, string? parent)
        {
            if (caller is not null && AbstractionBehind(node) is { } generic)
            {
                WalkAbstraction(node, generic, caller, parent);
                return;
            }

            var info = Add(node, new CallGraphNode
            {
                Kind = TechnicalNodeClassifier.Classify(node, caller is null),
                File = node.SourceFile,
                Line = node.SourceLine,
                Relevant = caller is null || relevant.Contains(node.Id),
            }, parent);
            foreach (var child in node.Children) Walk(child, node, info.Id);
        }

        // A abstração aparece no ponto de chamada, com a entidade e a instrução que a usa. node é o que o fluxo chama
        // (a própria abstração ou um wrapper que só repassa para ela); generic é o método genérico no fim da cadeia.
        void WalkAbstraction(CallNode node, CallNode generic, CallNode caller, string? parent)
        {
            var info = Add(node, new CallGraphNode
            {
                Kind = TechnicalNodeKinds.Abstraction,
                Entity = generic.GenericEntity,
                Via = $"{generic.GenericDeclaration}.{generic.MethodName}",
                Caller = $"{caller.TypeName}.{caller.MethodName}",
                Usage = UsageOf(context, node),
                File = node.CallFile ?? node.SourceFile,
                Line = node.CallFile is null ? node.SourceLine : node.CallLine,
                Relevant = relevant.Contains(node.Id),
            }, parent);

            var declarations = new HashSet<(string, string)> { (node.SourceFile, node.MethodName) };
            var hides = new List<string>();
            // Wrappers entre o chamador e a abstração: escondidos como ela.
            for (var link = node; link != generic; link = link.Children[0])
            {
                if (link != node) hides.Add($"{link.TypeName}.{link.MethodName}");
                declarations.Add((link.Children[0].SourceFile, link.Children[0].MethodName));
            }
            if (generic != node) hides.Add($"{generic.GenericDeclaration}.{generic.MethodName}");
            void Inside(CallNode child)
            {
                var kind = TechnicalNodeClassifier.Classify(child, false);
                if (child.GenericDeclaration is null && KeptInsideAbstraction.Contains(kind))
                {
                    Walk(child, node, info.Id);
                    return;
                }
                hides.Add(child.GenericDeclaration is null ? $"{child.TypeName}.{child.MethodName}" : $"{child.GenericDeclaration}.{child.MethodName}");
                declarations.Add((child.SourceFile, child.MethodName));
                foreach (var grandchild in child.Children) Inside(grandchild);
            }
            foreach (var child in generic.Children) Inside(child);

            info.Hides = hides.Count > 0 ? hides.Distinct().ToList() : null;
            uses.Add(new AbstractionUse(info, declarations));
        }

        if ((context.CallTree ?? context.BusinessGraph) is { } root) Walk(root, null, null);
        nodes = collected;
        abstractions = uses;
        return result;
    }

    private static EvidenceCollection BuildEvidence(EndpointAnalysisContext context, List<(CallNode Node, CallGraphNode Info)> nodes,
        List<AbstractionUse> abstractions)
    {
        var evidence = new EvidenceCollection();

        // Condições: o registro (C1, C2...) com a origem tirada das regras do grafo; regras fora do registro ganham R1, R2...
        var byExpression = context.Conditions.GroupBy(c => c.Expression).ToDictionary(g => g.Key, g => g.First());
        foreach (var (id, text) in context.ConditionRegistry)
        {
            byExpression.TryGetValue(text, out var info);
            var site = info is null ? SiteOf(context, id, nodes) : null;
            evidence.Conditions.Add(new ConditionEvidence
            {
                Id = id,
                Expression = text,
                Method = info?.Method ?? site?.Method ?? "",
                File = info?.SourceFile ?? site?.File ?? "",
                Line = info?.SourceLine ?? site?.Line ?? 0,
                Source = info is null ? Location(site?.File, site?.Line ?? 0) : Location(info.SourceFile, info.SourceLine),
            });
        }
        var registered = context.ConditionRegistry.Values.ToHashSet();
        foreach (var condition in context.Conditions.Where(c => !registered.Contains(c.Expression)).DistinctBy(c => c.Expression))
            evidence.Conditions.Add(new ConditionEvidence
            {
                Id = $"R{evidence.Conditions.Count(c => c.Id.StartsWith('R')) + 1}",
                Expression = condition.Expression,
                Method = condition.Method,
                File = condition.SourceFile,
                Line = condition.SourceLine,
                Source = Location(condition.SourceFile, condition.SourceLine),
            });

        // Exceções: status HTTP da matriz de cenários, quando ela foi gerada.
        var statusOf = (context.Scenarios?.Conditions ?? [])
            .Where(c => c.Rule is { Exception: not null, HttpStatus: not null })
            .GroupBy(c => c.Rule!.Exception!)
            .ToDictionary(g => g.Key, g => g.First().Rule!.HttpStatus);
        foreach (var effect in context.Effects.Where(e => e.Kind == SinkKinds.Throw))
            evidence.Exceptions.Add(With(new ExceptionEvidence
            {
                Id = $"E{evidence.Exceptions.Count + 1}",
                Type = effect.Target,
                Message = effect.Message,
                ConditionIds = effect.ConditionIds,
                HttpStatus = statusOf.GetValueOrDefault(effect.Target),
            }, effect.Source, abstractions));

        foreach (var change in context.EntityChanges.Where(c => c.EffectClass != EffectClasses.Infrastructure))
            evidence.Persistence.Add(With(new PersistenceEvidence
            {
                Id = $"P{evidence.Persistence.Count + 1}",
                Type = change.Operation,
                Entity = change.Entity,
                ConditionIds = change.ConditionIds,
            }, change.CreationSource ?? change.Source, abstractions, change.Entity));
        foreach (var point in context.PersistencePoints)
            evidence.Persistence.Add(With(new PersistenceEvidence
            {
                Id = $"P{evidence.Persistence.Count + 1}",
                Type = SinkKinds.SaveChanges,
                Entity = "banco de dados",
            }, point, abstractions));

        // Consultas: nós de acesso a dados e abstrações de leitura (pelo uso que o chamador faz delas).
        foreach (var (node, info) in nodes)
        {
            var abstractionRead = info.Kind == TechnicalNodeKinds.Abstraction && !IsWrite(node.MethodName);
            if (info.Kind != TechnicalNodeKinds.Query && !abstractionRead) continue;
            evidence.Queries.Add(new QueryEvidence
            {
                Id = $"Q{evidence.Queries.Count + 1}",
                NodeId = info.Id,
                Method = info.Caller ?? info.Method,
                Via = info.Via,
                Entity = info.Entity ?? EntityOf(node, context),
                Usage = info.Usage,
                Purpose = node.Description,
                File = info.File,
                Line = info.Line,
                Source = info.Source,
            });
        }

        foreach (var effect in context.Effects.Where(e => e.Kind is SinkKinds.Http or SinkKinds.Publish or SinkKinds.Send or SinkKinds.FileWrite))
            evidence.ExternalCalls.Add(With(new ExternalCallEvidence
            {
                Id = $"X{evidence.ExternalCalls.Count + 1}",
                Type = effect.Kind,
                Service = effect.Target,
            }, effect.Source, abstractions));
        // Nós de efeito externo que não viraram efeito (ex.: cliente de outro serviço chamado por método próprio).
        foreach (var (_, info) in nodes.Where(n => n.Info.Kind == TechnicalNodeKinds.Integration
                     && !evidence.ExternalCalls.Any(x => x.File == n.Info.File && x.Method == n.Info.Method)))
            evidence.ExternalCalls.Add(new ExternalCallEvidence
            {
                Id = $"X{evidence.ExternalCalls.Count + 1}",
                Type = "EXTERNAL",
                Service = info.Method[..Math.Max(0, info.Method.LastIndexOf('.'))],
                Method = info.Method,
                File = info.File,
                Line = info.Line,
                Source = info.Source,
            });

        return evidence;
    }

    /// <summary>Origem da evidência; código de dentro de uma abstração aponta para onde a entidade a usa (Via guarda a abstração).</summary>
    private static T With<T>(T evidence, SourceReference? source, List<AbstractionUse> abstractions, string? entity = null) where T : EvidenceBase
    {
        var method = source?.Method is { } m ? m[(m.LastIndexOf('.') + 1)..] : "";
        var use = source is null ? null : abstractions
            .Where(a => a.Declarations.Contains((source.File, method)))
            .OrderByDescending(a => entity is not null && a.Info.Entity == entity)
            .FirstOrDefault();
        if (use is not null)
        {
            evidence.Method = use.Info.Caller ?? use.Info.Method;
            evidence.Via = use.Info.Via;
            evidence.File = use.Info.File;
            evidence.Line = use.Info.Line;
            evidence.Source = use.Info.Source;
            return evidence;
        }

        evidence.Method = source?.Method ?? "";
        evidence.File = source?.File ?? "";
        evidence.Line = source?.Line ?? 0;
        evidence.Source = Location(source?.File, source?.Line ?? 0);
        return evidence;
    }

    /// <summary>Instrução do chamador que faz a chamada, tirada do código do método chamador (quando ele está no contexto).</summary>
    private static string? UsageOf(EndpointAnalysisContext context, CallNode node)
    {
        if (node.CallFile is null || node.CallLine == 0) return null;
        foreach (var snippet in context.Methods.Where(m => m.File == node.CallFile && m.Line <= node.CallLine))
        {
            var lines = snippet.Code.Split('\n');
            var from = node.CallLine - snippet.Line;
            if (from >= lines.Length) continue;
            var to = Math.Min(lines.Length - 1, Math.Max(from, (node.CallEndLine == 0 ? node.CallLine : node.CallEndLine) - snippet.Line));
            var text = Whitespace().Replace(string.Join(" ", lines[from..(to + 1)]), " ").Trim();
            return text.Length > MaxUsageChars ? text[..MaxUsageChars] + "…" : text;
        }
        return null;
    }

    /// <summary>
    /// Método genérico que o nó representa: ele mesmo (ServiceBase&lt;TEntity&gt;.ObterTodos) ou, para um wrapper que só
    /// repassa a chamada (CadastroVeiculoService.Adicionar(v) =&gt; repository.Adicionar(v)), o genérico no fim da cadeia
    /// de chamadas únicas com o mesmo nome. Nulo quando o nó não é abstração.
    /// </summary>
    private static CallNode? AbstractionBehind(CallNode node)
    {
        if (node.GenericDeclaration is not null) return node;
        var link = node;
        while (link.Children is [var only] && only.MethodName == node.MethodName
               && !KeptInsideAbstraction.Contains(TechnicalNodeClassifier.Classify(link, false)))
        {
            if (only.GenericDeclaration is not null) return only;
            link = only;
        }
        return null;
    }

    private static bool IsWrite(string method) => WriteVerbs.Any(v => method.StartsWith(v, StringComparison.Ordinal));

    /// <summary>Condição do registro sem regra correspondente: o nó que primeiro depende dela (chamada condicional).</summary>
    private static (string Method, string File, int Line)? SiteOf(EndpointAnalysisContext context, string id, List<(CallNode Node, CallGraphNode Info)> nodes)
    {
        var site = context.Scenarios?.Conditions.FirstOrDefault(c => c.RegistryId == id)?.Source;
        if (site is { File.Length: > 0 }) return (site.Method ?? "", site.File, site.Line);
        var node = nodes.FirstOrDefault(n => n.Node.ConditionIds?.Contains(id) == true).Node;
        return node is { CallFile: not null } ? ($"{node.TypeName}.{node.MethodName}", node.CallFile, node.CallLine) : null;
    }

    /// <summary>Entidade consultada: uma entidade alterada no fluxo cujo nome aparece no tipo ou no método.</summary>
    private static string? EntityOf(CallNode node, EndpointAnalysisContext context) =>
        context.EntityChanges.Select(c => c.Entity).Distinct()
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => node.TypeName.Contains(e, StringComparison.OrdinalIgnoreCase) || node.MethodName.Contains(e, StringComparison.OrdinalIgnoreCase));

    private static string Location(string? file, int line) =>
        string.IsNullOrEmpty(file) ? "" : $"{Path.GetFileName(file)}:{line}";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
