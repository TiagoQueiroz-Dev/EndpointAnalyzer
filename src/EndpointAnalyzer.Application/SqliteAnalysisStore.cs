using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace EndpointAnalyzer.Application;

/// <summary>Relatórios, histórico manual e credenciais protegidas, persistidos em uma transação.</summary>
public sealed class SqliteAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    public static string DatabasePath(RuntimeOptions options) => Path.GetFullPath(options.AnalysisDatabasePath
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointAnalyzer", "analyses.db"));

    public SqliteAnalysisStore(RuntimeOptions options, IDataProtectionProvider protection)
    {
        var path = DatabasePath(options);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 15, Pooling = false,
        }.ToString();
        _protector = protection.CreateProtector("EndpointAnalyzer.AnalysisApiToken.v1");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar()) > 1)
            throw new InvalidOperationException("O banco de análises foi criado por uma versão mais nova do EndpointAnalyzer.");
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analyses (
                id TEXT PRIMARY KEY,
                solution_path TEXT NOT NULL COLLATE NOCASE,
                endpoint_method TEXT NOT NULL,
                endpoint_route TEXT NOT NULL,
                project_name TEXT NOT NULL,
                source_fingerprint TEXT NOT NULL,
                report_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analyses_endpoint
                ON analyses(solution_path, endpoint_method, endpoint_route, project_name, updated_at);
            CREATE TABLE IF NOT EXISTS analysis_sessions (
                analysis_id TEXT PRIMARY KEY REFERENCES analyses(id) ON DELETE CASCADE,
                protected_api_token TEXT,
                manual_requests INTEGER NOT NULL DEFAULT 0 CHECK(manual_requests >= 0),
                last_used_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS manual_executions (
                analysis_id TEXT NOT NULL REFERENCES analyses(id) ON DELETE CASCADE,
                execution_id TEXT NOT NULL,
                scenario_id TEXT,
                started_at TEXT NOT NULL,
                source_fingerprint TEXT,
                execution_json TEXT NOT NULL,
                PRIMARY KEY(analysis_id, execution_id)
            );
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Save(AnalysisSession session)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO analyses(id, solution_path, endpoint_method, endpoint_route, project_name, source_fingerprint, report_json, created_at, updated_at)
            VALUES($id, $path, $method, $route, $project, $fingerprint, $report, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET report_json=excluded.report_json, updated_at=excluded.updated_at;
            INSERT INTO analysis_sessions(analysis_id, protected_api_token, manual_requests, last_used_at)
            VALUES($id, $token, $requests, $updated)
            ON CONFLICT(analysis_id) DO UPDATE SET protected_api_token=excluded.protected_api_token,
                manual_requests=excluded.manual_requests, last_used_at=excluded.last_used_at;
            """;
        command.Parameters.AddWithValue("$id", session.Id);
        command.Parameters.AddWithValue("$path", session.SolutionPath);
        command.Parameters.AddWithValue("$method", session.Report.Context.Endpoint.HttpMethod);
        command.Parameters.AddWithValue("$route", session.Report.Context.Endpoint.Route);
        command.Parameters.AddWithValue("$project", session.Report.Context.Endpoint.Project);
        command.Parameters.AddWithValue("$fingerprint", session.Fingerprint);
        command.Parameters.AddWithValue("$report", JsonSerializer.Serialize(session.Report, JsonOptions));
        command.Parameters.AddWithValue("$created", session.Report.Version.Date.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$token", session.TokenUnavailable ? (object?)session.ProtectedToken ?? DBNull.Value
            : session.Token is null ? DBNull.Value : _protector.CreateProtector(session.Id).Protect(session.Token));
        command.Parameters.AddWithValue("$requests", session.ManualRequests);
        command.ExecuteNonQuery();
        foreach (var execution in session.Report.Runtime?.Executions.Where(e => e.Phase == RuntimePhases.Manual) ?? [])
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO manual_executions(analysis_id, execution_id, scenario_id, started_at, source_fingerprint, execution_json)
                VALUES($id, $execution, $scenario, $started, $fingerprint, $json)
                ON CONFLICT(analysis_id, execution_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", session.Id);
            command.Parameters.AddWithValue("$execution", execution.Id);
            command.Parameters.AddWithValue("$scenario", (object?)execution.ScenarioId ?? DBNull.Value);
            command.Parameters.AddWithValue("$started", execution.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$fingerprint", (object?)execution.SourceFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(execution, JsonOptions));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public AnalysisSession? Load(string id, DateTimeOffset memoryExpiresAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.solution_path, a.source_fingerprint, a.report_json, s.protected_api_token, s.manual_requests
            FROM analyses a JOIN analysis_sessions s ON s.analysis_id=a.id WHERE a.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var report = JsonSerializer.Deserialize<EndpointAnalysisReport>(reader.GetString(2), JsonOptions)!;
        var protectedToken = reader.IsDBNull(3) ? null : reader.GetString(3);
        string? token = null;
        var unavailable = false;
        if (protectedToken is not null)
        {
            try { token = _protector.CreateProtector(id).Unprotect(protectedToken); }
            catch (CryptographicException) { unavailable = true; }
        }
        return new AnalysisSession(id, reader.GetString(0), report, reader.GetString(1), token, memoryExpiresAt)
        {
            ManualRequests = reader.GetInt32(4),
            ProtectedToken = protectedToken,
            TokenUnavailable = unavailable,
        };
    }

    public string? LatestId(string solutionPath, string method, string route, string project)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM analyses WHERE solution_path=$path AND endpoint_method=$method
                AND endpoint_route=$route AND project_name=$project ORDER BY updated_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(solutionPath));
        command.Parameters.AddWithValue("$method", method.ToUpperInvariant());
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$project", project);
        return command.ExecuteScalar() as string;
    }
}
