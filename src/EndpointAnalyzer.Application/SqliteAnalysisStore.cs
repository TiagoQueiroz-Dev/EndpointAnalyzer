using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace EndpointAnalyzer.Application;

/// <summary>Relatório salvo, lido sem a sessão (token protegido e orçamento manual ficam de fora).</summary>
public sealed record StoredAnalysis(string Id, string? PreviousId, string SolutionPath, string Method, string Route, string Project,
    string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, EndpointAnalysisReport Report);

/// <summary>Linha do histórico de análises do endpoint; DiffBase marca a versão com que a próxima análise será comparada.</summary>
public sealed record AnalysisHistoryItem(string Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Commit, string? Branch,
    string AnalyzerVersion, string? AiProvider, string? AiModel, List<string> Sections, bool HasAi, string? RuntimeStatus, bool DiffBase);

/// <summary>
/// Relatórios, histórico manual e credenciais protegidas, persistidos em uma transação. Cada análise é uma versão: a ordem
/// de criação (sequence) e a ligação com a versão anterior do mesmo endpoint (previous_analysis_id) são gravadas só na
/// criação; revalidar uma análise antiga altera o relatório e updated_at, nunca a ordem nem a ligação.
/// </summary>
public sealed class SqliteAnalysisStore
{
    public const int SchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly string _connectionString;
    private readonly IDataProtector _protector;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS analyses (
            id TEXT PRIMARY KEY,
            solution_path TEXT NOT NULL COLLATE NOCASE,
            endpoint_method TEXT NOT NULL,
            endpoint_route TEXT NOT NULL,
            project_name TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL,
            report_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            previous_analysis_id TEXT NULL REFERENCES analyses(id) ON DELETE SET NULL,
            sequence INTEGER NOT NULL DEFAULT 0,
            deleted_at TEXT NULL,
            diff_base INTEGER NOT NULL DEFAULT 0
        );
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
        """;

    /// <summary>
    /// v1 → v2: numera as análises existentes pela criação (created_at; rowid no empate) e liga cada uma à anterior do mesmo
    /// endpoint. O índice antigo ordenava por updated_at, o que fazia uma análise revalidada virar a "mais recente".
    /// </summary>
    private const string MigrationV2 = """
        ALTER TABLE analyses ADD COLUMN previous_analysis_id TEXT NULL REFERENCES analyses(id) ON DELETE SET NULL;
        ALTER TABLE analyses ADD COLUMN sequence INTEGER NOT NULL DEFAULT 0;
        UPDATE analyses SET sequence = (SELECT COUNT(*) FROM analyses b
            WHERE b.created_at < analyses.created_at OR (b.created_at = analyses.created_at AND b.rowid <= analyses.rowid));
        UPDATE analyses SET previous_analysis_id = (SELECT p.id FROM analyses p
            WHERE p.solution_path = analyses.solution_path AND p.endpoint_method = analyses.endpoint_method
                AND p.endpoint_route = analyses.endpoint_route AND p.project_name = analyses.project_name
                AND p.sequence < analyses.sequence
            ORDER BY p.sequence DESC LIMIT 1);
        DROP INDEX IF EXISTS ix_analyses_endpoint;
        """;

    /// <summary>
    /// v2 → v3: exclusão lógica (deleted_at) e a versão escolhida como base da diff da próxima análise do endpoint (diff_base).
    /// </summary>
    private const string MigrationV3 = """
        ALTER TABLE analyses ADD COLUMN deleted_at TEXT NULL;
        ALTER TABLE analyses ADD COLUMN diff_base INTEGER NOT NULL DEFAULT 0;
        """;

    private const string Indexes = """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_analyses_sequence ON analyses(sequence);
        CREATE INDEX IF NOT EXISTS ix_analyses_history
            ON analyses(solution_path, endpoint_method, endpoint_route, project_name, sequence);
        """;

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
        // BEGIN IMMEDIATE: outro processo abrindo o mesmo banco espera e, ao ler a versão, já encontra a migração feita.
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > SchemaVersion)
            throw new InvalidOperationException("O banco de análises foi criado por uma versão mais nova do EndpointAnalyzer.");
        command.CommandText = (version switch { 1 => MigrationV2 + MigrationV3, 2 => MigrationV3, _ => Schema }) + Indexes + $"PRAGMA user_version={SchemaVersion};";
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
        // A versão anterior e a ordem de criação são lidas e gravadas na mesma transação (BEGIN IMMEDIATE): duas análises
        // simultâneas do endpoint nunca apontam para a mesma antecessora. Atualizar uma análise não toca nessas colunas.
        command.CommandText = """
            INSERT INTO analyses(id, solution_path, endpoint_method, endpoint_route, project_name, source_fingerprint, report_json,
                created_at, updated_at, previous_analysis_id, sequence)
            VALUES($id, $path, $method, $route, $project, $fingerprint, $report, $created, $updated,
                COALESCE(
                    (SELECT p.id FROM analyses p WHERE p.solution_path=$path AND p.endpoint_method=$method AND p.endpoint_route=$route
                        AND p.project_name=$project AND p.id<>$id AND p.deleted_at IS NULL AND p.diff_base=1 LIMIT 1),
                    (SELECT p.id FROM analyses p WHERE p.solution_path=$path AND p.endpoint_method=$method AND p.endpoint_route=$route
                        AND p.project_name=$project AND p.id<>$id AND p.deleted_at IS NULL ORDER BY p.sequence DESC LIMIT 1)),
                (SELECT COALESCE(MAX(sequence), 0) + 1 FROM analyses))
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

    /// <summary>Relatório salvo e a ligação com a versão anterior, sem abrir a sessão (o token protegido não é lido).</summary>
    public StoredAnalysis? LoadStored(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, previous_analysis_id, solution_path, endpoint_method, endpoint_route, project_name, source_fingerprint,
                report_json, created_at, updated_at
            FROM analyses WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new StoredAnalysis(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
            JsonSerializer.Deserialize<EndpointAnalysisReport>(reader.GetString(7), JsonOptions)!);
    }

    /// <summary>Versão mais recente do endpoint pela ordem de criação (não pela última alteração).</summary>
    public string? LatestId(string solutionPath, string method, string route, string project)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM analyses WHERE solution_path=$path AND endpoint_method=$method
                AND endpoint_route=$route AND project_name=$project AND deleted_at IS NULL ORDER BY sequence DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(solutionPath));
        command.Parameters.AddWithValue("$method", method.ToUpperInvariant());
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$project", project);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Análises não excluídas do endpoint, da mais recente para a mais antiga. A base da diff é a escolhida ou, sem escolha,
    /// a mais recente.
    /// </summary>
    public IReadOnlyList<AnalysisHistoryItem> History(string solutionPath, string method, string route, string project)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at, updated_at, json_extract(report_json, '$.version.commit'), json_extract(report_json, '$.version.branch'),
                json_extract(report_json, '$.version.analyzerVersion'), json_extract(report_json, '$.version.aiProvider'),
                json_extract(report_json, '$.version.aiModel'), json_extract(report_json, '$.sections'),
                json_extract(report_json, '$.ai') IS NOT NULL, json_extract(report_json, '$.runtime.status'), diff_base
            FROM analyses WHERE solution_path=$path AND endpoint_method=$method AND endpoint_route=$route AND project_name=$project
                AND deleted_at IS NULL
            ORDER BY sequence DESC;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(solutionPath));
        command.Parameters.AddWithValue("$method", method.ToUpperInvariant());
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$project", project);
        var rows = new List<(AnalysisHistoryItem Item, bool Pinned)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            rows.Add((new AnalysisHistoryItem(reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                Text(3), Text(4), Text(5) ?? "", Text(6), Text(7),
                Text(8) is { } sections ? JsonSerializer.Deserialize<List<string>>(sections) ?? [] : [],
                reader.GetBoolean(9), Text(10), false), reader.GetInt32(11) == 1));
        }
        var baseIndex = Math.Max(0, rows.FindIndex(r => r.Pinned));
        return [.. rows.Select((r, i) => r.Item with { DiffBase = i == baseIndex })];
    }

    /// <summary>Exclusão lógica: a análise sai do histórico e deixa de ser a mais recente ou a base da diff.</summary>
    public bool Delete(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE analyses SET deleted_at=$now, diff_base=0 WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Define a análise com que a próxima análise do endpoint será comparada. Escolher a mais recente remove a escolha: a base
    /// volta a acompanhar a última análise.
    /// </summary>
    public bool SetDiffBase(string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM analyses WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return false;
        command.CommandText = """
            UPDATE analyses SET diff_base=0 WHERE id IN (SELECT o.id FROM analyses a JOIN analyses o ON o.solution_path=a.solution_path
                AND o.endpoint_method=a.endpoint_method AND o.endpoint_route=a.endpoint_route AND o.project_name=a.project_name
                WHERE a.id=$id AND a.deleted_at IS NULL);
            UPDATE analyses SET diff_base=1 WHERE id=$id AND deleted_at IS NULL AND sequence < (SELECT MAX(o.sequence) FROM analyses o
                WHERE o.solution_path=analyses.solution_path AND o.endpoint_method=analyses.endpoint_method
                    AND o.endpoint_route=analyses.endpoint_route AND o.project_name=analyses.project_name AND o.deleted_at IS NULL);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }
}
