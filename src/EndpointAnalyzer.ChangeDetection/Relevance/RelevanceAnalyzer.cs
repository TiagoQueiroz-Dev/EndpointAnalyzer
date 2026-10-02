using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;

namespace EndpointAnalyzer.ChangeDetection.Relevance;

public sealed class RelevanceResult
{
    public required CallNode BusinessGraph { get; init; }

    /// <summary>Métodos exibidos no grafo de negócio.</summary>
    public required HashSet<AnalyzedMethod> BusinessMethods { get; init; }

    /// <summary>Métodos do recorte (negócio + detalhes de consulta/persistência abaixo deles).</summary>
    public required HashSet<AnalyzedMethod> SliceMethods { get; init; }

    public required List<Effect> Effects { get; init; }

    public required EntityChangeAnalysis Changes { get; init; }

    public required List<ConditionInfo> Conditions { get; init; }

    public required Dictionary<string, string> ConditionRegistry { get; init; }

    public required AnalysisStats Stats { get; init; }
}

public interface IRelevanceAnalyzer
{
    RelevanceResult Analyze(CallGraph graph, IReadOnlyList<ConditionInfo> conditions, EntityChangeAnalysis changes);
}

/// <summary>
/// Remove o ruído da análise: classifica cada nó (BUSINESS, VALIDATION, DATA_ACCESS, PERSISTENCE,
/// EXTERNAL_EFFECT, INFRASTRUCTURE, UTILITY), colapsa infraestrutura, esconde helpers e recorta
/// (de trás para frente, a partir dos sinks) o grafo de negócio — o que realmente influencia
/// as regras, decisões e efeitos observáveis do endpoint.
/// </summary>
public class RelevanceAnalyzer(AnalyzerOptions analyzerOptions) : IRelevanceAnalyzer
{
    private readonly RelevanceOptions _options = analyzerOptions.Relevance;

    private sealed class NodeInfo
    {
        public required CallNode Node { get; init; }
        public NodeInfo? Parent { get; init; }
        public AnalyzedMethod? Method { get; init; }
        public List<Effect> Effects { get; init; } = [];
        public List<RawEntityChange> EntityChanges { get; init; } = [];
        public List<NodeInfo> Children { get; } = [];
        public string Category { get; set; } = NodeCategories.Unknown;
        public bool InInfrastructure { get; set; }
        public bool SubtreeDomainError { get; set; }
        public Effect? SubtreeBusinessEvent { get; set; }
        public bool IsRoot => Parent is null;
    }

    public RelevanceResult Analyze(CallGraph graph, IReadOnlyList<ConditionInfo> conditions, EntityChangeAnalysis changes)
    {
        var sinks = new EffectSinkDetector(_options).Detect(graph, changes);
        var methodsByNode = graph.Methods.ToDictionary(m => m.Node);
        var changesByMethod = changes.Raw.GroupBy(r => r.Method).ToDictionary(g => g.Key, g => g.ToList());

        // 1. Informações por nó do grafo técnico.
        var root = BuildInfo(graph.Root, null, methodsByNode, sinks, changesByMethod);
        var all = Flatten(root).ToList();

        // 2. Classificação (§13) de cima para baixo e marcas de subárvore de baixo para cima.
        foreach (var info in all) info.Category = Classify(info, changes.Catalog);
        MarkInfrastructure(root, inherited: false);
        MarkSubtree(root);

        foreach (var info in all) Score(info);

        // 3. Grafo de negócio: colapsa infraestrutura, esconde helpers, recorte a partir dos sinks (§12, §14, §20).
        var builder = new BusinessGraphBuilder(this);
        var business = builder.Build(root) ?? Clone(root.Node, []);

        var businessMethods = builder.Kept.Select(i => i.Method).OfType<AnalyzedMethod>().ToHashSet();
        var sliceMethods = builder.Slice.Select(i => i.Method).OfType<AnalyzedMethod>().ToHashSet();

        // 4. Efeitos e alterações só do recorte; o que acontece dentro da infraestrutura é efeito INFRASTRUCTURE.
        var infraMethods = all.Where(i => i.InInfrastructure).Select(i => i.Method).OfType<AnalyzedMethod>().ToHashSet();
        var filteredChanges = changes.Filter(
            r => sliceMethods.Contains(r.Method),
            r => infraMethods.Contains(r.Method) || RelevanceOptions.StartsWithAny(r.Entity, _options.InfrastructureEntityPrefixes)
                ? EffectClasses.Infrastructure
                : EffectClasses.Primary);

        // A mesma alteração aparece no service (Adicionar) e no repositório (DbSet.Add): um efeito só, o de mais alto nível.
        var effects = sinks
            .Where(s => sliceMethods.Contains(s.Key))
            .OrderBy(s => s.Key.Depth)
            .SelectMany(s => s.Value)
            .Where(e => e.Class != EffectClasses.Infrastructure && !infraMethods.Any(m => m.DisplayName == e.Source?.Method))
            .GroupBy(e => e.Kind is SinkKinds.Insert or SinkKinds.Update or SinkKinds.Delete or SinkKinds.SaveChanges
                ? (e.Kind, e.Target, (string?)null, 0)
                : (e.Kind, e.Target, e.Source?.File, e.Source?.Line ?? 0))
            .Select(g => g.First())
            .ToList();

        // Erros de domínio notificados (NotifyError) dentro da infraestrutura colapsada viram efeito do chamador.
        effects.AddRange(builder.DomainErrorCalls);

        var businessConditions = conditions
            .Where(c => c.Kind == ConditionKinds.Validation || businessMethods.Any(m => Contains(m, c)))
            .ToList();

        // 5. Registro de condições (§11): C1, C2... em vez de concatenar.
        var registry = new ConditionRegistry();
        foreach (var node in business.Flatten()) node.ConditionIds = registry.Register(node.Condition);
        foreach (var change in filteredChanges.Changes)
        {
            change.ConditionIds = registry.Register(change.Condition);
            foreach (var property in change.PropertyChanges) property.ConditionIds = registry.Register(property.Condition);
        }
        foreach (var effect in effects) effect.ConditionIds = registry.Register(effect.Condition);

        var stats = new AnalysisStats
        {
            TechnicalNodes = graph.Root.Flatten().Count(),
            BusinessNodes = business.Flatten().Count(),
            PrunedBranches = graph.Methods.Sum(m => m.Reachability.PrunedBranches),
            AmbiguousCalls = graph.Root.Flatten().Count(n => n.Ambiguous),
            CollapsedInfrastructure = all.Count(i => i.Category == NodeCategories.Infrastructure),
            HiddenHelpers = all.Count(i => i.Category == NodeCategories.Utility),
            TechnicalConditions = conditions.Count,
            BusinessConditions = businessConditions.Count,
        };

        return new RelevanceResult
        {
            BusinessGraph = business,
            BusinessMethods = businessMethods,
            SliceMethods = sliceMethods,
            Effects = effects,
            Changes = filteredChanges,
            Conditions = businessConditions,
            ConditionRegistry = registry.Entries,
            Stats = stats,
        };
    }

    private static bool Contains(AnalyzedMethod method, ConditionInfo condition)
    {
        if (method.SourceFile != condition.SourceFile) return false;
        var span = method.Declaration.GetLocation().GetLineSpan();
        return condition.SourceLine >= span.StartLinePosition.Line + 1 && condition.SourceLine <= span.EndLinePosition.Line + 1;
    }

    private static NodeInfo BuildInfo(CallNode node, NodeInfo? parent, Dictionary<CallNode, AnalyzedMethod> methods,
        Dictionary<AnalyzedMethod, List<Effect>> sinks, Dictionary<AnalyzedMethod, List<RawEntityChange>> changes)
    {
        var method = methods.GetValueOrDefault(node);
        var info = new NodeInfo
        {
            Node = node,
            Parent = parent,
            Method = method,
            Effects = method is null ? [] : sinks.GetValueOrDefault(method) ?? [],
            EntityChanges = method is null ? [] : changes.GetValueOrDefault(method) ?? [],
        };
        foreach (var child in node.Children)
            info.Children.Add(BuildInfo(child, info, methods, sinks, changes));
        return info;
    }

    private static IEnumerable<NodeInfo> Flatten(NodeInfo info) =>
        info.Children.SelectMany(Flatten).Prepend(info);

    // ---- Classificação (§13) ----

    private string Classify(NodeInfo info, EntityCatalog? catalog)
    {
        if (info.IsRoot) return NodeCategories.Business;

        var node = info.Node;
        var declaring = info.Method?.Symbol.ContainingType;
        var names = new[] { declaring?.Name ?? node.DeclaringType ?? node.TypeName, node.TypeName }.Distinct().ToArray();
        var name = node.MethodName == "ctor" ? names[0] : node.MethodName;
        var plain = name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name;

        bool TypeEnds(IEnumerable<string> suffixes) => names.Any(n => RelevanceOptions.EndsWithAny(n, suffixes));

        // Infraestrutura: barramento, notificações, log, ApiController, eventos de erro de domínio.
        if (TypeEnds(_options.InfrastructureTypeSuffixes) || RelevanceOptions.StartsWithAny(name, _options.InfrastructureMethodPrefixes))
            return NodeCategories.Infrastructure;
        if (node.MethodName == "ctor" && declaring is not null && IsDomainErrorType(declaring))
            return NodeCategories.Infrastructure;

        if (_options.UtilityMethods.Contains(name) || TypeEnds(_options.UtilityTypeSuffixes)
            || RelevanceOptions.StartsWithAny(name, _options.UtilityMethodPrefixes) || declaring is { IsStatic: true })
            return NodeCategories.Utility;

        // Persistência: grava (SaveChanges, Add/Update/Remove) ou é um verbo de persistência do service/repositório.
        // Application services, controllers e handlers orquestram o fluxo: não são persistência mesmo chamando Adicionar.
        // Método com regra própria (lança exceção) é negócio, mesmo chamado Atualizar/Adicionar.
        var orchestration = TypeEnds(["ApplicationService", "AppService", "Controller", "Handler", "UseCase"]);
        var hasOwnRules = info.Effects.Any(e => e.Kind == SinkKinds.Throw);
        if (!orchestration && !hasOwnRules
            && (info.Effects.Any(e => e.Kind == SinkKinds.SaveChanges) || info.EntityChanges.Any(r => r.Direct)
                || (_options.PersistenceMethodPrefixes.Any(p => plain.Equals(p, StringComparison.OrdinalIgnoreCase)) && TypeEnds(_options.PersistenceTypeSuffixes))
                || (TypeEnds(["Repository", "Repositorio", "UnitOfWork", "Uow", "Context"]) && RelevanceOptions.StartsWithAny(name, _options.PersistenceMethodPrefixes))))
            return NodeCategories.Persistence;

        if (!orchestration && info.Effects.Any(e => e.Kind is SinkKinds.Http or SinkKinds.FileWrite || (e.Kind is SinkKinds.Publish or SinkKinds.Send && e.EventKind == EventKinds.BusinessEvent)))
            return NodeCategories.ExternalEffect;

        if (TypeEnds(_options.ValidationTypeSuffixes) || RelevanceOptions.StartsWithAny(name, _options.ValidationMethodPrefixes)
            || RelevanceOptions.ContainsAny(name, _options.ValidationMethodContains))
            return NodeCategories.Validation;

        if (!orchestration && TypeEnds(_options.DataAccessTypeSuffixes)
            || RelevanceOptions.StartsWithAny(name, _options.DataAccessMethodPrefixes)
            || RelevanceOptions.EndsWithAny(plain, _options.DataAccessMethodSuffixes))
            return NodeCategories.DataAccess;

        if (TypeEnds(_options.BusinessTypeSuffixes) || (declaring is not null && catalog?.IsEntity(declaring) == true)
            || info.Effects.Any(e => e.Kind == SinkKinds.Throw) || info.EntityChanges.Count > 0)
            return NodeCategories.Business;

        return NodeCategories.Unknown;
    }

    private bool IsDomainErrorType(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (RelevanceOptions.ContainsAny(t.Name, _options.DomainErrorEventContains)) return true;
        return false;
    }

    private static void MarkInfrastructure(NodeInfo info, bool inherited)
    {
        info.InInfrastructure = inherited || info.Category == NodeCategories.Infrastructure;
        foreach (var child in info.Children) MarkInfrastructure(child, info.InInfrastructure);
    }

    private static void MarkSubtree(NodeInfo info)
    {
        foreach (var child in info.Children) MarkSubtree(child);

        // Evento publicado nesta chamada: bus.PublicarEvento(new VeiculoCriadoEvent(...)) é detectado no chamador.
        var callEvents = CallSiteEffects(info).Where(e => e.EventKind is not null).ToList();

        info.SubtreeDomainError = info.Effects.Any(e => e.EventKind == EventKinds.DomainError)
            || callEvents.Any(e => e.EventKind == EventKinds.DomainError)
            || info.Children.Any(c => c.SubtreeDomainError)
            || (info.Node.MethodName == "ctor" && info.Category == NodeCategories.Infrastructure);
        info.SubtreeBusinessEvent = info.Effects.FirstOrDefault(e => e.EventKind == EventKinds.BusinessEvent)
            ?? callEvents.FirstOrDefault(e => e.EventKind == EventKinds.BusinessEvent)
            ?? info.Children.Select(c => c.SubtreeBusinessEvent).FirstOrDefault(e => e is not null);
    }

    /// <summary>Efeitos do chamador detectados exatamente na linha desta chamada.</summary>
    private static IEnumerable<Effect> CallSiteEffects(NodeInfo info)
    {
        if (info.Parent?.Method is not { } caller) return [];
        var site = caller.CallSites.FirstOrDefault(cs => cs.Node == info.Node);
        if (site is null) return [];
        var line = site.Syntax.StartLine();
        return info.Parent.Effects.Where(e => e.Source?.Line == line);
    }

    // ---- Score de relevância (§24) ----

    private void Score(NodeInfo info)
    {
        var score = 0;
        var reasons = new List<string>();
        void Add(int points, string reason) { score += points; reasons.Add($"{(points > 0 ? "+" : "")}{points} {reason}"); }

        var entities = info.EntityChanges.Where(c => !info.InInfrastructure).Select(c => $"{c.Entity} {c.Operation}").Distinct().ToList();
        if (entities.Count > 0) Add(100, $"altera entidade ({string.Join(", ", entities)})");
        if (info.Effects.Any(e => e.Kind == SinkKinds.SaveChanges)) Add(100, "persiste alterações");
        if (info.Category is NodeCategories.Business or NodeCategories.Validation && HasRules(info)) Add(90, "representa regra de negócio");
        if (info.Effects.Any(e => e.Kind == SinkKinds.Throw && e.Class == EffectClasses.Primary)) Add(80, "gera exceção de domínio");
        if (info.IsRoot || info.Node.Usage?.Contains(UsageKinds.Return) == true) Add(80, "influencia a resposta");
        if (info.Category == NodeCategories.DataAccess && IsInfluential(info)) Add(70, $"consulta usada em decisão ({string.Join(", ", info.Node.Usage!)})");
        if (info.SubtreeBusinessEvent is not null && !info.InInfrastructure) Add(70, $"publica evento de negócio ({info.SubtreeBusinessEvent.Target})");
        if (info.Category == NodeCategories.Persistence && entities.Count == 0) Add(60, "operação de persistência");
        if (info.Category == NodeCategories.Validation) Add(60, "validação");
        if (info.SubtreeDomainError && info.Category == NodeCategories.Infrastructure) reasons.Add("notifica erro de domínio (DOMAIN_ERROR não expande consumidores)");

        if (info.Category == NodeCategories.Infrastructure) Add(-30, "infraestrutura");
        if (RelevanceOptions.StartsWithAny(info.Node.MethodName, ["Log"])) Add(-50, "logging");
        if (info.Category == NodeCategories.Utility) Add(-60, "helper genérico");
        if (info.Node.Ambiguous) reasons.Add($"resolução ambígua: {string.Join(", ", info.Node.Candidates ?? [])}");
        if (info.Node.Resolution is { } resolution && resolution != ResolutionStrategies.Direct) reasons.Add($"resolvido por {resolution}");

        info.Node.Category = info.Category;
        info.Node.Score = score;
        info.Node.Reasons = reasons.Count == 0 ? null : reasons;
    }

    private static bool IsInfluential(NodeInfo info) =>
        info.Node.Usage is { } usage && usage.Any(UsageKinds.Influential.Contains);

    /// <summary>O método decide algo: lança exceção, notifica erro de domínio ou altera entidade.</summary>
    private static bool HasRules(NodeInfo info) =>
        info.Effects.Any(e => e.Kind == SinkKinds.Throw)
        || info.EntityChanges.Count > 0
        || info.Children.Any(c => c.Category == NodeCategories.Infrastructure && c.SubtreeDomainError);

    private static CallNode Clone(CallNode node, List<CallNode> children) => new()
    {
        Id = node.Id,
        MethodName = node.MethodName,
        TypeName = node.TypeName,
        DeclaringType = node.DeclaringType,
        SourceFile = node.SourceFile,
        SourceLine = node.SourceLine,
        Description = node.Description,
        CallFile = node.CallFile,
        CallLine = node.CallLine,
        CallEndLine = node.CallEndLine,
        Condition = node.Condition,
        Control = node.Control,
        ResolvedFrom = node.ResolvedFrom,
        Receiver = node.Receiver,
        Resolution = node.Resolution,
        Ambiguous = node.Ambiguous,
        Candidates = node.Candidates,
        AlreadyVisited = node.AlreadyVisited,
        DepthLimitReached = node.DepthLimitReached,
        Category = node.Category,
        Score = node.Score,
        Reasons = node.Reasons,
        Usage = node.Usage,
        Children = children,
    };

    // ---- Grafo de negócio (§12, §14, §20, §21, §26) ----

    private sealed class BusinessGraphBuilder(RelevanceAnalyzer owner)
    {
        private readonly HashSet<string> _keptNames = [];

        public List<NodeInfo> Kept { get; } = [];

        /// <summary>Nós do recorte: negócio + detalhes de consulta/persistência abaixo deles.</summary>
        public List<NodeInfo> Slice { get; } = [];

        public List<Effect> DomainErrorCalls { get; } = [];

        public CallNode? Build(NodeInfo info) => Visit(info, insideDataOrPersistence: false);

        private CallNode? Visit(NodeInfo info, bool insideDataOrPersistence)
        {
            var node = info.Node;

            if (!info.IsRoot)
            {
                switch (info.Category)
                {
                    // Infraestrutura vira um nó-resumo (ou some), com o mecanismo disponível só no grafo técnico (§14).
                    case NodeCategories.Infrastructure:
                        return Collapse(info);

                    // Helpers saem do grafo, mas ficam registrados no pai (§26).
                    case NodeCategories.Utility:
                        return null;

                    // Repositório por baixo de um service de consulta/persistência é detalhe técnico.
                    case NodeCategories.DataAccess or NodeCategories.Persistence when insideDataOrPersistence:
                        AddSlice(info);
                        return null;
                }
            }

            var isDataOrPersistence = info.Category is NodeCategories.DataAccess or NodeCategories.Persistence;
            var children = new List<CallNode>();
            var helpers = new List<string>();

            foreach (var child in info.Children)
            {
                var built = Visit(child, isDataOrPersistence);
                if (built is not null)
                {
                    // Nós-resumo repetidos lado a lado viram um só.
                    if (built.Summary is not null && children.LastOrDefault() is { Summary: { } last } previous
                        && last == built.Summary && previous.Condition == built.Condition)
                    {
                        previous.CollapsedCount += built.CollapsedCount;
                        continue;
                    }
                    children.Add(built);
                }
                else if (child.Category == NodeCategories.Utility || (!child.Node.AlreadyVisited && child.Method is not null && IsInfluential(child)
                         && child.Category is not (NodeCategories.Infrastructure or NodeCategories.DataAccess or NodeCategories.Persistence)))
                {
                    if (!owner._options.UtilityMethods.Contains(child.Node.MethodName) && child.Node.MethodName != "ctor")
                        helpers.Add(child.Node.FullName);
                }
            }

            var keep = info.IsRoot
                || info.Category is NodeCategories.Persistence or NodeCategories.ExternalEffect or NodeCategories.Validation
                || (info.Category == NodeCategories.DataAccess && (IsInfluential(info) || info.Node.Usage is null))
                || HasRules(info)
                || children.Count > 0
                || (node.AlreadyVisited && _keptNames.Contains(node.FullName));

            if (!keep) return null;

            _keptNames.Add(node.FullName);
            Kept.Add(info);
            AddSlice(info, includeSelfOnly: true);
            if (isDataOrPersistence)
                foreach (var descendant in info.Children.SelectMany(Flatten).Where(d => !d.InInfrastructure))
                    AddSlice(descendant, includeSelfOnly: true);

            var copy = Clone(node, children);
            copy.Helpers = helpers.Count == 0 ? null : helpers.Distinct().ToList();
            return copy;
        }

        /// <summary>
        /// ApiController.ObterErrosModel → MostrarErrosModel → NotifyError → InMemoryBus.PublicarEvento → ...
        /// vira "Retorna erro de validação".
        /// </summary>
        private CallNode? Collapse(NodeInfo info)
        {
            var name = info.Node.MethodName;
            string? summary = null;

            if (RelevanceOptions.ContainsAny(name, ["Response", "Resposta"]))
                summary = "Monta a resposta da API (sucesso ou erros de validação)";
            else if (info.SubtreeDomainError || RelevanceOptions.ContainsAny(name, ["Erro", "Error", "Problema", "Notif"]))
                summary = info.Parent?.IsRoot == true ? "Retorna erro de validação" : "Notifica erro de validação";
            else if (info.SubtreeBusinessEvent is { } businessEvent)
                summary = $"Publica evento {businessEvent.Target}";

            if (summary is null) return null;

            if (summary.StartsWith("Notifica", StringComparison.Ordinal) && info.Parent?.Method is { } caller)
                DomainErrorCalls.Add(new Effect
                {
                    Kind = SinkKinds.Publish,
                    EventKind = EventKinds.DomainError,
                    Class = EffectClasses.Primary,
                    Target = "erro de validação",
                    Description = $"notifica erro de validação ({info.Node.FullName})",
                    Condition = SyntaxConditions.Combine(caller.PathCondition, info.Node.Condition),
                    Source = new SourceReference { File = caller.SourceFile, Method = caller.DisplayName, Line = info.Node.SourceLine },
                });

            return new CallNode
            {
                Id = info.Node.Id,
                Summary = summary,
                MethodName = info.Node.MethodName,
                TypeName = info.Node.TypeName,
                DeclaringType = info.Node.DeclaringType,
                SourceFile = info.Node.SourceFile,
                SourceLine = info.Node.SourceLine,
                Condition = info.Node.Condition,
                Control = info.Node.Control,
                CallFile = info.Node.CallFile,
                CallLine = info.Node.CallLine,
                CallEndLine = info.Node.CallEndLine,
                Category = NodeCategories.Infrastructure,
                CollapsedCount = Flatten(info).Count(),
                Reasons = [$"infraestrutura colapsada: {info.Node.FullName} (+{Flatten(info).Count() - 1} chamadas internas)"],
            };
        }

        private void AddSlice(NodeInfo info, bool includeSelfOnly = false)
        {
            if (!Slice.Contains(info)) Slice.Add(info);
        }
    }
}

/// <summary>
/// Registro de condições (§11): cada condição atômica recebe um id (C1, C2...) e as alterações referenciam os ids.
/// </summary>
public sealed class ConditionRegistry
{
    private readonly Dictionary<string, string> _byText = [];

    public Dictionary<string, string> Entries { get; } = [];

    public List<string>? Register(string? condition)
    {
        var parts = SyntaxConditions.SplitConjunction(condition);
        if (parts.Count == 0) return null;

        var ids = new List<string>();
        foreach (var part in parts)
        {
            if (!_byText.TryGetValue(part, out var id))
            {
                id = $"C{Entries.Count + 1}";
                _byText[part] = id;
                Entries[id] = part;
            }
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }
}
