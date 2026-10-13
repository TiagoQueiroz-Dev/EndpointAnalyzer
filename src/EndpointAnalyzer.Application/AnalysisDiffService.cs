using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;
using static EndpointAnalyzer.Application.DiffText;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Diff entre duas versões salvas da análise do mesmo endpoint, a partir dos relatórios estruturados: sem IA, sem executar
/// o endpoint e sem diff textual do JSON. Cada regra, validação, efeito e cenário é projetado numa estrutura canônica
/// (sem CEN-xx, Dx, Cx, linhas nem ordem), pareado por correspondência semântica (<see cref="DiffMatcher"/>) e comparado
/// campo a campo com <see cref="JsonNode.DeepEquals"/>.
/// </summary>
public sealed partial class AnalysisDiffService
{
    private const string SummaryTab = "summary";
    private const string ScenariosTab = "scenarios";

    /// <param name="previous">Versão base; nula na primeira análise do endpoint.</param>
    /// <exception cref="ArgumentException">As versões não são do mesmo endpoint, projeto e solução.</exception>
    public AnalysisDiffResult Compare(StoredAnalysis? previous, StoredAnalysis current)
    {
        var result = new AnalysisDiffResult { AnalysisId = current.Id, Current = Version(current) };
        if (previous is null)
        {
            result.Message = "Nenhuma análise anterior para comparar.";
            return result;
        }
        if (!SameEndpoint(previous, current))
            throw new ArgumentException("As análises comparadas não são do mesmo endpoint, projeto e solução.");

        result.HasBase = true;
        result.Base = Version(previous);
        result.Notes.AddRange(Notes(previous, current));
        result.Sections.Add(Summary(previous.Report, current.Report));
        result.Sections.Add(Scenarios(previous.Report, current.Report));
        foreach (var section in result.Sections) result.Counts.Add(section.Counts);
        result.HasDifferences = result.Counts.Total > 0;
        result.Message = Message(result);
        return result;
    }

    private static bool SameEndpoint(StoredAnalysis a, StoredAnalysis b) =>
        string.Equals(a.SolutionPath, b.SolutionPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Method, b.Method, StringComparison.OrdinalIgnoreCase)
        && a.Route == b.Route && a.Project == b.Project;

    private static AnalysisDiffVersion Version(StoredAnalysis analysis) => new()
    {
        AnalysisId = analysis.Id,
        CreatedAt = analysis.CreatedAt,
        UpdatedAt = analysis.UpdatedAt,
        Commit = analysis.Report.Version.Commit,
        Branch = analysis.Report.Version.Branch,
        AnalyzerVersion = analysis.Report.Version.AnalyzerVersion,
        AiProvider = analysis.Report.Version.AiProvider,
        AiModel = analysis.Report.Version.AiModel,
        Sections = [.. analysis.Report.Sections],
        HasAi = analysis.Report.Ai is not null,
        RuntimeStatus = analysis.Report.Runtime?.Status,
    };

    private static IEnumerable<string> Notes(StoredAnalysis previous, StoredAnalysis current)
    {
        var (a, b) = (previous.Report.Version, current.Report.Version);
        if (previous.CreatedAt > current.CreatedAt)
            yield return "A versão base é mais recente que a atual: \"adicionado\" e \"removido\" seguem a ordem escolhida.";
        if (a.AnalyzerVersion != b.AnalyzerVersion)
            yield return $"Versões do analisador diferentes ({a.AnalyzerVersion} → {b.AnalyzerVersion}): parte das diferenças pode vir do próprio analisador.";
        if (previous.Fingerprint == current.Fingerprint && previous.Fingerprint != "importado")
            yield return "Mesmo código-fonte nas duas versões: as diferenças vêm da IA, dos dados de teste ou do ambiente.";
        if (previous.Report.Ai is not null && current.Report.Ai is not null && (a.AiProvider != b.AiProvider || a.AiModel != b.AiModel))
            yield return $"IA diferente ({a.AiModel ?? a.AiProvider} → {b.AiModel ?? b.AiProvider}): diferenças de documentação podem ser só de redação.";
    }

    private static string Message(AnalysisDiffResult result)
    {
        var c = result.Counts;
        var partial = result.Sections.Any(s => !s.Comparable || s.Groups.Any(g => !g.Comparable));
        if (!result.HasDifferences)
            return (partial ? "Nenhuma diferença encontrada nas partes comparáveis." : "Nenhuma diferença encontrada.")
                + (c.Minor > 0 ? $" {c.Minor} item(ns) variou(aram) só em dados de teste, origem no código ou ambiente." : "");
        var parts = new[] { (c.Added, "adicionado(s)"), (c.Removed, "removido(s)"), (c.Changed, "alterado(s)"), (c.Uncertain, "incerto(s)") }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
        return $"{c.Total} diferença(s): {string.Join(", ", parts)}.";
    }

    // ---- Aba Resumo ----

    private static AnalysisDiffSection Summary(EndpointAnalysisReport previous, EndpointAnalysisReport current)
    {
        var section = new AnalysisDiffSection { Id = SummaryTab, Title = "Resumo" };
        if (MissingTab(previous, current, SummaryTab, "Resumo") is { } reason) return NotComparable(section, reason);

        // Regras e validações: da matriz (estruturadas) quando as duas versões têm; senão, das condições do código.
        var (pm, cm) = (previous.Context.Scenarios, current.Context.Scenarios);
        if (pm is not null && cm is not null)
        {
            section.Groups.Add(Group("validacoes", "Validações", Conditions(pm, "validacao"), Conditions(cm, "validacao"),
                "Fonte: validações estruturadas da matriz (DataAnnotations, FluentValidation, implícitas)."));
            section.Groups.Add(Group("regras", "Regras de negócio", Conditions(pm, "regra"), Conditions(cm, "regra"),
                "Fonte: regras estruturadas da matriz (gatilho, mensagem e como interrompem a operação)."));
        }
        else
        {
            section.Groups.Add(Group("validacoes", "Validações", CodeConditions(previous.Context, true), CodeConditions(current.Context, true),
                "Fonte: condições encontradas no código (Context.Conditions)."));
            section.Groups.Add(Group("regras", "Regras de negócio", CodeConditions(previous.Context, false), CodeConditions(current.Context, false),
                "Fonte: condições encontradas no código (Context.Conditions)."));
        }
        section.Groups.Add(Group("efeitos", "Efeitos", Effects(previous.Context), Effects(current.Context)));
        section.Groups.Add(Group("entidades", "Entidades alteradas", Entities(previous.Context), Entities(current.Context)));

        var (pa, ca) = (HasAiSummary(previous), HasAiSummary(current));
        if (pa && ca)
            section.Groups.Add(Group("documentacao", "Documentação da IA", Documentation(previous.Ai!), Documentation(current.Ai!),
                "Texto escrito pela IA (objetivo, títulos, contexto e mensagens): separado das mudanças de comportamento acima."));
        else if (pa || ca)
            section.Groups.Add(NotComparable(new AnalysisDiffGroup { Id = "documentacao", Title = "Documentação da IA" },
                $"Resumo sem IA na versão {(pa ? "atual" : "anterior")}: a documentação da IA não é comparável (nada foi removido)."));
        return Total(section);
    }

    private static bool HasAiSummary(EndpointAnalysisReport report) => report.Ai is { } ai
        && (!string.IsNullOrWhiteSpace(ai.Summary) || ai.BusinessRules.Count > 0 || ai.Validations.Count > 0 || ai.EntityChanges.Count > 0);

    private static List<DiffProjection> Conditions(ScenarioMatrix matrix, string kind) =>
        matrix.Conditions.Where(c => c.Kind == kind).Select((c, i) => kind == "validacao" ? Validation(c, i) : Rule(c, i)).ToList();

    private static DiffProjection Validation(DecisionCondition c, int position)
    {
        var v = c.Validation;
        var field = v is null ? null : string.IsNullOrEmpty(v.Owner) ? v.Field : $"{v.Owner}.{v.Field}";
        var p = new DiffProjection(c.Label ?? c.Expression, c.Id, position, DiffImpacts.Behavior) { Source = c };
        // Sem a validação estruturada, os três primeiros níveis ficam vazios: os níveis seguintes continuam alinhados.
        if (v is not null)
            p.Key($"val|{field}|{v.Kind}|{Code(v.When)}|{v.Min}|{v.Max}").Key($"val|{field}|{v.Kind}|{Code(v.When)}").Key($"val|{field}|{v.Kind}");
        else p.Key(null).Key(null).Key(null);
        p.Key($"val|{Code(c.Expression)}").Key($"val|{Mask(Code(c.Expression))}")
            .Key(string.IsNullOrWhiteSpace(c.Label) ? null : $"val|msg|{Text(c.Label)}")
            .SupportKey(v is null ? null : $"val|{v.Field}");
        p.Add("expressao", "Expressão", DiffImpacts.Behavior, S(Code(c.Expression)), S(c.Expression))
            .Add("mensagem", "Mensagem", DiffImpacts.Behavior, S(Text(c.Label)));
        if (v is not null)
            p.Add("campo", "Campo", DiffImpacts.Behavior, S(field))
                .Add("tipo", "Validação", DiffImpacts.Behavior, S(v.Kind))
                .Add("minimo", "Mínimo", DiffImpacts.Behavior, S(v.Min))
                .Add("maximo", "Máximo", DiffImpacts.Behavior, S(v.Max))
                .Add("colecao", "Lista", DiffImpacts.Behavior, Flag(v.Collection))
                .Add("opcional", "Só quando informado", DiffImpacts.Behavior, Flag(v.Optional))
                .Add("quando", "Quando", DiffImpacts.Behavior, S(Code(v.When)), S(v.When))
                .Add("origemValidacao", "Origem da validação", DiffImpacts.Origin, S(v.Origin));
        return p.Add("origem", "Origem no código", DiffImpacts.Origin, S(Origin(c.Source)));
    }

    private static DiffProjection Rule(DecisionCondition c, int position)
    {
        var r = c.Rule;
        return new DiffProjection(c.Label ?? c.Expression, c.Id, position, DiffImpacts.Behavior) { Source = c }
            .Key($"regra|{Code(c.Expression)}").Key($"regra|{Mask(Code(c.Expression))}")
            .Key(string.IsNullOrWhiteSpace(c.Label) ? null : $"regra|msg|{Text(c.Label)}")
            .SupportKey(c.Source?.Method is null ? null : $"regra|src|{c.Source.Method}|{r?.Kind}|{r?.Exception}")
            .Add("expressao", "Condição", DiffImpacts.Behavior, S(Code(c.Expression)), S(c.Expression))
            .Add("mensagem", "Mensagem", DiffImpacts.Behavior, S(Text(c.Label)))
            .Add("acao", "Interrompe com", DiffImpacts.Behavior, S(r?.Kind))
            .Add("excecao", "Exceção", DiffImpacts.Behavior, S(r?.Exception))
            .Add("http", "HTTP", DiffImpacts.Behavior, N(r?.HttpStatus))
            .Add("origem", "Origem no código", DiffImpacts.Origin, S(Origin(c.Source)));
    }

    /// <summary>Sem a matriz nas duas versões: condições que bloqueiam (throw, return, notificação) e validações do código.</summary>
    private static List<DiffProjection> CodeConditions(EndpointAnalysisContext context, bool validations) =>
        context.Conditions.Where(c => (c.Kind == ConditionKinds.Validation) == validations
                && (validations || Regex.IsMatch(c.Action, "^(throw|return|notifica)")))
            .Select((c, i) => new DiffProjection(c.Message ?? c.Expression, null, i, DiffImpacts.Behavior) { Source = c }
                .Key($"cond|{c.Kind}|{Code(c.Expression)}|{c.Method}").Key($"cond|{c.Kind}|{Code(c.Expression)}")
                .Key($"cond|{c.Kind}|{Mask(Code(c.Expression))}|{c.Method}")
                .Key(string.IsNullOrWhiteSpace(c.Message) ? null : $"cond|{c.Kind}|msg|{Text(c.Message)}")
                .SupportKey($"cond|{c.Kind}|src|{c.Method}|{Text(c.Action)}")
                .Add("tipo", "Tipo", DiffImpacts.Behavior, S(c.Kind))
                .Add("expressao", "Condição", DiffImpacts.Behavior, S(Code(c.Expression)), S(c.Expression))
                .Add("acao", "Ação", DiffImpacts.Behavior, S(Text(c.Action)))
                .Add("mensagem", "Mensagem", DiffImpacts.Behavior, S(Text(c.Message)))
                .Add("origem", "Origem no código", DiffImpacts.Origin, S(Origin(c.SourceFile, c.Method))))
            .ToList();

    /// <summary>Condições (C1, C2...) pela expressão: o registro é renumerado a cada análise.</summary>
    private static (JsonNode? Value, JsonNode? Display) ConditionSet(IEnumerable<string>? ids, Dictionary<string, string> registry)
    {
        var expressions = (ids ?? []).Select(id => registry.GetValueOrDefault(id) ?? id).ToList();
        return (Set(expressions.Select(Code)), Set(expressions));
    }

    /// <summary>Sem THROW/RETURN (regras) e INSERT/UPDATE/DELETE (já comparados em "Entidades alteradas").</summary>
    private static List<DiffProjection> Effects(EndpointAnalysisContext context) =>
        context.Effects.Where(e => e.Kind is not (SinkKinds.Throw or SinkKinds.Return or SinkKinds.Insert or SinkKinds.Update or SinkKinds.Delete))
            .Select((e, i) =>
        {
            var (conditions, display) = ConditionSet(e.ConditionIds, context.ConditionRegistry);
            return new DiffProjection(e.Description, null, i, DiffImpacts.Behavior) { Source = e }
                .Key($"efeito|{e.Kind}|{e.Target}|{e.EventKind}|{conditions?.ToJsonString()}").Key($"efeito|{e.Kind}|{e.Target}|{e.EventKind}")
                .Key($"efeito|{e.Kind}|{e.Target}").SupportKey(e.Source?.Method is null ? null : $"efeito|{e.Kind}|src|{e.Source.Method}")
                .Add("tipo", "Efeito", DiffImpacts.Behavior, S($"{e.Kind} {e.Target}"))
                .Add("classe", "Classe", DiffImpacts.Behavior, S(e.Class))
                .Add("evento", "Tipo de evento", DiffImpacts.Behavior, S(e.EventKind))
                .Add("mensagem", "Mensagem", DiffImpacts.Behavior, S(Text(e.Message)))
                .Add("condicoes", "Condições", DiffImpacts.Behavior, conditions, display)
                .Add("descricao", "Descrição", DiffImpacts.Documentation, S(Text(e.Description)))
                .Add("origem", "Origem no código", DiffImpacts.Origin, S(Origin(e.Source)));
        }).ToList();

    private static List<DiffProjection> Entities(EndpointAnalysisContext context) =>
        context.EntityChanges.Select((e, i) =>
        {
            var (conditions, display) = ConditionSet(e.ConditionIds, context.ConditionRegistry);
            var p = new DiffProjection($"{e.Entity} {e.Operation}", null, i, DiffImpacts.Behavior) { Source = e }
                .Key($"entidade|{e.Entity}|{e.Operation}|{e.EffectClass}|{conditions?.ToJsonString()}")
                .Key($"entidade|{e.Entity}|{e.Operation}|{e.EffectClass}").Key($"entidade|{e.Entity}|{e.Operation}")
                .SupportKey($"entidade|{e.Entity}")
                .Add("operacao", "Operação", DiffImpacts.Behavior, S($"{e.Entity} {e.Operation}"))
                .Add("classe", "Classe", DiffImpacts.Behavior, S(e.EffectClass))
                .Add("condicoes", "Condições", DiffImpacts.Behavior, conditions, display)
                .Add("propriedades", "Propriedades", DiffImpacts.Behavior, Set(e.Properties));
            // Uma linha por propriedade: valor atribuído e condição (a mesma propriedade pode ser atribuída em mais de um ramo).
            foreach (var property in e.PropertyChanges.GroupBy(c => c.Property).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var assignments = property.Select(c => (Change: c, Conditions: ConditionSet(c.ConditionIds, context.ConditionRegistry)))
                    .OrderBy(a => Code(a.Change.Value), StringComparer.Ordinal).ToList();
                p.Add($"propriedade.{property.Key}", $"Propriedade {property.Key}", DiffImpacts.Behavior,
                    new JsonArray(assignments.Select(a => (JsonNode?)new JsonObject
                    {
                        ["valor"] = Code(a.Change.Value),
                        ["condicoes"] = a.Conditions.Value,
                    }).ToArray()),
                    new JsonArray(assignments.Select(a => (JsonNode?)new JsonObject
                    {
                        ["valor"] = a.Change.Value,
                        ["condicoes"] = a.Conditions.Display ?? JsonValue.Create("sempre"),
                    }).ToArray()));
            }
            return p.Add("origem", "Origem no código", DiffImpacts.Origin, S(Origin(e.CreationSource ?? e.Source)));
        }).ToList();

    /// <summary>Texto da IA: tudo é documentação (redação nunca é tratada como mudança de comportamento).</summary>
    private static List<DiffProjection> Documentation(EndpointAnalysisResult ai)
    {
        var items = new List<DiffProjection>
        {
            new DiffProjection("Objetivo", null, 0, DiffImpacts.Documentation).Key("ia|objetivo")
                .Add("objetivo", "Objetivo", DiffImpacts.Documentation, S(Text(ai.Summary))),
        };
        items.AddRange(AiRules(ai.BusinessRules, "Regra", items.Count));
        items.AddRange(AiRules(ai.Validations, "Validação", items.Count));
        items.AddRange(ai.EntityChanges.Select((e, i) => new DiffProjection($"Entidade: {e.Entity} {e.Operation}", null, items.Count + i, DiffImpacts.Documentation)
            .Key($"ia-entidade|{e.Entity}|{e.Operation}").SupportKey($"ia-entidade|{e.Entity}")
            .Add("propriedades", "Propriedades e condições", DiffImpacts.Documentation,
                Set(e.Properties.Select(p => $"{p.Name}: {Code(p.Condition)}")),
                List(e.Properties.Select(p => string.IsNullOrWhiteSpace(p.Condition) ? $"{p.Name}: sempre" : $"{p.Name}: {p.Condition}")))));
        items.Add(new DiffProjection("Pontos não determinados", null, int.MaxValue, DiffImpacts.Documentation).Key("ia|incertezas")
            .Add("incertezas", "Pontos não determinados", DiffImpacts.Documentation, Set(ai.Uncertainties.Select(Text)), List(ai.Uncertainties)));
        return items;
    }

    private static IEnumerable<DiffProjection> AiRules(List<BusinessRule> rules, string label, int offset) => rules.Select((r, i) =>
    {
        var prefix = label == "Regra" ? "ia-regra" : "ia-validacao";
        var title = string.IsNullOrWhiteSpace(r.Title) ? r.Description : r.Title;
        return new DiffProjection($"{label}: {title}", null, offset + i, DiffImpacts.Documentation)
            .Key(string.IsNullOrWhiteSpace(r.Condition) ? null : $"{prefix}|{Code(r.Condition)}")
            .Key(string.IsNullOrWhiteSpace(r.Condition) ? null : $"{prefix}|{Mask(Code(r.Condition))}")
            .Key(string.IsNullOrWhiteSpace(r.ErrorMessage) ? null : $"{prefix}|msg|{Text(r.ErrorMessage)}")
            .Key(r.Evidence is { Code.Length: > 0 } ev ? $"{prefix}|ev|{ev.Method}|{Code(ev.Code)}" : null)
            .Key(string.IsNullOrWhiteSpace(title) ? null : $"{prefix}|titulo|{Text(title)}")
            .SupportKey(r.Evidence?.Method is { Length: > 0 } method ? $"{prefix}|src|{method}" : null)
            .Add("titulo", "Título", DiffImpacts.Documentation, S(Text(title)))
            .Add("contexto", "Contexto", DiffImpacts.Documentation, S(Text(r.Context)))
            .Add("condicao", "Condição", DiffImpacts.Documentation, S(Code(r.Condition)), S(r.Condition))
            .Add("mensagem", "Mensagem de erro", DiffImpacts.Documentation, S(Text(r.ErrorMessage)))
            .Add("origem", "Evidência", DiffImpacts.Origin, S(Origin(r.Evidence?.File, r.Evidence?.Method)));
    });

    // ---- Aba Cenários ----

    private static AnalysisDiffSection Scenarios(EndpointAnalysisReport previous, EndpointAnalysisReport current)
    {
        var section = new AnalysisDiffSection { Id = ScenariosTab, Title = "Cenários" };
        if (MissingTab(previous, current, ScenariosTab, "Cenários") is { } reason) return NotComparable(section, reason);
        var (pm, cm) = (previous.Context.Scenarios, current.Context.Scenarios);
        if (pm is null || cm is null)
            return NotComparable(section, $"Matriz de cenários ausente na versão {(pm is null ? "anterior" : "atual")}: não comparável (nenhum cenário foi removido).");

        var labels = previous.Ai?.Scenarios is { Count: > 0 } && current.Ai?.Scenarios is { Count: > 0 };
        var (bases, currents) = (StaticScenarios(pm, previous.Ai, labels), StaticScenarios(cm, current.Ai, labels));
        var matches = DiffMatcher.Match(bases, currents);
        var matrix = Group("matriz-estatica", "Matriz estática", matches, m => Decisions(m, pm, cm),
            "Cenários pareados pelo comportamento (tipo, foco, regra, mensagens), não pelo id: CEN-xx é renumerado a cada análise.");
        if (!labels && (previous.Ai?.Scenarios is { Count: > 0 } || current.Ai?.Scenarios is { Count: > 0 }))
            matrix.Notes.Add("Títulos da IA presentes em só uma das versões: não comparados.");
        section.Groups.Add(matrix);
        section.Groups.Add(Group("entradas", "Campos do payload", Inputs(pm), Inputs(cm)));
        if (Runtime(previous, current, matches) is { } runtime) section.Groups.Add(runtime);
        return Total(section);
    }

    private static List<DiffProjection> StaticScenarios(ScenarioMatrix matrix, EndpointAnalysisResult? ai, bool labels)
    {
        var titles = labels ? (ai?.Scenarios ?? []).GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First()) : [];
        return matrix.Scenarios.Select((s, i) => StaticScenario(s, i, titles.GetValueOrDefault(s.Id), labels)).ToList();
    }

    private static DiffProjection StaticScenario(Scenario s, int position, ScenarioLabel? label, bool labels)
    {
        var f = s.Focus;
        var e = s.Expected;
        var messages = Set(e.Messages.Select(Text));
        var p = new DiffProjection(label?.Title is { Length: > 0 } title ? title : s.Title, s.Id, position, DiffImpacts.Behavior) { Source = s };
        if (f is null) p.Key($"cen|{s.Kind}|sem-foco|{Code(e.Rule)}|{e.Outcome}").Key(null).Key(null);
        else p.Key($"cen|{s.Kind}|{Code(f.Expression)}|{f.Value}|{Text(f.Detail)}").Key($"cen|{s.Kind}|{Code(f.Expression)}|{f.Value}|{Mask(f.Detail)}")
            .Key($"cen|{s.Kind}|{Mask(Code(f.Expression))}|{f.Value}");
        p.Key(messages is null ? null : $"cen|{s.Kind}|msg|{messages.ToJsonString()}")
            .Key(string.IsNullOrWhiteSpace(e.Rule) ? null : $"cen|{s.Kind}|regra|{Code(e.Rule)}")
            .SupportKey(Subject(f?.Expression) is { } subject ? $"cen|{s.Kind}|campo|{subject}" : null);

        p.Add("tipo", "Tipo", DiffImpacts.Behavior, S(s.Kind))
            .Add("foco.expressao", "Condição exercitada", DiffImpacts.Behavior, S(Code(f?.Expression)), S(f?.Expression))
            .Add("foco.valor", "Valor da condição", DiffImpacts.Behavior, f is null ? null : JsonValue.Create(f.Value))
            .Add("foco.detalhe", "Detalhe do foco", DiffImpacts.Behavior, S(Text(f?.Detail)), masked: S(Mask(f?.Detail)), descriptive: true)
            .Add("precondicoes", "Pré-condições", DiffImpacts.Behavior, Set(s.Preconditions.Select(c => Text($"{c.Kind}: {c.Description}"))),
                List(s.Preconditions.Select(c => $"{c.Kind}: {c.Description}")), Set(s.Preconditions.Select(c => Mask($"{c.Kind}: {c.Description}"))))
            .Add("esperado.resultado", "Resultado esperado", DiffImpacts.Behavior, S(e.Outcome))
            .Add("esperado.http", "HTTP esperado", DiffImpacts.Behavior, N(e.HttpStatus))
            .Add("esperado.mensagens", "Mensagens", DiffImpacts.Behavior, messages, List(e.Messages))
            .Add("esperado.excecao", "Exceção", DiffImpacts.Behavior, S(e.Exception))
            .Add("esperado.regra", "Regra que interrompe", DiffImpacts.Behavior, S(Code(e.Rule)), S(e.Rule))
            .Add("esperado.efeitos", "Efeitos", DiffImpacts.Behavior, Set(e.Effects.Select(EffectText)), List(e.Effects.Select(EffectDisplay)))
            .Add("esperado.persiste", "Persiste no banco", DiffImpacts.Behavior, JsonValue.Create(e.Persisted))
            .Add("esperado.origemStatus", "Origem do status", DiffImpacts.Origin, S(e.StatusSource))
            .Add("esperado.origem", "Origem no código", DiffImpacts.Origin, S(Origin(e.Source)));
        AddRequest(p, s.Request, DiffImpacts.Behavior, false);
        p.Add("titulo", "Título", DiffImpacts.Documentation, S(Text(s.Title)), masked: S(Mask(s.Title)), descriptive: true);
        if (labels)
            p.Add("titulo.ia", "Título (IA)", DiffImpacts.Documentation, S(Text(label?.Title)))
                .Add("descricao.ia", "Descrição (IA)", DiffImpacts.Documentation, S(Text(label?.Description)));
        return p.Add("tecnica", "Técnica", DiffImpacts.Environment, S(s.Technique))
            .Add("solver", "Solver", DiffImpacts.Environment, S(s.Solver))
            .Add("observacoes", "Observações", DiffImpacts.Environment, Set((s.Notes ?? []).Select(Text)), List(s.Notes ?? []));
    }

    private static string EffectText(ScenarioEffect e) =>
        $"{e.Kind} {e.Target}{(e.Properties is { Count: > 0 } props ? $": {string.Join(",", props.Order(StringComparer.Ordinal))}" : "")}{(e.Uncertain ? " (?)" : "")}";

    private static string EffectDisplay(ScenarioEffect e) =>
        $"{e.Description}{(e.Properties is { Count: > 0 } props ? $": {string.Join(", ", props)}" : "")}{(e.Uncertain ? " (?)" : "")}";

    /// <summary>Payload do cenário: mudar só os valores (datas relativas a hoje, ids) é dado de teste; mudar os campos, não.</summary>
    private static void AddRequest(DiffProjection p, ScenarioRequest? request, string impact, bool contextOnly)
    {
        if (request is null) return;
        var parameters = new JsonObject();
        if (request.Route is { Count: > 0 }) parameters["rota"] = KeyValues(request.Route);
        if (request.Query is { Count: > 0 }) parameters["query"] = KeyValues(request.Query);
        if (request.Headers is { Count: > 0 }) parameters["headers"] = KeyValues(request.Headers);
        var body = Redact(request.Body);
        p.Add("requisicao.metodo", "Método", impact, S(request.Method), contextOnly: contextOnly)
            .Add("requisicao.url", "URL", impact, S(RedactText(request.Url)), masked: S(Mask(request.Url)), contextOnly: contextOnly)
            .Add("requisicao.parametros", "Rota, query e headers", impact, parameters.Count == 0 ? null : parameters,
                masked: parameters.Count == 0 ? null : Shape(parameters), contextOnly: contextOnly)
            .Add("requisicao.corpo", "Body", impact, body, masked: Shape(body), contextOnly: contextOnly);
    }

    private static JsonObject KeyValues(Dictionary<string, string?> values) => new(values.OrderBy(v => v.Key, StringComparer.Ordinal)
        .Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(IsSensitive(v.Key) && v.Value is not null ? Redacted : RedactText(v.Value ?? "")))));

    [GeneratedRegex(@"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+")]
    private static partial Regex MemberAccess();

    /// <summary>Campo exercitado: "request.Observacao: [MaxLength(500)]" → request.Observacao.</summary>
    private static string? Subject(string? expression) =>
        expression is null ? null : MemberAccess().Match(expression) is { Success: true } m ? m.Value : null;

    /// <summary>Valor das condições da tabela de decisão presentes nas duas versões (pela expressão; Dx é renumerado).</summary>
    private static IEnumerable<AnalysisDiffField> Decisions(DiffMatch match, ScenarioMatrix previous, ScenarioMatrix current)
    {
        if (match is not { Base.Source: Scenario b, Current.Source: Scenario c, Uncertain: false }) yield break;
        var (bv, cv) = (DecisionValues(b, previous), DecisionValues(c, current));
        var before = new JsonObject();
        var after = new JsonObject();
        foreach (var (key, (value, expression)) in bv)
        {
            if (!cv.TryGetValue(key, out var other) || value == other.Value) continue;
            before[$"{key.Split('|')[0]}: {expression}"] = value is { } x ? JsonValue.Create(x) : null;
            after[$"{key.Split('|')[0]}: {expression}"] = other.Value is { } y ? JsonValue.Create(y) : null;
        }
        if (before.Count > 0)
            yield return new AnalysisDiffField { Path = "decisoes", Label = "Tabela de decisão", Impact = DiffImpacts.Behavior, Before = before, After = after };
    }

    private static Dictionary<string, (bool? Value, string Expression)> DecisionValues(Scenario scenario, ScenarioMatrix matrix)
    {
        var conditions = matrix.Conditions.GroupBy(c => $"{c.Kind}|{Code(c.Expression)}").Where(g => g.Count() == 1).Select(g => g.First());
        return conditions.ToDictionary(c => $"{c.Kind}|{Code(c.Expression)}",
            c => (scenario.Decisions.TryGetValue(c.Id, out var value) ? value : null, c.Expression));
    }

    private static List<DiffProjection> Inputs(ScenarioMatrix matrix) => matrix.Inputs.Select((input, i) =>
        new DiffProjection($"{input.Name} ({input.Location})", null, i, DiffImpacts.Behavior)
            .Key($"entrada|{input.Location}|{input.Name}").Key($"entrada|{input.Name}")
            .Add("tipo", "Tipo", DiffImpacts.Behavior, S(input.Type))
            .Add("local", "Local", DiffImpacts.Behavior, S(input.Location))
            .Add("obrigatorio", "Obrigatório", DiffImpacts.Behavior, Flag(input.Required))
            .Add("anulavel", "Anulável", DiffImpacts.Behavior, Flag(input.Nullable))
            .Add("restricoes", "Restrições", DiffImpacts.Behavior, Set(input.Constraints.Select(Code)), List(input.Constraints))
            .Add("valores", "Valores do enum", DiffImpacts.Behavior, Set(input.EnumValues ?? []))
            .Add("caminho", "Caminho no código", DiffImpacts.Origin, S(input.Path))).ToList();

    // ---- Matriz validada em runtime ----

    /// <summary>
    /// Runtime x runtime, separado das diferenças estáticas. Os cenários da matriz seguem o par estático (o id muda entre as
    /// versões); os descobertos em runtime (RT-xx) são pareados pelo resultado observado. Inalcançável (Removed) é um status,
    /// nunca um cenário excluído do código.
    /// </summary>
    private static AnalysisDiffGroup? Runtime(EndpointAnalysisReport previous, EndpointAnalysisReport current, List<DiffMatch> matches)
    {
        var group = new AnalysisDiffGroup { Id = "matriz-runtime", Title = "Matriz validada em runtime" };
        var (pr, cr) = (previous.Runtime, current.Runtime);
        if (pr is null && cr is null) return null;
        if (pr?.Matrix is null || cr?.Matrix is null)
        {
            var missing = pr?.Matrix is null ? "anterior" : "atual";
            var why = (pr?.Matrix is null ? pr : cr) is { Status: RuntimeValidationStatuses.Failed } ? "a validação em runtime falhou" : "não houve validação em runtime";
            return NotComparable(group, $"Na versão {missing} {why}: a matriz validada não é comparável (nenhum cenário foi removido).");
        }

        var (bv, cv) = (Validated(pr.Matrix), Validated(cr.Matrix));
        var items = new List<AnalysisDiffItem>();
        var skipped = 0;
        foreach (var match in matches)
        {
            if (match.Base is null || match.Current is null) continue;
            if (match.Uncertain) { skipped++; continue; }
            var (b, c) = (bv.GetValueOrDefault(match.Base.Ref!), cv.GetValueOrDefault(match.Current.Ref!));
            if (b is null && c is null) continue;
            var pair = new DiffMatch(RuntimeScenario(b, match.Base), RuntimeScenario(c, match.Current));
            if (Item(pair) is not { } item) { group.Counts.Unchanged++; continue; }
            Alerts(item, b?.Status, c?.Status);
            items.Add(item);
        }
        if (skipped > 0) group.Notes.Add($"{skipped} cenário(s) com correspondência incerta na matriz estática não foram comparados em runtime.");

        // Descobertos em runtime: observações, não cenários do código.
        var discovered = DiffMatcher.Match(Discovered(pr.Matrix), Discovered(cr.Matrix));
        foreach (var match in discovered)
        {
            if (Item(match) is not { } item) { group.Counts.Unchanged++; continue; }
            item.Notes.Add(match.Base is null ? "Resultado observado em runtime que a análise estática não previu."
                : match.Current is null ? "Não foi observado nesta validação: depende dos dados e do ambiente, não indica mudança no código."
                : "Resultado descoberto em runtime nas duas versões.");
            items.Add(item);
        }
        foreach (var item in items) Count(group.Counts, item);
        group.Items = items;
        group.Notes.AddRange(RuntimeNotes(pr, cr));
        if (RuntimeExecution(pr, cr) is { } execution)
        {
            group.Items.Insert(0, execution);
            Count(group.Counts, execution);
        }
        return group;
    }

    private static Dictionary<string, ValidatedScenario> Validated(ValidatedMatrix matrix) =>
        matrix.Scenarios.Concat(matrix.Removed).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>Lado runtime de um cenário pareado: chave e título do par estático; sem o cenário validado, nenhum campo.</summary>
    private static DiffProjection RuntimeScenario(ValidatedScenario? v, DiffProjection scenario)
    {
        var p = new DiffProjection(scenario.Title, v?.Id ?? scenario.Ref, scenario.Position, DiffImpacts.Runtime).Key(scenario.SemanticKey);
        if (v is null) return p;
        var o = v.Observed;
        var conformity = o is null ? "não executado" : o.HttpStatus == v.Expected.HttpStatus ? "conforme" : "divergente";
        p.Add("runtime.status", "Status runtime", DiffImpacts.Runtime, S(v.Status))
            .Add("runtime.http", "HTTP observado", DiffImpacts.Runtime, N(o?.HttpStatus))
            .Add("runtime.conformidade", "Esperado × observado", DiffImpacts.Runtime, S(conformity),
                S($"{conformity}: esperado {v.Expected.HttpStatus?.ToString() ?? "?"} · observado {o?.HttpStatus?.ToString() ?? "—"}"))
            .Add("runtime.excecao", "Exceção observada", DiffImpacts.Environment, S(Text(o?.Exception)), masked: S(Mask(o?.Exception)));
        AddRequest(p, v.Request, DiffImpacts.TestData, true);
        return p.Add("runtime.dados", "Dados reais usados", DiffImpacts.TestData,
                Set(v.Bindings.Select(b => $"{b.Variable} = {(IsSensitive(b.Variable) ? Redacted : b.Value)}")), contextOnly: true)
            .Add("runtime.tentativas", "Tentativas", DiffImpacts.Environment, N(v.Attempts), contextOnly: true)
            .Add("runtime.motivos", "Motivos", DiffImpacts.Environment, List(v.Reasons.Select(RedactText)), contextOnly: true)
            .Add("runtime.evidencias", "Evidências", DiffImpacts.Environment, List(v.Evidence.Select(RedactText)), contextOnly: true);
    }

    [GeneratedRegex(@"\s*\(ao executar [^)]*\)$")]
    private static partial Regex TriggeredBy();

    private static List<DiffProjection> Discovered(ValidatedMatrix matrix) =>
        matrix.Scenarios.Where(s => s.Status == ScenarioValidationStatuses.Discovered).Select((s, i) =>
        {
            var message = s.Expected.Messages.FirstOrDefault() ?? s.Observed?.Exception ?? TriggeredBy().Replace(s.Title, "");
            return new DiffProjection(TriggeredBy().Replace(s.Title, ""), s.Id, 1000 + i, DiffImpacts.Runtime)
                .Key($"rt|{s.Kind}|{s.Observed?.HttpStatus}|{Text(message)}").Key($"rt|{s.Kind}|{s.Observed?.HttpStatus}|{Mask(message)}")
                .SupportKey($"rt|{s.Observed?.HttpStatus}")
                .Add("runtime.http", "HTTP observado", DiffImpacts.Runtime, N(s.Observed?.HttpStatus))
                .Add("runtime.mensagem", "Mensagem observada", DiffImpacts.Runtime, S(RedactText(Text(message))), masked: S(Mask(message)))
                .Add("runtime.tipo", "Tipo", DiffImpacts.Runtime, S(s.Kind));
        }).ToList();

    private static void Alerts(AnalysisDiffItem item, string? before, string? after)
    {
        if (before == after) return;
        if (before == ScenarioValidationStatuses.Confirmed && after is ScenarioValidationStatuses.Inconclusive or ScenarioValidationStatuses.NotMaterialized)
        {
            item.Alert = true;
            item.Notes.Add("Alerta: deixou de ser confirmado em runtime. Não é prova isolada de regressão (limites, dados ou ambiente podem explicar).");
        }
        else if (after == ScenarioValidationStatuses.Unreachable)
            item.Notes.Add("Retirado da matriz validada (inalcançável em runtime): continua no código, não é um cenário excluído.");
        else if (before == ScenarioValidationStatuses.Unreachable)
            item.Notes.Add("Voltou à matriz validada: deixou de ser inalcançável.");
        else if (after == ScenarioValidationStatuses.Confirmed)
            item.Notes.Add("Passou a ser confirmado em runtime.");
    }

    /// <summary>Totais por status, requisições e duração: contexto da execução, não diferença de comportamento.</summary>
    private static IEnumerable<string> RuntimeNotes(RuntimeValidation previous, RuntimeValidation current)
    {
        var statuses = ScenarioValidationStatuses.All
            .Select(s => (Status: s, Before: previous.Matrix!.Counts.GetValueOrDefault(s), After: current.Matrix!.Counts.GetValueOrDefault(s)))
            .Where(x => x.Before != x.After).Select(x => $"{x.Status}: {x.Before} → {x.After}").ToList();
        if (statuses.Count > 0) yield return $"Cenários por status: {string.Join(" · ", statuses)}.";
        yield return $"Execução (ambiente): {previous.Stats.Requests} → {current.Stats.Requests} requisição(ões), "
            + $"{previous.Stats.AiCalls} → {current.Stats.AiCalls} chamada(s) à IA, {previous.DurationMs / 1000.0:0.#}s → {current.DurationMs / 1000.0:0.#}s.";
    }

    private static AnalysisDiffItem? RuntimeExecution(RuntimeValidation previous, RuntimeValidation current)
    {
        DiffProjection Of(RuntimeValidation r) => new DiffProjection("Execução da validação", null, -1, DiffImpacts.Environment).Key("rt|execucao")
            .Add("status", "Status da validação", DiffImpacts.Environment, S(r.Status))
            .Add("escrita", "Escrita permitida", DiffImpacts.Environment, JsonValue.Create(r.WritesAllowed));
        return Item(new DiffMatch(Of(previous), Of(current)));
    }

    // ---- Montagem ----

    private static string? MissingTab(EndpointAnalysisReport previous, EndpointAnalysisReport current, string tab, string label)
    {
        var (inPrevious, inCurrent) = (previous.Sections.Contains(tab), current.Sections.Contains(tab));
        if (inPrevious && inCurrent) return null;
        var where = !inPrevious && !inCurrent ? "nas duas versões" : $"na versão {(inPrevious ? "atual" : "anterior")}";
        return $"Aba {label} não gerada {where}: não comparável (nada foi removido).";
    }

    private static T NotComparable<T>(T target, string reason)
    {
        switch (target)
        {
            case AnalysisDiffSection section:
                section.Comparable = false;
                section.Reason = reason;
                section.Counts.NotComparable = 1;
                break;
            case AnalysisDiffGroup group:
                group.Comparable = false;
                group.Reason = reason;
                group.Counts.NotComparable = 1;
                break;
        }
        return target;
    }

    private static AnalysisDiffSection Total(AnalysisDiffSection section)
    {
        foreach (var group in section.Groups) section.Counts.Add(group.Counts);
        return section;
    }

    private static AnalysisDiffGroup Group(string id, string title, List<DiffProjection> bases, List<DiffProjection> currents, string? note = null) =>
        Group(id, title, DiffMatcher.Match(bases, currents), null, note);

    private static AnalysisDiffGroup Group(string id, string title, List<DiffMatch> matches,
        Func<DiffMatch, IEnumerable<AnalysisDiffField>>? extra, string? note)
    {
        var group = new AnalysisDiffGroup { Id = id, Title = title };
        if (note is not null) group.Notes.Add(note);
        foreach (var match in matches)
        {
            if (Item(match, extra) is not { } item)
            {
                group.Counts.Unchanged++;
                continue;
            }
            group.Items.Add(item);
            Count(group.Counts, item);
        }
        return group;
    }

    /// <summary>Item do diff; nulo quando o par é igual (a menos de campos de contexto).</summary>
    private static AnalysisDiffItem? Item(DiffMatch match, Func<DiffMatch, IEnumerable<AnalysisDiffField>>? extra = null)
    {
        var (b, c) = (match.Base, match.Current);
        var item = new AnalysisDiffItem
        {
            Key = (c ?? b)!.SemanticKey,
            Title = (c ?? b)!.Title,
            BaseRef = b?.Ref,
            CurrentRef = c?.Ref,
            Before = b?.Snapshot(),
            After = c?.Snapshot(),
        };
        if (match.Note is not null) item.Notes.Add(match.Note);
        if (b is null || c is null)
        {
            item.Kind = match.Uncertain ? DiffKinds.Uncertain : b is null ? DiffKinds.Added : DiffKinds.Removed;
            item.Impact = (c ?? b)!.Impact;
            item.Fields = DiffMatcher.Fields(b, c);
            return item;
        }
        item.Fields = DiffMatcher.Compare(b, c);
        if (extra is not null) item.Fields.InsertRange(0, extra(match));
        if (item.Fields.Count == 0 && !match.Uncertain) return null;
        item.Kind = match.Uncertain ? DiffKinds.Uncertain : DiffKinds.Changed;
        item.Impact = item.Fields.Count == 0 ? b.Impact : item.Fields.MinBy(f => DiffImpacts.Rank(f.Impact))!.Impact;
        return item;
    }

    private static void Count(AnalysisDiffCounts counts, AnalysisDiffItem item)
    {
        if (item.Kind == DiffKinds.Uncertain) counts.Uncertain++;
        else if (!DiffImpacts.IsPrimary(item.Impact)) counts.Minor++;
        else if (item.Kind == DiffKinds.Added) counts.Added++;
        else if (item.Kind == DiffKinds.Removed) counts.Removed++;
        else counts.Changed++;
    }
}
