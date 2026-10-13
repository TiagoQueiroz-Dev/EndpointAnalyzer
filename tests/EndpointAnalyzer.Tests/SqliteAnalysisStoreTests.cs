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

    private LoadedSolution Solution(AdhocWorkspace workspace) =>
        new() { Path = Path.Combine(_directory, "SampleApi.sln"), RootDirectory = _directory, Solution = workspace.CurrentSolution };

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Migracao_v1_para_v2_preserva_dados_e_liga_as_versoes_pela_criacao()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.GetFullPath(Path.Combine(_directory, "SampleApi.sln"));
        var json = JsonSerializer.Serialize(Report(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using (var v1 = Connect())
        {
            using var command = v1.CreateCommand();
            // Esquema da versão 1, como os bancos já existentes.
            command.CommandText = """
                CREATE TABLE analyses (id TEXT PRIMARY KEY, solution_path TEXT NOT NULL COLLATE NOCASE, endpoint_method TEXT NOT NULL,
                    endpoint_route TEXT NOT NULL, project_name TEXT NOT NULL, source_fingerprint TEXT NOT NULL, report_json TEXT NOT NULL,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE INDEX ix_analyses_endpoint ON analyses(solution_path, endpoint_method, endpoint_route, project_name, updated_at);
                CREATE TABLE analysis_sessions (analysis_id TEXT PRIMARY KEY REFERENCES analyses(id) ON DELETE CASCADE, protected_api_token TEXT,
                    manual_requests INTEGER NOT NULL DEFAULT 0 CHECK(manual_requests >= 0), last_used_at TEXT NOT NULL);
                CREATE TABLE manual_executions (analysis_id TEXT NOT NULL REFERENCES analyses(id) ON DELETE CASCADE, execution_id TEXT NOT NULL,
                    scenario_id TEXT, started_at TEXT NOT NULL, source_fingerprint TEXT, execution_json TEXT NOT NULL, PRIMARY KEY(analysis_id, execution_id));
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
            // a1 foi revalidada depois de a3: pelo updated_at, ela pareceria a mais recente.
            foreach (var (id, route, created, updated) in new[]
            {
                ("a2", "/veiculo/v1", "2026-10-09T10:00:00.0000000+00:00", "2026-10-09T10:05:00.0000000+00:00"),
                ("a1", "/veiculo/v1", "2026-10-09T09:00:00.0000000+00:00", "2026-10-13T08:00:00.0000000+00:00"),
                ("b1", "/outro", "2026-10-09T09:30:00.0000000+00:00", "2026-10-09T09:30:00.0000000+00:00"),
                ("a3", "/veiculo/v1", "2026-10-09T11:00:00.0000000+00:00", "2026-10-09T11:00:00.0000000+00:00"),
            })
            {
                command.CommandText = """
                    INSERT INTO analyses VALUES($id, $path, 'POST', $route, 'SampleApi', 'fonte', $json, $created, $updated);
                    INSERT INTO analysis_sessions VALUES($id, 'token-protegido', 2, $updated);
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$route", route);
                command.Parameters.AddWithValue("$json", json);
                command.Parameters.AddWithValue("$created", created);
                command.Parameters.AddWithValue("$updated", updated);
                command.ExecuteNonQuery();
            }
            command.CommandText = "INSERT INTO manual_executions VALUES('a1', 'EX-01', 'CEN-02', '2026-10-13T08:00:00Z', NULL, '{}');";
            command.ExecuteNonQuery();
        }

        using (var services = Services())
        {
            var store = services.GetRequiredService<SqliteAnalysisStore>();
            Assert.Equal("a3", store.LatestId(path, "POST", "/veiculo/v1", "SampleApi"));
            Assert.Null(store.LoadStored("a1")!.PreviousId);
            Assert.Equal("a1", store.LoadStored("a2")!.PreviousId);
            Assert.Equal("a2", store.LoadStored("a3")!.PreviousId);
            Assert.Null(store.LoadStored("b1")!.PreviousId);
            Assert.Equal("CEN-02", store.LoadStored("a3")!.Report.Context.Scenarios!.Scenarios[0].Id);
        }
        using (var reopened = Services()) Assert.NotNull(reopened.GetRequiredService<SqliteAnalysisStore>().LoadStored("a1")); // Migração idempotente.

        using var connection = Connect();
        Assert.Equal((long)SqliteAnalysisStore.SchemaVersion, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM analysis_sessions WHERE protected_api_token='token-protegido' AND manual_requests=2;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM manual_executions;"));
        Assert.Equal(4L, Scalar(connection, $"SELECT COUNT(*) FROM analyses WHERE report_json='{json.Replace("'", "''")}';"));
        Assert.Equal("2026-10-13T08:00:00.0000000+00:00", Scalar(connection, "SELECT updated_at FROM analyses WHERE id='a1';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='ix_analyses_endpoint';"));
    }

    [Fact]
    public void Nova_analise_liga_a_anterior_e_revalidar_a_antiga_nao_muda_o_historico()
    {
        using var workspace = new AdhocWorkspace();
        using var services = Services();
        var store = services.GetRequiredService<AnalysisSessionStore>();
        var database = services.GetRequiredService<SqliteAnalysisStore>();
        var (first, second) = (Report(), Report());
        store.Add(Solution(workspace), null, first, "v1", "token-da-api");
        store.Add(Solution(workspace), null, second, "v2", null);
        Assert.Null(database.LoadStored(first.AnalysisId!)!.PreviousId);
        Assert.Equal(first.AnalysisId, database.LoadStored(second.AnalysisId!)!.PreviousId);

        var session = store.Acquire(first.AnalysisId!);
        try
        {
            first.Runtime!.Matrix!.Scenarios[0].Status = ScenarioValidationStatuses.Confirmed;
            store.Save(session);
        }
        finally { session.Gate.Release(); }

        Assert.Equal(second.AnalysisId, database.LatestId(Solution(workspace).Path, "POST", "/veiculo/v1", "SampleApi"));
        var revalidated = database.LoadStored(first.AnalysisId!)!;
        Assert.Null(revalidated.PreviousId);
        Assert.True(revalidated.UpdatedAt > revalidated.CreatedAt);
        Assert.Equal(ScenarioValidationStatuses.Confirmed, revalidated.Report.Runtime!.Matrix!.Scenarios[0].Status);
        Assert.Equal(first.AnalysisId, database.LoadStored(second.AnalysisId!)!.PreviousId);
        Assert.DoesNotContain("token-da-api", JsonSerializer.Serialize(revalidated));
    }

    [Fact]
    public void Exclusao_logica_e_base_da_diff_definem_a_versao_comparada_com_a_proxima_analise()
    {
        using var workspace = new AdhocWorkspace();
        using var services = Services();
        var store = services.GetRequiredService<AnalysisSessionStore>();
        var database = services.GetRequiredService<SqliteAnalysisStore>();
        var path = Solution(workspace).Path;
        var (first, second, third) = (Report(), Report(), Report());
        store.Add(Solution(workspace), null, first, "v1", null);
        store.Add(Solution(workspace), null, second, "v2", null);
        Assert.Equal(second.AnalysisId, Assert.Single(database.History(path, "POST", "/veiculo/v1", "SampleApi"), h => h.DiffBase).Id);

        Assert.True(database.SetDiffBase(first.AnalysisId!));
        Assert.Equal(first.AnalysisId, Assert.Single(database.History(path, "POST", "/veiculo/v1", "SampleApi"), h => h.DiffBase).Id);
        store.Add(Solution(workspace), null, third, "v3", null);
        Assert.Equal(first.AnalysisId, database.LoadStored(third.AnalysisId!)!.PreviousId);

        Assert.True(database.SetDiffBase(third.AnalysisId!));
        Assert.True(database.Delete(third.AnalysisId!));
        Assert.False(database.Delete(third.AnalysisId!));
        Assert.False(database.SetDiffBase(third.AnalysisId!));
        var history = database.History(path, "POST", "/veiculo/v1", "SampleApi");
        Assert.Equal([second.AnalysisId, first.AnalysisId], history.Select(h => h.Id));
        Assert.Equal(second.AnalysisId, Assert.Single(history, h => h.DiffBase).Id);
        Assert.Equal(second.AnalysisId, database.LatestId(path, "POST", "/veiculo/v1", "SampleApi"));
        Assert.NotNull(database.LoadStored(third.AnalysisId!));
    }

    [Fact]
    public async Task Analises_simultaneas_de_dois_processos_formam_uma_unica_cadeia()
    {
        using var workspace = new AdhocWorkspace();
        using var one = Services();
        using var two = Services(); // Outra instância do store: como um segundo processo no mesmo banco.
        var stores = new[] { one.GetRequiredService<AnalysisSessionStore>(), two.GetRequiredService<AnalysisSessionStore>() };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            stores[i % 2].Add(Solution(workspace), null, Report(), $"v{i}", null))));

        using var connection = Connect();
        Assert.Equal(20L, Scalar(connection, "SELECT COUNT(DISTINCT sequence) FROM analyses;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM analyses WHERE previous_analysis_id IS NULL;"));
        Assert.Equal(19L, Scalar(connection, "SELECT COUNT(DISTINCT previous_analysis_id) FROM analyses;"));
        // Cada versão aponta para a imediatamente anterior na ordem de criação.
        Assert.Equal(0L, Scalar(connection, """
            SELECT COUNT(*) FROM analyses a JOIN analyses p ON p.id = a.previous_analysis_id
            WHERE p.sequence <> (SELECT MAX(x.sequence) FROM analyses x WHERE x.sequence < a.sequence);
            """));
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
