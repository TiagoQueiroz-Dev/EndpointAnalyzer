using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Tests;

public class SqliteAnalysisStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-sqlite-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "analyses.db");
    private ServiceProvider Services() => new ServiceCollection().AddEndpointAnalyzer(configureRuntime: o => o.AnalysisDatabasePath = DatabasePath).BuildServiceProvider();
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private static EndpointAnalysisReport Report() => new()
    {
        Context = new EndpointAnalysisContext
        {
            Endpoint = new EndpointInfo { HttpMethod = "POST", Route = "/veiculo/v1", Project = "SampleApi" },
            Scenarios = new ScenarioMatrix
            {
                Scenarios = [new Scenario { Id = "CEN-02", Kind = ScenarioKinds.Validation, Expected = new ScenarioExpectation { HttpStatus = 400 } }],
            },
        },
        Runtime = new RuntimeValidation { Matrix = new ValidatedMatrix { Scenarios = [new ValidatedScenario { Id = "CEN-02", Status = ScenarioValidationStatuses.Inconclusive }] } },
    };

    [Fact]
    public void Reinicio_preserva_id_relatorio_historico_orcamento_e_token_criptografado()
    {
        const string token = "secret-token-for-restart-test";
        using var workspace = new AdhocWorkspace();
        var solution = new LoadedSolution { Path = Path.Combine(_directory, "SampleApi.sln"), RootDirectory = _directory, Solution = workspace.CurrentSolution };
        var report = Report();
        using (var first = Services())
        {
            var store = first.GetRequiredService<AnalysisSessionStore>();
            store.Add(solution, null, report, "original-source", token);
            var session = store.Acquire(report.AnalysisId!);
            try
            {
                session.ManualRequests = 1;
                report.Runtime!.Executions.Add(new RuntimeExecution
                {
                    Id = "EX-01", ScenarioId = "CEN-02", Phase = RuntimePhases.Manual,
                    Status = 400, ResponseBody = "{\"errors\":[\"tipo inválido\"]}", SourceFingerprint = "corrected-source",
                });
                store.Save(session);
            }
            finally { session.Gate.Release(); }
        }
        using (var second = Services())
        {
            var session = second.GetRequiredService<AnalysisSessionStore>().Get(report.AnalysisId!);
            Assert.Equal(report.AnalysisId, session.Id);
            Assert.Equal(token, session.Token);
            Assert.Equal(1, session.ManualRequests);
            Assert.Equal("original-source", session.Fingerprint);
            Assert.Null(session.Report.SessionExpiresAt);
            Assert.False(session.Model.CanMaterialize); // Reabertura não depende do solver.
            Assert.Equal("CEN-02", Assert.Single(session.Model.Matrix.Scenarios).Id);
            Assert.Equal("corrected-source", Assert.Single(session.Report.Runtime!.Executions).SourceFingerprint);
            Assert.DoesNotContain(token, JsonSerializer.Serialize(session.Report));
            Assert.Equal(session.Id, second.GetRequiredService<SqliteAnalysisStore>().LatestId(solution.Path, "POST", "/veiculo/v1", "SampleApi"));
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_api_token FROM analysis_sessions;";
        var encrypted = Assert.IsType<string>(command.ExecuteScalar());
        Assert.NotEqual(token, encrypted);
        Assert.DoesNotContain(token, encrypted);
        command.CommandText = "SELECT COUNT(*) FROM manual_executions;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT report_json FROM analyses;";
        Assert.DoesNotContain(token, Assert.IsType<string>(command.ExecuteScalar()));
        connection.Close();
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(File.ReadAllBytes(DatabasePath)));
    }

    [Fact]
    public void Alteracao_da_matriz_e_historico_sao_salvos_juntos_sem_duplicar_tentativas()
    {
        using var workspace = new AdhocWorkspace();
        using var services = Services();
        var report = Report();
        var store = services.GetRequiredService<AnalysisSessionStore>();
        store.Add(new LoadedSolution { Path = Path.Combine(_directory, "SampleApi.sln"), RootDirectory = _directory, Solution = workspace.CurrentSolution }, null, report, "version", null);
        var session = store.Acquire(report.AnalysisId!);
        try
        {
            report.Runtime!.Matrix!.Scenarios[0].Status = ScenarioValidationStatuses.Confirmed;
            report.Runtime.Executions.Add(new RuntimeExecution { Id = "EX-01", Phase = RuntimePhases.Manual, MatchLevel = "Confirmed" });
            session.ManualRequests = 1;
            store.Save(session);
            store.Save(session);
        }
        finally { session.Gate.Release(); }
        var loaded = services.GetRequiredService<SqliteAnalysisStore>().Load(session.Id, DateTimeOffset.UtcNow)!;
        Assert.Equal(ScenarioValidationStatuses.Confirmed, loaded.Report.Runtime!.Matrix!.Scenarios[0].Status);
        Assert.Single(loaded.Report.Runtime.Executions);
        Assert.Equal(1, loaded.ManualRequests);
    }
}
