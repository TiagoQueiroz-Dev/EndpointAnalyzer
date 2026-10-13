using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Tests;

/// <summary>GET /api/analysis/diff (EndpointAnalysisService.Diff): versões salvas no SQLite, sem IA e sem nova análise.</summary>
public class AnalysisDiffApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-diff-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAiProvider _ai = new();
    private readonly AdhocWorkspace _workspace = new();

    private ServiceProvider Services() => new ServiceCollection()
        .AddEndpointAnalyzer(configureRuntime: o => o.AnalysisDatabasePath = Path.Combine(_directory, "analyses.db"))
        .AddSingleton<IAiProviderSelector>(new SingleAiProviderSelector(_ai))
        .BuildServiceProvider();

    public void Dispose()
    {
        _workspace.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private LoadedSolution Solution => new() { Path = Path.Combine(_directory, "Api.sln"), RootDirectory = _directory, Solution = _workspace.CurrentSolution };

    private static EndpointAnalysisReport Report(int status, string route = "/api/pedidos") => new()
    {
        Context = new EndpointAnalysisContext
        {
            Endpoint = new EndpointInfo { HttpMethod = "POST", Route = route, Project = "Api" },
            Scenarios = new ScenarioMatrix
            {
                Scenarios =
                [
                    new Scenario { Id = "CEN-01", Kind = ScenarioKinds.Success, Expected = new ScenarioExpectation { Outcome = "sucesso", HttpStatus = 201 } },
                    new Scenario
                    {
                        Id = "CEN-02", Kind = ScenarioKinds.Rule, Focus = new ScenarioFocus { Expression = "!pedido.Aberto" },
                        Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = status, Messages = ["Pedido fechado."] },
                    },
                ],
            },
        },
    };

    private string Add(ServiceProvider services, EndpointAnalysisReport report)
    {
        services.GetRequiredService<AnalysisSessionStore>().Add(Solution, null, report, "fonte", null);
        return report.AnalysisId!;
    }

    [Fact]
    public void Compara_com_a_versao_anterior_mesmo_depois_de_reiniciar_sem_chamar_a_ia()
    {
        string first, second, third;
        using (var services = Services())
        {
            first = Add(services, Report(422));
            var service = services.GetRequiredService<EndpointAnalysisService>();
            var initial = service.Diff(first);
            Assert.False(initial.HasBase);
            Assert.Equal("Nenhuma análise anterior para comparar.", initial.Message);
            second = Add(services, Report(409));
            third = Add(services, Report(409));
        }

        using (var restarted = Services())
        {
            var service = restarted.GetRequiredService<EndpointAnalysisService>();
            var diff = service.Diff(second);
            Assert.Equal((first, second), (diff.Base!.AnalysisId, diff.Current.AnalysisId));
            var item = Assert.Single(diff.Sections.Single(s => s.Id == "scenarios").Groups.Single(g => g.Id == "matriz-estatica").Items);
            Assert.Equal((DiffKinds.Changed, "esperado.http"), (item.Kind, Assert.Single(item.Fields).Path));

            var unchanged = service.Diff(third);
            Assert.Equal(second, unchanged.Base!.AnalysisId);
            Assert.False(unchanged.HasDifferences);
            Assert.Equal("Nenhuma diferença encontrada.", unchanged.Message);

            // Base explícita: versões históricas.
            Assert.Equal(first, service.Diff(third, first).Base!.AnalysisId);
            Assert.True(service.Diff(third, first).HasDifferences);
        }
        Assert.Equal(0, _ai.Calls);
    }

    [Fact]
    public void Recusa_analise_inexistente_base_igual_e_endpoints_diferentes()
    {
        using var services = Services();
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var current = Add(services, Report(422));
        var other = Add(services, Report(422, "/api/outro"));

        Assert.Equal(404, Assert.Throws<AnalysisSessionException>(() => service.Diff("nao-existe")).StatusCode);
        Assert.Equal(404, Assert.Throws<AnalysisSessionException>(() => service.Diff(current, "nao-existe")).StatusCode);
        Assert.Throws<ArgumentException>(() => service.Diff(current, current));
        Assert.Throws<ArgumentException>(() => service.Diff(current, other));
        Assert.Throws<ArgumentException>(() => service.Diff(" "));
    }
}
