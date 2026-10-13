using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Tests;

/// <summary>Correspondência semântica do diff: matriz real da SampleApi, alterada como uma nova análise alteraria.</summary>
[Collection(SampleSolutionCollection.Name)]
public class AnalysisDiffTests(SampleSolutionFixture fixture)
{
    private static readonly AnalysisDiffService Diff = new();

    private async Task<EndpointAnalysisReport> ReportAsync()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        // O contexto do fixture é compartilhado: cada teste altera a própria cópia.
        return Clone(new EndpointAnalysisReport { Context = context, Version = new AnalysisVersion { AnalyzerVersion = "1" } });
    }

    private static EndpointAnalysisReport Clone(EndpointAnalysisReport report) =>
        JsonSerializer.Deserialize<EndpointAnalysisReport>(JsonSerializer.Serialize(report))!;

    private static StoredAnalysis Stored(EndpointAnalysisReport report, string id, int minute = 0, string route = "/api/programacoes") =>
        new(id, null, @"C:\src\SampleApi.sln", "POST", route, "SampleApi", $"fonte-{id}",
            new DateTimeOffset(2026, 10, 13, 12, minute, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 12, minute, 0, TimeSpan.Zero), report);

    private static AnalysisDiffResult Compare(EndpointAnalysisReport previous, EndpointAnalysisReport current) =>
        Diff.Compare(Stored(previous, "anterior"), Stored(current, "atual", 5));

    private static AnalysisDiffGroup Group(AnalysisDiffResult result, string id) =>
        result.Sections.SelectMany(s => s.Groups).Single(g => g.Id == id);

    private static Scenario ScenarioOf(EndpointAnalysisReport report, Func<Scenario, bool> predicate) =>
        report.Context.Scenarios!.Scenarios.Single(predicate);

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static void Renumber(ScenarioMatrix matrix)
    {
        for (var i = 0; i < matrix.Scenarios.Count; i++) matrix.Scenarios[i].Id = $"CEN-{i + 1:00}";
    }

    /// <summary>Mesmo código analisado de novo: outra ordem, outros ids (CEN, D, C), outras linhas e outra formatação.</summary>
    private static EndpointAnalysisReport Reshuffled(EndpointAnalysisReport report)
    {
        var copy = Clone(report);
        var context = copy.Context;
        var matrix = context.Scenarios!;
        matrix.Scenarios.Reverse();
        Renumber(matrix);

        var decisions = matrix.Conditions.Select((c, i) => (Old: c.Id, New: $"D{matrix.Conditions.Count - i}")).ToDictionary(x => x.Old, x => x.New);
        foreach (var condition in matrix.Conditions)
        {
            condition.Id = decisions[condition.Id];
            condition.Expression = condition.Expression.Replace(" ", "  ");
            if (condition.Source is not null) condition.Source.Line += 10;
        }
        matrix.Conditions.Reverse();
        foreach (var scenario in matrix.Scenarios)
        {
            scenario.Decisions = scenario.Decisions.ToDictionary(d => decisions[d.Key], d => d.Value);
            if (scenario.Expected.Source is not null) scenario.Expected.Source.Line += 10;
            scenario.Preconditions.Reverse();
            scenario.Expected.Effects.Reverse();
        }

        var registry = context.ConditionRegistry.Keys.ToDictionary(k => k, k => $"C{int.Parse(k[1..]) + 100}");
        context.ConditionRegistry = context.ConditionRegistry.ToDictionary(r => registry[r.Key], r => r.Value);
        string Id(string id) => registry.GetValueOrDefault(id, id);
        foreach (var effect in context.Effects)
        {
            effect.ConditionIds = effect.ConditionIds?.Select(Id).ToList();
            if (effect.Source is not null) effect.Source.Line += 7;
        }
        context.Effects.Reverse();
        foreach (var change in context.EntityChanges)
        {
            change.ConditionIds = change.ConditionIds?.Select(Id).ToList();
            change.Properties.Reverse();
            foreach (var property in change.PropertyChanges) property.ConditionIds = property.ConditionIds?.Select(Id).ToList();
        }
        foreach (var condition in context.Conditions) condition.SourceLine += 3;
        return copy;
    }

    [Fact]
    public void Primeira_analise_nao_tem_base()
    {
        var result = Diff.Compare(null, Stored(new EndpointAnalysisReport(), "atual"));
        Assert.False(result.HasBase);
        Assert.Null(result.Base);
        Assert.Equal("Nenhuma análise anterior para comparar.", result.Message);
    }

    [Fact]
    public async Task Versoes_de_outro_endpoint_sao_recusadas()
    {
        var report = await ReportAsync();
        Assert.Throws<ArgumentException>(() => Diff.Compare(Stored(report, "a"), Stored(report, "b", 1, "/api/outro")));
    }

    [Fact]
    public async Task Renumeracao_ordem_formatacao_e_linhas_nao_geram_diferencas()
    {
        var previous = await ReportAsync();
        var result = Compare(previous, Reshuffled(previous));

        Assert.True(result.HasBase);
        Assert.False(result.HasDifferences, string.Join("\n", result.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items)
            .Select(i => $"{i.Kind} {i.Title}: {string.Join(", ", i.Fields.Select(f => f.Path))}")));
        Assert.Equal(0, result.Counts.Minor);
        Assert.Equal("Nenhuma diferença encontrada.", result.Message);
        Assert.Equal(previous.Context.Scenarios!.Scenarios.Count, Group(result, "matriz-estatica").Counts.Unchanged);
    }

    [Fact]
    public async Task Cenario_inserido_que_renumera_os_demais_e_so_uma_inclusao()
    {
        var previous = await ReportAsync();
        var current = Clone(previous);
        var matrix = current.Context.Scenarios!;
        var inserted = JsonSerializer.Deserialize<Scenario>(JsonSerializer.Serialize(ScenarioOf(previous, s => s.Expected.Messages.Contains("Veículo indisponível."))))!;
        inserted.Focus = new ScenarioFocus { Expression = "request.Quantidade > 1000", Value = true };
        inserted.Expected.Messages = ["Quantidade acima do limite."];
        inserted.Expected.Rule = "request.Quantidade > 1000";
        inserted.Title = "Regra: Quantidade acima do limite.";
        matrix.Scenarios.Insert(0, inserted);
        Renumber(matrix);

        var group = Group(Compare(previous, current), "matriz-estatica");
        var item = Assert.Single(group.Items);
        Assert.Equal(DiffKinds.Added, item.Kind);
        Assert.Equal("CEN-01", item.CurrentRef);
        Assert.Equal((1, 0, 0, 0), (group.Counts.Added, group.Counts.Removed, group.Counts.Changed, group.Counts.Uncertain));
    }

    [Fact]
    public async Task Alteracao_de_limite_aparece_como_alteracao_com_antes_e_depois()
    {
        var previous = await ReportAsync();
        var current = Clone(previous);
        var condition = current.Context.Scenarios!.Conditions.Single(c => c.Validation?.Kind == "maxLength");
        condition.Expression = condition.Expression.Replace("500", "600");
        condition.Label = condition.Label!.Replace("500", "600");
        condition.Validation!.Max = "600";
        foreach (var scenario in current.Context.Scenarios.Scenarios.Where(s => s.Focus?.Expression.Contains("Observacao") == true))
        {
            scenario.Focus!.Expression = scenario.Focus.Expression.Replace("500", "600");
            scenario.Focus.Detail = scenario.Focus.Detail!.Replace("501", "601").Replace("500", "600");
            scenario.Title = scenario.Title.Replace("501", "601").Replace("500", "600");
            scenario.Expected.Messages = scenario.Expected.Messages.Select(m => m.Replace("500", "600")).ToList();
            scenario.Expected.Rule = scenario.Expected.Rule?.Replace("500", "600");
        }

        var result = Compare(previous, current);
        var validation = Assert.Single(Group(result, "validacoes").Items);
        Assert.Equal(DiffKinds.Changed, validation.Kind);
        Assert.Equal(DiffImpacts.Behavior, validation.Impact);
        var max = validation.Fields.Single(f => f.Path == "maximo");
        Assert.Equal(("500", "600"), (max.Before!.GetValue<string>(), max.After!.GetValue<string>()));
        Assert.Contains(validation.Fields, f => f.Path == "mensagem");

        var scenarios = Group(result, "matriz-estatica");
        Assert.Equal((0, 0, 2), (scenarios.Counts.Added, scenarios.Counts.Removed, scenarios.Counts.Changed));
        Assert.All(scenarios.Items, i => Assert.Contains(i.Fields, f => f.Path == "foco.expressao" && f.Impact == DiffImpacts.Behavior));
    }

    [Fact]
    public async Task Resultado_esperado_alterado_aparece_no_cenario_certo()
    {
        var previous = await ReportAsync();
        var current = Reshuffled(previous);
        ScenarioOf(current, s => s.Expected.Messages.Contains("Veículo indisponível.")).Expected.HttpStatus = 409;

        var item = Assert.Single(Group(Compare(previous, current), "matriz-estatica").Items);
        Assert.Equal(DiffKinds.Changed, item.Kind);
        Assert.NotEqual(item.BaseRef, item.CurrentRef); // Pareado pelo comportamento, apesar da renumeração.
        var http = Assert.Single(item.Fields);
        Assert.Equal(("esperado.http", 422, 409), (http.Path, http.Before!.GetValue<int>(), http.After!.GetValue<int>()));
    }

    [Fact]
    public async Task Dados_de_teste_nao_contam_como_mudanca_de_comportamento()
    {
        var previous = await ReportAsync();
        var current = Clone(previous);
        foreach (var scenario in current.Context.Scenarios!.Scenarios)
        {
            if (scenario.Request.Body is JsonObject body && body["veiculoId"] is not null) body["veiculoId"] = 37;
            foreach (var pre in scenario.Preconditions) pre.Description = pre.Description.Replace("(1)", "(37)");
        }

        var result = Compare(previous, current);
        Assert.False(result.HasDifferences);
        Assert.True(result.Counts.Minor > 0);
        Assert.All(Group(result, "matriz-estatica").Items, i => Assert.Equal(DiffImpacts.TestData, i.Impact));
        Assert.StartsWith("Nenhuma diferença encontrada.", result.Message);
    }

    [Fact]
    public async Task Correspondencia_ambigua_fica_incerta_e_nao_vira_inclusao_ou_remocao()
    {
        var previous = await ReportAsync();
        var current = Clone(previous);
        Scenario Rule(string expression) => new()
        {
            Kind = ScenarioKinds.Rule, Focus = new ScenarioFocus { Expression = expression },
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 422, Messages = ["Limite excedido."] },
        };
        previous.Context.Scenarios!.Scenarios.AddRange([Rule("request.Total > 10"), Rule("request.Total > 20")]);
        current.Context.Scenarios!.Scenarios.AddRange([Rule("request.Total > 15"), Rule("request.Total > 25")]);
        Renumber(previous.Context.Scenarios);
        Renumber(current.Context.Scenarios);

        var group = Group(Compare(previous, current), "matriz-estatica");
        Assert.Equal((0, 0, 0, 2), (group.Counts.Added, group.Counts.Removed, group.Counts.Changed, group.Counts.Uncertain));
        Assert.All(group.Items, i =>
        {
            Assert.Equal(DiffKinds.Uncertain, i.Kind);
            Assert.NotNull(i.BaseRef);
            Assert.NotNull(i.CurrentRef);
            Assert.Contains(i.Notes, n => n.Contains("ambígua"));
        });
    }

    [Fact]
    public async Task Aba_ausente_e_nao_comparavel_nunca_remocao()
    {
        var previous = await ReportAsync();
        previous.Sections = ["summary"];
        var current = await ReportAsync();
        current.Ai = new EndpointAnalysisResult { Summary = "Cria uma programação." };
        current.Runtime = new RuntimeValidation { Matrix = new ValidatedMatrix() };

        var result = Compare(previous, current);
        var scenarios = result.Sections.Single(s => s.Id == "scenarios");
        Assert.False(scenarios.Comparable);
        Assert.Contains("não gerada na versão anterior", scenarios.Reason);
        Assert.Empty(scenarios.Groups);
        var documentation = Group(result, "documentacao");
        Assert.False(documentation.Comparable);
        Assert.Equal(0, result.Counts.Removed);
        Assert.Equal(2, result.Counts.NotComparable);
        Assert.Equal("Nenhuma diferença encontrada nas partes comparáveis.", result.Message);

        // Matriz validada só na versão atual: runtime não comparável, sem remoção.
        previous.Sections = current.Sections;
        var runtime = Group(Compare(previous, current), "matriz-runtime");
        Assert.False(runtime.Comparable);
        Assert.Empty(runtime.Items);
    }

    private static RuntimeValidation Validated(ScenarioMatrix matrix) => new()
    {
        Status = RuntimeValidationStatuses.Completed,
        Matrix = new ValidatedMatrix
        {
            Scenarios = matrix.Scenarios.Select(s => new ValidatedScenario
            {
                Id = s.Id, Kind = s.Kind, Title = s.Title, Expected = s.Expected, Request = s.Request, Preconditions = s.Preconditions,
                Status = ScenarioValidationStatuses.Confirmed, Observed = new ObservedResult { HttpStatus = s.Expected.HttpStatus, ElapsedMs = 30 },
                Attempts = 1, Evidence = ["status confere"],
            }).ToList(),
        },
    };

    [Fact]
    public async Task Runtime_mostra_status_e_divergencia_separados_da_matriz_estatica()
    {
        var previous = await ReportAsync();
        previous.Runtime = Validated(previous.Context.Scenarios!);
        var current = Reshuffled(previous);
        current.Runtime = Validated(current.Context.Scenarios!);
        foreach (var s in current.Runtime.Matrix!.Scenarios) s.Observed!.ElapsedMs = 999; // Duração não é diferença.
        var matrix = current.Runtime.Matrix!;
        var unavailable = matrix.Scenarios.Single(s => s.Expected.Messages.Contains("Veículo indisponível."));
        unavailable.Status = ScenarioValidationStatuses.Inconclusive;
        unavailable.Observed = new ObservedResult { HttpStatus = 500 };
        var notFound = matrix.Scenarios.Single(s => s.Expected.Messages.Contains("Veículo não encontrado."));
        notFound.Status = ScenarioValidationStatuses.Unreachable;
        matrix.Scenarios.Remove(notFound);
        matrix.Removed.Add(notFound);
        matrix.Scenarios.Add(new ValidatedScenario
        {
            Id = "RT-01", Status = ScenarioValidationStatuses.Discovered, Kind = ScenarioKinds.Rule, Title = "HTTP 500: falha (ao executar CEN-03)",
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 500, StatusSource = "runtime", Messages = ["falha"] },
            Observed = new ObservedResult { HttpStatus = 500 },
        });

        var result = Compare(previous, current);
        Assert.Empty(Group(result, "matriz-estatica").Items);
        var runtime = Group(result, "matriz-runtime");
        Assert.Equal((1, 0, 2), (runtime.Counts.Added, runtime.Counts.Removed, runtime.Counts.Changed));

        var alert = runtime.Items.Single(i => i.Alert);
        Assert.Equal(DiffKinds.Changed, alert.Kind);
        Assert.Equal(DiffImpacts.Runtime, alert.Impact);
        Assert.Contains(alert.Notes, n => n.Contains("prova isolada de regressão"));
        Assert.Contains(alert.Fields, f => f.Path == "runtime.status" && Text(f.Before) == "confirmado" && Text(f.After) == "inconclusivo");
        Assert.Contains(alert.Fields, f => f.Path == "runtime.conformidade" && Text(f.After)!.StartsWith("divergente"));

        var unreachable = runtime.Items.Single(i => i.Fields.Any(f => Text(f.After) == "inalcancavel"));
        Assert.Equal(DiffKinds.Changed, unreachable.Kind); // Retirado da matriz validada não é cenário removido do código.
        Assert.Contains(unreachable.Notes, n => n.Contains("não é um cenário excluído"));
        var discovered = runtime.Items.Single(i => i.Kind == DiffKinds.Added);
        Assert.Equal("RT-01", discovered.CurrentRef);
    }

    [Fact]
    public async Task Documentacao_da_ia_fica_separada_do_comportamento()
    {
        var previous = await ReportAsync();
        previous.Ai = new EndpointAnalysisResult
        {
            Summary = "Cria uma programação de transporte.",
            BusinessRules = [new BusinessRule { Title = "Data não pode estar no passado.", Condition = "request.Data < DateTime.Today", ErrorMessage = "Data inválida." }],
        };
        var current = Clone(previous);
        current.Ai!.Summary = "Cadastra uma programação de transporte.";
        current.Ai.BusinessRules[0].Title = "A data precisa ser hoje ou depois.";
        current.Ai.BusinessRules[0].Condition = "request.Data  <  DateTime.Today";

        var result = Compare(previous, current);
        var documentation = Group(result, "documentacao");
        Assert.Equal(2, documentation.Counts.Changed);
        Assert.All(documentation.Items, i => Assert.Equal(DiffImpacts.Documentation, i.Impact));
        var rule = documentation.Items.Single(i => i.Title.StartsWith("Regra"));
        Assert.Equal("titulo", Assert.Single(rule.Fields).Path); // Só a redação; a condição apenas mudou de formatação.
        Assert.All(result.Sections.SelectMany(s => s.Groups).Where(g => g.Id != "documentacao"), g => Assert.Empty(g.Items));
    }

    [Fact]
    public async Task Credenciais_nao_aparecem_no_diff()
    {
        var previous = await ReportAsync();
        var current = Clone(previous);
        foreach (var (report, secret) in new[] { (previous, "segredo-antigo-123"), (current, "segredo-novo-456") })
        {
            var scenario = ScenarioOf(report, s => s.Expected.Messages.Contains("Veículo indisponível."));
            scenario.Request.Headers = new() { ["Authorization"] = $"Bearer {secret}", ["X-Trace"] = "1" };
            ((JsonObject)scenario.Request.Body!)["senha"] = secret;
            scenario.Expected.HttpStatus = report == current ? 409 : 422;
        }

        var json = JsonSerializer.Serialize(Compare(previous, current));
        Assert.DoesNotContain("segredo-antigo-123", json);
        Assert.DoesNotContain("segredo-novo-456", json);
        Assert.Contains(DiffText.Redacted, json);
    }
}
