using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class ManualRevalidationTests(SampleSolutionFixture fixture) : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-manual-tests", Guid.NewGuid().ToString("N"));
    private IServiceCollection Services(Action<RuntimeOptions>? configure = null) => new ServiceCollection().AddEndpointAnalyzer(configureRuntime: o =>
    {
        o.AnalysisDatabasePath = Path.Combine(_storageDirectory, "analyses.db");
        configure?.Invoke(o);
    });
    public void Dispose() { if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true); }
    private async Task<(LoadedSolution Solution, EndpointAnalysisContext Context, ScenarioModel Model, Scenario Scenario)> SetupAsync()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var solution = await fixture.Solutions.GetAsync(fixture.SolutionPath);
        var graph = await new CallGraphBuilder(new AnalyzerOptions(), new MethodResolver()).BuildAsync(solution, context.Endpoint);
        var model = await new ScenarioGenerator().GenerateModelAsync(graph, context);
        return (solution, context, model, model.Matrix.Scenarios.First(s => s.Title.Contains("501")));
    }

    private static RuntimeValidation Report(ScenarioModel model) => new()
    {
        Status = RuntimeValidationStatuses.Partial,
        Stats = new RuntimeStats { AiCalls = 7 },
        Matrix = new ValidatedMatrix
        {
            Scenarios = model.Matrix.Scenarios.Select(s => new ValidatedScenario
            {
                Id = s.Id, Kind = s.Kind, Title = s.Title, Expected = s.Expected,
                Request = s.Request, Preconditions = s.Preconditions, Status = ScenarioValidationStatuses.Inconclusive,
            }).ToList(),
        },
    };

    private static JsonNode Body(Scenario scenario, int length = 501)
    {
        var body = scenario.Request.Body!.DeepClone();
        body["observacao"] = new string('x', length);
        return body;
    }

    [Fact]
    public async Task Valida_o_json_exato_sem_completar_ou_alterar_campos()
    {
        var (_, _, model, scenario) = await SetupAsync();
        var body = Body(scenario);
        body["extra"] = new JsonObject { ["valor"] = 37 };
        var request = new ScenarioRequest { Method = scenario.Request.Method, Url = scenario.Request.Url, Body = body };
        var before = body.ToJsonString();
        var result = model.ValidateRequest(scenario.Id, request);
        Assert.True(result.Success, result.Error);
        Assert.Same(request, result.Request);
        Assert.Equal(before, result.Request!.Body!.ToJsonString());
        request.Body = Body(scenario, 20);
        Assert.False(model.ValidateRequest(scenario.Id, request).Success);
        request.Body = Body(scenario);
        request.Body["veiculoId"] = "37"; // Número no JSON é diferente de uma string conversível.
        Assert.False(model.ValidateRequest(scenario.Id, request).Success);
        request.Body = Body(scenario);
        request.Body["data"] = "9 outubro 2026";
        Assert.False(model.ValidateRequest(scenario.Id, request).Success);
        request.Body["data"] = DateTime.Today.AddDays(1).AddHours(12).ToString("O");
        Assert.True(model.ValidateRequest(scenario.Id, request).Success);
    }

    [Fact]
    public async Task Nao_usa_estado_antigo_ou_suposicao_do_solver_como_prova()
    {
        var (_, _, model, _) = await SetupAsync();
        var scenario = model.Matrix.Scenarios.First(s => s.Expected.Messages.Contains("Veículo indisponível."));
        var result = model.ValidateRequest(scenario.Id, scenario.Request);
        Assert.False(result.Success);
        Assert.Contains("comprovar", result.Error);
    }

    [Fact]
    public async Task Cenario_de_tipo_invalido_preserva_o_campo_invalido_e_recusa_um_tipo_valido()
    {
        var (_, _, model, _) = await SetupAsync();
        var scenario = model.Matrix.Scenarios.First(s => s.Expected.Rule == "model binding: tipo do JSON incompatível");
        Assert.True(model.ValidateRequest(scenario.Id, scenario.Request).Success);
        var happy = model.Matrix.Scenarios.First(s => s.Kind == ScenarioKinds.Success && s.Focus is null);
        Assert.False(model.ValidateRequest(scenario.Id, happy.Request).Success);
    }

    [Theory]
    [InlineData(400, true, "confirmado")]
    [InlineData(400, false, "inconclusivo")]
    [InlineData(422, false, "inconclusivo")]
    public async Task Executa_uma_requisicao_sem_ia_e_preserva_os_demais_cenarios(int status, bool correctMessage, string expectedStatus)
    {
        var (_, _, model, scenario) = await SetupAsync();
        var report = Report(model);
        var untouched = report.Matrix!.Scenarios.Where(s => s.Id != scenario.Id).ToDictionary(s => s.Id, s => JsonSerializer.Serialize(s));
        var originalExpectation = JsonSerializer.Serialize(report.Matrix.Scenarios.First(s => s.Id == scenario.Id).Expected);
        var handler = new RecordingHandler(status, JsonSerializer.Serialize(new { error = correctMessage ? scenario.Expected.Messages[0] : "Outra regra falhou." }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions { MaxRequests = 0 }; // Exploração automática esgotada não bloqueia a tentativa manual.
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report, requestBudget: 1);
        var runtime = new RuntimeValidationService(options, new FailingRunner());
        var body = Body(scenario);
        var execution = await runtime.ExecuteManualAsync(model, report, scenario.Id, body, executor);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, report.Stats.Requests);
        Assert.Equal(7, report.Stats.AiCalls);
        Assert.Equal(body.ToJsonString(), handler.Body);
        Assert.Equal(scenario.Request.Method, handler.Method);
        Assert.Equal(scenario.Request.Url, handler.Url);
        Assert.Equal(RuntimePhases.Manual, execution.Phase);
        var updated = report.Matrix.Scenarios.First(s => s.Id == scenario.Id);
        Assert.Equal(expectedStatus, updated.Status);
        Assert.Equal(1, updated.Attempts);
        Assert.Equal(execution.Id, updated.ExecutionId);
        Assert.Equal(originalExpectation, JsonSerializer.Serialize(updated.Expected));
        foreach (var other in report.Matrix.Scenarios.Where(s => s.Id != scenario.Id)) Assert.Equal(untouched[other.Id], JsonSerializer.Serialize(other));
        Assert.Equal(expectedStatus == "confirmado" ? 1 : 0, report.Matrix.Counts[ScenarioValidationStatuses.Confirmed]);
        Assert.NotNull(execution.MatchLevel);
        if (!correctMessage) Assert.NotEmpty(execution.Reasons);
    }

    [Fact]
    public async Task Executa_payload_que_contradiz_o_cenario_e_mantem_historico_das_tentativas()
    {
        var (_, _, model, scenario) = await SetupAsync();
        var report = Report(model);
        var handler = new RecordingHandler(400, "{\"error\":\"outra regra\"}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report);
        var runtime = new RuntimeValidationService(options, new FailingRunner());
        var contradictoryBody = Body(scenario, 5);
        await runtime.ExecuteManualAsync(model, report, scenario.Id, contradictoryBody, executor);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(contradictoryBody.ToJsonString(), handler.Body);
        Assert.Single(report.Executions);
        Assert.Equal(ScenarioValidationStatuses.Inconclusive, report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
        var firstBody = Body(scenario);
        firstBody["tentativa"] = 1;
        await runtime.ExecuteManualAsync(model, report, scenario.Id, firstBody, executor);
        var first = JsonSerializer.Serialize(report.Executions[1]);
        var secondBody = Body(scenario);
        secondBody["tentativa"] = 2;
        await runtime.ExecuteManualAsync(model, report, scenario.Id, secondBody, executor);
        Assert.Equal(first, JsonSerializer.Serialize(report.Executions[1]));
        Assert.Equal(3, handler.Calls);
        Assert.Equal(3, report.Executions.Count);
        Assert.Equal(3, report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Estado_desconhecido_nao_bloqueia_http_e_resultado_depende_da_resposta(bool correctMessage)
    {
        var (_, _, model, _) = await SetupAsync();
        var scenario = model.Matrix.Scenarios.First(s => s.Expected.Messages.Contains("Veículo indisponível."));
        var body = scenario.Request.Body!.DeepClone();
        body["veiculoId"] = 136;
        var report = Report(model);
        var expected = JsonSerializer.Serialize(report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Expected);
        var handler = new RecordingHandler(scenario.Expected.HttpStatus!.Value,
            JsonSerializer.Serialize(new { error = correctMessage ? "Veículo indisponível." : "Outra regra falhou." }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report, requestBudget: 1);
        var runtime = new RuntimeValidationService(options, new FailingRunner());

        await runtime.ExecuteManualAsync(model, report, scenario.Id, body, executor);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(body.ToJsonString(), handler.Body);
        Assert.Equal(7, report.Stats.AiCalls);
        var updated = report.Matrix.Scenarios.First(s => s.Id == scenario.Id);
        Assert.Equal(correctMessage ? ScenarioValidationStatuses.Confirmed : ScenarioValidationStatuses.Inconclusive, updated.Status);
        Assert.Equal(expected, JsonSerializer.Serialize(updated.Expected));
        Assert.Empty(updated.Bindings);
        Assert.Empty(updated.Preconditions);
    }

    [Fact]
    public async Task Tipo_incompativel_no_payload_manual_e_enviado_para_a_api()
    {
        var (_, _, model, scenario) = await SetupAsync();
        var body = Body(scenario);
        body["veiculoId"] = "abc";
        var report = Report(model);
        var handler = new RecordingHandler(400, "{\"error\":\"Erro de conversão do JSON.\"}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report, requestBudget: 1);

        await new RuntimeValidationService(options, new FailingRunner()).ExecuteManualAsync(model, report, scenario.Id, body, executor);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(body.ToJsonString(), handler.Body);
        Assert.Equal(ScenarioValidationStatuses.Inconclusive, report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
    }

    [Fact]
    public async Task Bloqueios_e_api_indisponivel_produzem_erros_controlados()
    {
        var (solution, context, model, scenario) = await SetupAsync();
        var runner = new FailingRunner();
        var options = new RuntimeOptions { AllowWrites = false };
        var runtime = new RuntimeValidationService(options, runner);
        var report = Report(model);
        var error = await Assert.ThrowsAsync<ScenarioRevalidationException>(() => runtime.RevalidateScenarioAsync(solution, context, model, report, scenario.Id, Body(scenario)));
        Assert.Equal(403, error.StatusCode);
        Assert.Equal(0, runner.Calls);
        options.AllowWrites = true;
        error = await Assert.ThrowsAsync<ScenarioRevalidationException>(() => runtime.RevalidateScenarioAsync(solution, context, model, report, scenario.Id, Body(scenario)));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(report.Executions);
        report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status = ScenarioValidationStatuses.Confirmed;
        error = await Assert.ThrowsAsync<ScenarioRevalidationException>(() => runtime.RevalidateScenarioAsync(solution, context, model, report, scenario.Id, Body(scenario)));
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task Sessao_controla_concorrencia_expiracao_orcamento_e_json_duplicado()
    {
        var (solution, context, model, scenario) = await SetupAsync();
        using var services = Services(o => o.MaxManualRequests = 0)
            .AddSingleton<IAppRunner>(new FailingRunner()).BuildServiceProvider();
        var store = services.GetRequiredService<AnalysisSessionStore>();
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var report = new EndpointAnalysisReport { Context = context, Runtime = Report(model) };
        store.Add(solution, model, report, AnalysisSourceVersion.Capture(solution), "super-secret-token");
        Assert.DoesNotContain("super-secret-token", JsonSerializer.Serialize(report));
        var body = JsonSerializer.SerializeToElement(Body(scenario));
        var expired = await Assert.ThrowsAsync<AnalysisSessionException>(() => service.RevalidateScenarioAsync("unknown", scenario.Id, body));
        Assert.Equal(404, expired.StatusCode);
        var session = store.Get(report.AnalysisId!);
        await session.Gate.WaitAsync();
        try
        {
            var busy = await Assert.ThrowsAsync<AnalysisSessionException>(() => service.RevalidateScenarioAsync(session.Id, scenario.Id, body));
            Assert.Equal(409, busy.StatusCode);
        }
        finally { session.Gate.Release(); }
        var limited = await Assert.ThrowsAsync<AnalysisSessionException>(() => service.RevalidateScenarioAsync(session.Id, scenario.Id, body));
        Assert.Equal(429, limited.StatusCode);
        using var duplicated = JsonDocument.Parse("{\"data\":1,\"Data\":2}");
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevalidateScenarioAsync(session.Id, scenario.Id, duplicated.RootElement));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RevalidateScenarioAsync(session.Id, scenario.Id, default));
    }

    [Fact]
    public async Task Reanalise_com_a_sample_api_confirma_sem_chamar_ia()
    {
        var (solution, context, model, scenario) = await SetupAsync();
        using var services = Services().BuildServiceProvider();
        var store = services.GetRequiredService<AnalysisSessionStore>();
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var report = new EndpointAnalysisReport { Context = context, Runtime = Report(model) };
        store.Add(solution, model, report, AnalysisSourceVersion.Capture(solution), null);
        var result = await service.RevalidateScenarioAsync(report.AnalysisId!, scenario.Id, JsonSerializer.SerializeToElement(Body(scenario)));
        Assert.Equal(ScenarioValidationStatuses.Confirmed, result.Runtime.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
        Assert.Single(result.Runtime.Executions);
        Assert.Equal(1, result.Runtime.Stats.Requests);
        Assert.Equal(7, result.Runtime.Stats.AiCalls);
        Assert.Contains("CONFIRMADO", result.Markdown);
    }

    [Fact]
    public async Task Reanalise_com_estado_desconhecido_sobe_sample_api_e_registra_resposta()
    {
        var (solution, context, model, _) = await SetupAsync();
        var scenario = model.Matrix.Scenarios.First(s => s.Expected.Messages.Contains("Veículo indisponível."));
        var body = scenario.Request.Body!.DeepClone();
        body["veiculoId"] = 136;
        using var services = Services().BuildServiceProvider();
        var report = new EndpointAnalysisReport { Context = context, Runtime = Report(model) };
        services.GetRequiredService<AnalysisSessionStore>().Add(solution, model, report, AnalysisSourceVersion.Capture(solution), null);

        var result = await services.GetRequiredService<EndpointAnalysisService>().RevalidateScenarioAsync(
            report.AnalysisId!, scenario.Id, JsonSerializer.SerializeToElement(body));

        var execution = Assert.Single(result.Runtime.Executions);
        Assert.False(execution.Blocked);
        Assert.NotNull(execution.Status);
        Assert.Equal(body.ToJsonString(), execution.RequestBody!.ToJsonString());
        Assert.Equal(ScenarioValidationStatuses.Inconclusive, result.Runtime.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
        Assert.Equal(1, result.Runtime.Stats.Requests);
        Assert.Equal(7, result.Runtime.Stats.AiCalls);
    }

    [Fact]
    public async Task Expiracao_e_limite_da_memoria_nao_apagam_as_analises_persistidas()
    {
        var (solution, context, model, _) = await SetupAsync();
        var clock = new TestClock();
        using var services = Services(o => { o.MaxAnalysisSessions = 1; o.AnalysisSessionMinutes = 1; }).BuildServiceProvider();
        using var store = new AnalysisSessionStore(services.GetRequiredService<RuntimeOptions>(), services.GetRequiredService<SqliteAnalysisStore>(), clock);
        var first = new EndpointAnalysisReport { Context = context };
        store.Add(solution, model, first, "version", "token");
        var second = new EndpointAnalysisReport { Context = context };
        store.Add(solution, model, second, "version", null);
        Assert.Equal(first.AnalysisId, store.Get(first.AnalysisId!).Report.AnalysisId);
        Assert.Equal(second.AnalysisId, store.Get(second.AnalysisId!).Report.AnalysisId);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(second.AnalysisId, store.Get(second.AnalysisId!).Report.AnalysisId);
        Assert.Null(second.SessionExpiresAt);
    }

    [Fact]
    public async Task Apos_reinicio_reanalisa_cenario_salvo_com_a_sample_api_sem_ia()
    {
        var (solution, context, model, scenario) = await SetupAsync();
        var report = new EndpointAnalysisReport { Context = context, Runtime = Report(model) };
        using (var first = Services().BuildServiceProvider())
            first.GetRequiredService<AnalysisSessionStore>().Add(solution, model, report, "previous-version", "same-token-after-restart");

        using (var second = Services().BuildServiceProvider())
        {
            var result = await second.GetRequiredService<EndpointAnalysisService>().RevalidateScenarioAsync(report.AnalysisId!, scenario.Id,
                JsonSerializer.SerializeToElement(Body(scenario)));
            Assert.Equal(ScenarioValidationStatuses.Confirmed, result.Runtime.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
            Assert.Equal(7, result.Runtime.Stats.AiCalls);
            Assert.Equal(1, result.Runtime.Stats.Requests);
            Assert.NotNull(Assert.Single(result.Runtime.Executions).SourceFingerprint);
        }
        using var third = Services().BuildServiceProvider();
        var restored = third.GetRequiredService<AnalysisSessionStore>().Get(report.AnalysisId!);
        Assert.Equal("same-token-after-restart", restored.Token);
        Assert.Equal(1, restored.ManualRequests);
        Assert.Equal(ScenarioValidationStatuses.Confirmed, restored.Report.Runtime!.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Status);
        Assert.Single(restored.Report.Runtime.Executions);
    }

    [Fact]
    public async Task Importa_relatorio_antigo_sem_ia_e_nao_sobrescreve_dados_mais_novos()
    {
        var (_, context, model, _) = await SetupAsync();
        var report = new EndpointAnalysisReport { AnalysisId = "old-memory-session", Context = context, Runtime = Report(model), SessionExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        context.Scenarios = model.Matrix;
        using var services = Services().BuildServiceProvider();
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var imported = await service.RestoreAnalysisAsync(fixture.SolutionPath, report, "saved-token");
        Assert.NotEqual("old-memory-session", imported.AnalysisId);
        Assert.Null(imported.SessionExpiresAt);
        Assert.Equal(7, imported.Runtime!.Stats.AiCalls);
        var oldCopy = JsonSerializer.Deserialize<EndpointAnalysisReport>(JsonSerializer.Serialize(imported))!;
        var store = services.GetRequiredService<AnalysisSessionStore>();
        var current = store.Get(imported.AnalysisId!);
        current.Report.Runtime!.Notes.Add("newer history");
        store.Save(current);
        var again = await service.RestoreAnalysisAsync(fixture.SolutionPath, oldCopy);
        Assert.Contains("newer history", again.Runtime!.Notes);
        Assert.Equal("saved-token", store.Get(again.AnalysisId!).Token);
    }

    [Fact]
    public async Task Alteracoes_locais_recarregam_o_projeto_e_iniciam_a_api_sem_refazer_cenarios()
    {
        var (original, context, model, scenario) = await SetupAsync();
        var directory = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-manual-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Api.csproj");
        await File.WriteAllTextAsync(path, "<Project />");
        var solution = new LoadedSolution { Path = path, RootDirectory = directory, Solution = original.Solution };
        try
        {
            var loader = new CountingLoader(original.Solution);
            using var services = Services().AddSingleton<ISolutionLoader>(loader).AddSingleton<IAppRunner>(new FailingRunner()).BuildServiceProvider();
            var store = services.GetRequiredService<AnalysisSessionStore>();
            var service = services.GetRequiredService<EndpointAnalysisService>();
            var report = new EndpointAnalysisReport { Context = context, Runtime = Report(model) };
            var fingerprint = AnalysisSourceVersion.Capture(solution);
            store.Add(solution, model, report, fingerprint, null);
            await File.WriteAllTextAsync(Path.Combine(directory, "NewRule.cs"), "class NewRule {}");
            Assert.NotEqual(fingerprint, AnalysisSourceVersion.Capture(solution));
            var error = await Assert.ThrowsAsync<ScenarioRevalidationException>(() => service.RevalidateScenarioAsync(report.AnalysisId!, scenario.Id, JsonSerializer.SerializeToElement(Body(scenario))));
            Assert.Equal(503, error.StatusCode); // Chegou ao runner; a API indisponível do teste é o único bloqueio.
            Assert.Equal(1, ((FailingRunner)services.GetRequiredService<IAppRunner>()).Calls);
            Assert.Equal(1, loader.Calls);
            Assert.Equal(7, report.Runtime!.Stats.AiCalls);
            await File.WriteAllTextAsync(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Assert.NotEqual(fingerprint, AnalysisSourceVersion.Capture(solution));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Token_e_headers_sensiveis_nao_aparecem_no_historico()
    {
        var (_, _, model, scenario) = await SetupAsync();
        var report = Report(model);
        const string secret = "private-session-token";
        var handler = new RecordingHandler(400, JsonSerializer.Serialize(new { error = scenario.Expected.Messages[0], echo = secret }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report, "Bearer " + secret);
        var runtime = new RuntimeValidationService(options, new FailingRunner());
        await runtime.ExecuteManualAsync(model, report, scenario.Id, Body(scenario), executor);
        Assert.Equal("Bearer " + secret, handler.Authorization);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Cancelamento_preserva_a_tentativa_e_nao_confirma_o_cenario()
    {
        var (_, _, model, scenario) = await SetupAsync();
        var report = Report(model);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new CancellingHandler(cancellation)) { BaseAddress = new Uri("http://localhost:12345/") };
        var options = new RuntimeOptions();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = scenario.Request.Method, Route = scenario.Request.Url }]);
        var executor = new ScenarioExecutor(client, options, catalog, true, report);
        var runtime = new RuntimeValidationService(options, new FailingRunner());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteManualAsync(model, report, scenario.Id, Body(scenario), executor, cancellation.Token));
        var execution = Assert.Single(report.Executions);
        Assert.Contains("cancelada", execution.Exception);
        Assert.Equal("Mismatch", execution.MatchLevel);
        Assert.Equal(1, report.Stats.Requests);
        Assert.Equal(1, report.Matrix!.Scenarios.First(s => s.Id == scenario.Id).Attempts);
        Assert.Equal(ScenarioValidationStatuses.Inconclusive, report.Matrix.Scenarios.First(s => s.Id == scenario.Id).Status);
    }

    [Fact]
    public async Task Cache_recarrega_apos_arquivo_novo_e_reutiliza_fontes_iguais()
    {
        var directory = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-manual-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var workspace = new AdhocWorkspace();
        var path = Path.Combine(directory, "Api.csproj");
        await File.WriteAllTextAsync(path, "<Project />");
        try
        {
            var loader = new CountingLoader(workspace.CurrentSolution);
            var cache = new SolutionCache(loader);
            var first = await cache.GetCurrentAsync(path);
            Assert.Same(first, await cache.GetCurrentAsync(path));
            Assert.Equal(1, loader.Calls);
            await File.WriteAllTextAsync(Path.Combine(directory, "Rule.cs"), "class Rule {}");
            Assert.NotSame(first, await cache.GetCurrentAsync(path));
            Assert.Equal(2, loader.Calls);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Lista_com_elemento_de_tipo_invalido_nao_se_confunde_com_validacao_de_tamanho()
    {
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var project = workspace.AddProject("ManualApi", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(references).AddDocument("Api.cs", """
                using System.Collections.Generic;
                using System.ComponentModel.DataAnnotations;
                using Microsoft.AspNetCore.Mvc;
                [ApiController, Route("manual")]
                public class ManualController : ControllerBase
                {
                    [HttpPost]
                    public IActionResult Post([FromBody] Request request) => Ok();
                }
                public class Request
                {
                    [MinLength(2)] public List<int> Items { get; set; } = new();
                }
                """).Project;
        var solution = new LoadedSolution { Solution = project.Solution, Path = "ManualApi.csproj", RootDirectory = Directory.GetCurrentDirectory() };
        var endpoint = Assert.Single(await new EndpointScanner(new AnalyzerOptions()).ScanAsync(solution));
        var graph = await new CallGraphBuilder(new AnalyzerOptions(), new MethodResolver()).BuildAsync(solution, endpoint);
        var model = await new ScenarioGenerator().GenerateModelAsync(graph, new EndpointAnalysisContext { Endpoint = endpoint });
        var scenario = model.Matrix.Scenarios.First(s => s.Kind == ScenarioKinds.Validation && s.Request.Body?["items"] is JsonArray { Count: 1 });
        var body = scenario.Request.Body!.DeepClone();
        var request = new ScenarioRequest { Method = scenario.Request.Method, Url = scenario.Request.Url, Body = body };
        Assert.True(model.ValidateRequest(scenario.Id, request).Success);
        body["items"]![0] = "abc";
        var invalid = model.ValidateRequest(scenario.Id, request);
        Assert.False(invalid.Success);
        Assert.Contains("tipo", invalid.Error);
    }

    private sealed class CountingLoader(Solution solution) : ISolutionLoader
    {
        public int Calls { get; private set; }
        public Task<LoadedSolution> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LoadedSolution { Path = path, RootDirectory = Path.GetDirectoryName(path)!, Solution = solution });
        }
    }

    private sealed class CancellingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailingRunner : IAppRunner
    {
        public int Calls { get; private set; }
        public Task<RunningApp> StartAsync(LoadedSolution solution, EndpointInfo endpoint, Action<string> log, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new RuntimeStartException("API indisponível para o teste");
        }
    }

    private sealed class RecordingHandler(int status, string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Method = request.Method.Method;
            Url = request.RequestUri!.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(response) };
        }
    }
}
