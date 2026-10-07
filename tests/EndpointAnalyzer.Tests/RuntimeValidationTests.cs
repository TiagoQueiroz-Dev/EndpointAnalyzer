using System.Text.Json.Nodes;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Tests;

/// <summary>Materialização dos cenários com valores reais (solver + simulação) e comparação esperado × observado.</summary>
[Collection(SampleSolutionCollection.Name)]
public class RuntimeMaterializationTests(SampleSolutionFixture fixture)
{
    private async Task<(ScenarioModel Model, EndpointAnalysisContext Context, CallGraph Graph)> ModelAsync(string endpointId)
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, endpointId);
        var context = await fixture.Service.BuildContextAsync(fixture.SolutionPath, endpoint);
        var solution = await fixture.Solutions.GetAsync(fixture.SolutionPath);
        var graph = await new CallGraphBuilder(new AnalyzerOptions(), new MethodResolver()).BuildAsync(solution, endpoint);
        var model = await new ScenarioGenerator().GenerateModelAsync(graph, context);
        return (model, context, graph);
    }

    private static Scenario Find(ScenarioModel model, string kind, Func<Scenario, bool>? filter = null) =>
        model.Matrix.Scenarios.First(s => s.Kind == kind && (filter?.Invoke(s) ?? true));

    [Fact]
    public async Task Modelo_gera_a_mesma_matriz_da_analise_estatica()
    {
        var (model, context, _) = await ModelAsync("POST /api/programacoes");

        Assert.True(model.CanMaterialize);
        Assert.Equal(context.Scenarios!.Scenarios.Select(s => $"{s.Id}|{s.Title}"), model.Matrix.Scenarios.Select(s => $"{s.Id}|{s.Title}"));
    }

    [Fact]
    public async Task Materializa_o_caminho_feliz_com_dados_reais()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var happy = Find(model, ScenarioKinds.Success, s => s.Focus is null);
        var variables = model.Variables(happy.Id);
        var disponivel = variables.First(v => v.Origin == "estado" && v.Name.EndsWith(".Disponivel"));

        var m = model.Materialize(happy.Id, [
            new ScenarioBinding("request.VeiculoId", "37", "veiculoDisponivel"),
            new ScenarioBinding(disponivel.Name, "true", "veiculoDisponivel"),
        ]);

        Assert.True(m.Success, m.Error);
        Assert.Equal(37, m.Request!.Body!["veiculoId"]!.GetValue<long>());
        Assert.Equal(201, m.Expected!.HttpStatus);
        Assert.Contains(m.VerifiedState, s => s.Contains("ObterPorId(37).Disponivel = true"));
        // O estado informado vira fato para os outros cenários.
        Assert.Contains(m.Facts, f => f.Root.Contains("ObterPorId(37)") && f.Member == "Disponivel" && f.Value == "true");
    }

    [Fact]
    public async Task Recusa_dados_reais_que_contradizem_o_cenario()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var indisponivel = Find(model, ScenarioKinds.Rule, s => s.Expected.Messages.Contains("Veículo indisponível."));
        var disponivel = model.Variables(indisponivel.Id).First(v => v.Name.EndsWith(".Disponivel"));

        var m = model.Materialize(indisponivel.Id, [new ScenarioBinding("request.VeiculoId", "37"), new ScenarioBinding(disponivel.Name, "true")]);

        Assert.False(m.Success);
        Assert.Contains("contradizem", m.Error);
    }

    [Fact]
    public async Task Recusa_valores_que_mudam_o_resultado_simulado()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var happy = Find(model, ScenarioKinds.Success, s => s.Focus is null);

        // Data no passado faz outra regra disparar antes: não é mais o caminho feliz.
        var m = model.Materialize(happy.Id, [new ScenarioBinding("request.Data", "\"2000-01-01\"")]);

        Assert.False(m.Success);
    }

    [Fact]
    public async Task A_partir_do_baseline_altera_so_o_necessario()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var happy = Find(model, ScenarioKinds.Success, s => s.Focus is null);
        var disponivel = model.Variables(happy.Id).First(v => v.Name.EndsWith(".Disponivel"));
        var baseline = model.Materialize(happy.Id, [new ScenarioBinding("request.VeiculoId", "37"), new ScenarioBinding(disponivel.Name, "true")]);
        Assert.True(baseline.Success, baseline.Error);

        var maxLength = Find(model, ScenarioKinds.Validation, s => s.Title.Contains("501"));
        var m = model.Materialize(maxLength.Id, baseline: baseline);

        Assert.True(m.Success, m.Error);
        Assert.Equal(37, m.Request!.Body!["veiculoId"]!.GetValue<long>());
        Assert.Equal(501, m.Request.Body["observacao"]!.GetValue<string>().Length);
        var change = Assert.Single(m.Changes);
        Assert.StartsWith("request.Observacao", change);
    }

    [Fact]
    public async Task Payload_base_preenche_os_campos_e_cede_ao_que_o_cenario_exige()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var happy = Find(model, ScenarioKinds.Success, s => s.Focus is null);
        var disponivel = model.Variables(happy.Id).First(v => v.Name.EndsWith(".Disponivel"));
        IReadOnlyList<ScenarioBinding> payload =
        [
            new("request.VeiculoId", "37", "veiculoModelo"),
            new("observacao", "\"Entrega agendada com o cliente\"", "sintetico"),
        ];

        // Caminho feliz: os campos que o cenário não liga vêm do payload base (e não do gerador).
        var baseline = model.Materialize(happy.Id, [new ScenarioBinding(disponivel.Name, "true")], payload: payload);
        Assert.True(baseline.Success, baseline.Error);
        Assert.Equal(37, baseline.Request!.Body!["veiculoId"]!.GetValue<long>());
        Assert.Equal("Entrega agendada com o cliente", baseline.Request.Body["observacao"]!.GetValue<string>());
        // Valor preferido não é dado do cenário: não aparece como binding.
        Assert.DoesNotContain(baseline.Bindings, b => b.Variable == "request.Observacao");

        // Validação de tamanho: a observação do payload base contradiz o cenário e cede; o resto fica.
        var maxLength = Find(model, ScenarioKinds.Validation, s => s.Title.Contains("501"));
        var m = model.Materialize(maxLength.Id, payload: payload);
        Assert.True(m.Success, m.Error);
        Assert.Equal(37, m.Request!.Body!["veiculoId"]!.GetValue<long>());
        Assert.Equal(501, m.Request.Body["observacao"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task Fato_do_estado_real_impede_reutilizar_registro_incompativel()
    {
        var (model, _, _) = await ModelAsync("POST /api/programacoes");
        var happy = Find(model, ScenarioKinds.Success, s => s.Focus is null);
        var disponivel = model.Variables(happy.Id).First(v => v.Name.EndsWith(".Disponivel"));
        var baseline = model.Materialize(happy.Id, [new ScenarioBinding("request.VeiculoId", "37"), new ScenarioBinding(disponivel.Name, "true")]);

        // Sem novos dados, o cenário "indisponível" herdaria o veículo 37 do baseline, que está disponível.
        var indisponivel = Find(model, ScenarioKinds.Rule, s => s.Expected.Messages.Contains("Veículo indisponível."));
        var m = model.Materialize(indisponivel.Id, baseline: baseline, facts: baseline.Facts);

        Assert.False(m.Success);
        Assert.True(m.NeedsData);
    }

    [Fact]
    public void Matcher_so_confirma_com_status_e_mensagem()
    {
        var scenario = new Scenario
        {
            Id = "CEN-02",
            Kind = ScenarioKinds.Rule,
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 422, Messages = ["Veículo indisponível."], Rule = "!veiculo.Disponivel" },
        };
        var other = new Scenario
        {
            Id = "CEN-03",
            Kind = ScenarioKinds.Rule,
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 422, Messages = ["Data inválida."], Rule = "request.Data < DateTime.Today" },
        };
        var matrix = new ScenarioMatrix { Scenarios = [scenario, other] };
        RuntimeExecution Run(int status, string body) => new() { Id = "EX-01", Status = status, ResponseBody = body };

        Assert.Equal(MatchLevel.Confirmed, OutcomeMatcher.Match(scenario, scenario.Expected, null, Run(422, "{\"erro\":\"Ve\\u00edculo indispon\\u00edvel.\"}"), matrix).Level);
        // Mesmo status, outra regra: não confirma (seria falso positivo).
        var wrongRule = OutcomeMatcher.Match(scenario, scenario.Expected, null, Run(422, "{\"erro\":\"Data inválida.\"}"), matrix);
        Assert.Equal(MatchLevel.Partial, wrongRule.Level);
        Assert.True(wrongRule.Retryable);
        Assert.Equal(MatchLevel.Mismatch, OutcomeMatcher.Match(scenario, scenario.Expected, null, Run(201, ""), matrix).Level);

        // Sem mensagem esperada e status compartilhado com outra regra: inconclusivo.
        var silent = new Scenario { Id = "CEN-04", Kind = ScenarioKinds.Rule, Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 422, Rule = "x" } };
        Assert.Equal(MatchLevel.Partial, OutcomeMatcher.Match(silent, silent.Expected, null, Run(422, "{}"), new ScenarioMatrix { Scenarios = [silent, other] }).Level);
    }

    [Fact]
    public void Matcher_aceita_campo_de_validacao_com_mensagem_padrao()
    {
        var scenario = new Scenario
        {
            Id = "CEN-02",
            Kind = ScenarioKinds.Validation,
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 400, Messages = ["The Placa field is required."], Rule = "placa: [Required]" },
            Notes = ["Mensagem padrão do ASP.NET Core/FluentValidation (pode estar traduzida ou customizada)."],
        };
        var execution = new RuntimeExecution { Id = "EX-01", Status = 400, ResponseBody = "{\"errors\":{\"Placa\":[\"O campo Placa é obrigatório.\"]}}" };

        Assert.Equal(MatchLevel.Confirmed, OutcomeMatcher.Match(scenario, scenario.Expected, null, execution, new ScenarioMatrix { Scenarios = [scenario] }).Level);
    }

    [Fact]
    public void Reconciliador_so_retira_cenario_com_estado_comprovado()
    {
        var target = new Scenario
        {
            Id = "CEN-01", Kind = ScenarioKinds.Rule, Title = "Veículo indisponível",
            Request = new ScenarioRequest { Method = "POST", Url = "/api/programacoes" },
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 422, Messages = ["Veículo indisponível."] },
        };
        var other = new Scenario
        {
            Id = "CEN-02", Kind = ScenarioKinds.Rule,
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 404, Messages = ["Veículo não encontrado."] },
        };
        var matrix = new ScenarioMatrix { Scenarios = [target, other] };

        // A IA diz "inalcançável" depois de duas execuções iguais, explicadas por outra regra.
        Dictionary<string, ScenarioState> States(List<string> unverified)
        {
            var state = new ScenarioState(target) { Verdict = "inalcancavel", Justification = "não há como", Done = true };
            for (var i = 1; i <= 2; i++)
            {
                var m = new ScenarioMaterialization { ScenarioId = "CEN-01", Success = true, Request = target.Request, Expected = target.Expected, UnverifiedState = unverified };
                var e = new RuntimeExecution { Id = $"EX-0{i}", Phase = RuntimePhases.Scenario, ScenarioId = "CEN-01", Method = "POST", Url = "/api/programacoes", Status = 404, ResponseBody = "{\"erro\":\"Veículo não encontrado.\"}" };
                state.Attempts.Add(new Attempt(i, m, e, OutcomeMatcher.Match(target, target.Expected, m, e, matrix)));
            }
            return new() { ["CEN-01"] = state };
        }

        // O veículo usado só "existia" para o solver: o 404 mostra que os dados estavam errados, não que o cenário é impossível.
        var assumed = MatrixReconciler.Reconcile(matrix, States(["veiculos.ObterPorId(999) = {}"]), [], true, new RuntimeOptions());
        Assert.Equal(ScenarioValidationStatuses.Inconclusive, assumed.Scenarios.Single(s => s.Id == "CEN-01").Status);
        Assert.Empty(assumed.Removed);

        var proven = MatrixReconciler.Reconcile(matrix, States([]), [], true, new RuntimeOptions());
        Assert.Equal(ScenarioValidationStatuses.Unreachable, Assert.Single(proven.Removed).Status);
    }

    [Fact]
    public void Catalogo_reconhece_rotas_com_parametros()
    {
        var catalog = EndpointCatalog.Of([
            new CatalogEndpoint { Method = "GET", Route = "/api/programacoes/{id}" },
            new CatalogEndpoint { Method = "GET", Route = "/api/programacoes/novas" },
            new CatalogEndpoint { Method = "PATCH", Route = "/api/programacoes/{id:int}/finalizar" },
        ]);

        Assert.Equal("/api/programacoes/{id}", catalog.Match("GET", "/api/programacoes/37?x=1")?.Route);
        Assert.Equal("/api/programacoes/novas", catalog.Match("get", "api/programacoes/novas")?.Route);
        Assert.Equal("/api/programacoes/{id:int}/finalizar", catalog.Match("PATCH", "/api/programacoes/5/finalizar")?.Route);
        Assert.Null(catalog.Match("DELETE", "/api/programacoes/5"));
        Assert.Null(catalog.Match("GET", "/api/outra"));
    }

    [Fact]
    public async Task Catalogo_inclui_endpoints_de_leitura_com_parametros_e_dtos()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");
        var catalog = await EndpointCatalog.BuildAsync(await fixture.Solutions.GetAsync(fixture.SolutionPath), endpoint);

        var obter = Assert.Single(catalog.Endpoints, e => e is { Method: "GET", Route: "/api/programacoes/{id}" });
        Assert.Contains(obter.Parameters, p => p is { Name: "id", Location: "route" });
        var criar = Assert.Single(catalog.Endpoints, e => e.IsTarget);
        Assert.Contains("veiculoId", criar.Body);
        Assert.Contains(catalog.Endpoints, e => e is { Method: "POST", Route: "/api/veiculos" });
    }
}

/// <summary>Validação de ponta a ponta: sobe a SampleApi, executa os cenários e reconcilia a matriz.</summary>
[Collection(SampleSolutionCollection.Name)]
public class RuntimeValidationTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Valida_a_matriz_com_a_api_em_execucao_sem_falso_positivo()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/veiculos");

        // A IA falsa não responde às etapas de runtime: o que for confirmado vem só do solver + execução real.
        var report = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint, useAi: true, validateRuntime: true);

        var runtime = Assert.IsType<RuntimeValidation>(report.Runtime);
        Assert.True(runtime.Status != RuntimeValidationStatuses.Failed, runtime.Error + "\n" + string.Join("\n", runtime.Log));
        Assert.True(runtime.WritesAllowed);
        var matrix = Assert.IsType<ValidatedMatrix>(runtime.Matrix);
        var byId = matrix.Scenarios.ToDictionary(s => s.Id);
        var statics = report.Context.Scenarios!.Scenarios;

        // Caminho feliz, validações e a regra de placa duplicada (estado criado pelo baseline) são reproduzidos.
        var happy = statics.First(s => s.Kind == ScenarioKinds.Success && s.Focus is null);
        Assert.Equal(ScenarioValidationStatuses.Confirmed, byId[happy.Id].Status);
        foreach (var validation in statics.Where(s => s.Kind == ScenarioKinds.Validation))
            Assert.True(byId[validation.Id].Status == ScenarioValidationStatuses.Confirmed, $"{validation.Id}: {string.Join(" ", byId[validation.Id].Reasons)}");
        var duplicate = statics.First(s => s.Expected.Messages.Contains("Já existe um veículo com esta placa."));
        Assert.Equal(ScenarioValidationStatuses.Confirmed, byId[duplicate.Id].Status);

        // Todo cenário confirmado tem a execução, o payload e o resultado real registrados.
        foreach (var confirmed in matrix.Scenarios.Where(s => s.Status == ScenarioValidationStatuses.Confirmed))
        {
            var execution = Assert.Single(runtime.Executions, e => e.Id == confirmed.ExecutionId);
            Assert.Equal(confirmed.Expected.HttpStatus, execution.Status);
            Assert.NotNull(confirmed.Request);
            Assert.NotEmpty(confirmed.Evidence);
        }
        // Nada é retirado sem evidência.
        Assert.Empty(matrix.Removed);
        Assert.Equal(statics.Count, matrix.Scenarios.Count(s => s.Status != ScenarioValidationStatuses.Discovered));

        var markdown = ReportRenderer.Markdown(report);
        Assert.Contains("Matriz de cenários (validada em runtime)", markdown);
        Assert.Contains("CONFIRMADO", markdown);
    }

    [Fact]
    public async Task Sem_ia_nao_inicia_a_api()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/veiculos");

        var report = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint, useAi: false, validateRuntime: true);

        Assert.Null(report.Runtime);
        Assert.NotNull(report.Context.Scenarios);
    }

    [Fact]
    public async Task Servico_que_nao_sobe_mantem_a_matriz_estatica()
    {
        var options = new RuntimeOptions { Project = "samples/NaoExiste/NaoExiste.csproj" };
        var service = new RuntimeValidationService(options, new AppRunner(options));
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/veiculos");
        var context = await fixture.Service.BuildContextAsync(fixture.SolutionPath, endpoint);
        var solution = await fixture.Solutions.GetAsync(fixture.SolutionPath);
        var graph = await new CallGraphBuilder(new AnalyzerOptions(), new MethodResolver()).BuildAsync(solution, endpoint);
        var model = await new ScenarioGenerator().GenerateModelAsync(graph, context);

        var runtime = await service.ValidateAsync(solution, context, model, fixture.Ai);

        Assert.Equal(RuntimeValidationStatuses.Failed, runtime.Status);
        Assert.Contains("não encontrado", runtime.Error);
        Assert.Null(runtime.Matrix);
        Assert.Empty(runtime.Executions);
    }
}
