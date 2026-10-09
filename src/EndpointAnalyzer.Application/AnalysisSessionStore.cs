using System.Security.Cryptography;
using System.Text;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Application;

public sealed class AnalysisSessionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Contextos em memória reconstruídos a partir do SQLite. A análise persistida não expira.</summary>
public sealed class AnalysisSessionStore : IDisposable
{
    private readonly RuntimeOptions options;
    private readonly SqliteAnalysisStore _database;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, AnalysisSession> _sessions = [];
    private readonly Timer _cleanup;

    public AnalysisSessionStore(RuntimeOptions options, SqliteAnalysisStore database, TimeProvider? time = null)
    {
        this.options = options;
        _database = database;
        _time = time ?? TimeProvider.System;
        _cleanup = new Timer(_ => { lock (_gate) Prune(); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Add(LoadedSolution solution, ScenarioModel? model, EndpointAnalysisReport report, string fingerprint, string? token)
    {
        lock (_gate)
        {
            if (model is not null) report.Context.Scenarios = model.Matrix;
            var session = new AnalysisSession(Guid.NewGuid().ToString("N"), Path.GetFullPath(solution.Path), report,
                fingerprint, token, MemoryExpiration());
            report.AnalysisId = session.Id;
            report.SessionExpiresAt = null;
            _database.Save(session);
            Cache(session);
        }
    }

    public AnalysisSession Get(string id)
    {
        lock (_gate)
        {
            Prune();
            if (!_sessions.TryGetValue(id, out var session))
            {
                session = _database.Load(id, MemoryExpiration())
                    ?? throw new AnalysisSessionException(404, "Análise não encontrada no banco local. Restaure a análise salva no navegador.");
                Cache(session);
            }
            return session;
        }
    }

    public void Save(AnalysisSession session) => _database.Save(session);

    public AnalysisSession Acquire(string id)
    {
        lock (_gate)
        {
            var session = Get(id);
            if (!session.Gate.Wait(0)) throw new AnalysisSessionException(409, "Já existe uma reanálise em andamento nesta análise.");
            return session;
        }
    }

    public AnalysisSession? Latest(string path, string method, string route, string project)
    {
        var id = _database.LatestId(path, method, route, project);
        return id is null ? null : Get(id);
    }

    private DateTimeOffset MemoryExpiration() => _time.GetUtcNow().AddMinutes(Math.Max(1, options.AnalysisSessionMinutes));

    private void Cache(AnalysisSession session)
    {
        Prune();
        if (_sessions.Count >= Math.Max(1, options.MaxAnalysisSessions))
        {
            var oldest = _sessions.Values.Where(s => s.Gate.CurrentCount > 0).OrderBy(s => s.ExpiresAt).FirstOrDefault();
            if (oldest is not null) _sessions.Remove(oldest.Id);
        }
        _sessions[session.Id] = session;
    }

    private void Prune()
    {
        foreach (var session in _sessions.Values.Where(s => s.ExpiresAt <= _time.GetUtcNow() && s.Gate.CurrentCount > 0).ToList())
        {
            _sessions.Remove(session.Id);
            session.Token = null;
        }
    }

    public void Dispose()
    {
        _cleanup.Dispose();
        lock (_gate)
        {
            foreach (var session in _sessions.Values) session.Token = null;
            _sessions.Clear();
        }
    }
}

public sealed class AnalysisSession(string id, string solutionPath, EndpointAnalysisReport report,
    string fingerprint, string? token, DateTimeOffset expiresAt)
{
    public string Id { get; } = id;
    public string SolutionPath { get; } = solutionPath;
    public ScenarioModel Model => ScenarioModel.FromSavedMatrix(Report.Context.Scenarios ?? new ScenarioMatrix());
    public EndpointAnalysisReport Report { get; } = report;
    public string Fingerprint { get; } = fingerprint;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    internal string? Token { get; set; } = token;
    internal string? ProtectedToken { get; set; }
    internal bool TokenUnavailable { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public int ManualRequests { get; set; }
}

/// <summary>Inclui arquivos novos/removidos e alterações locais, além da versão do Git.</summary>
public static class AnalysisSourceVersion
{
    public static string Capture(LoadedSolution solution)
    {
        var directories = solution.Solution.Projects.Select(p => Path.GetDirectoryName(p.FilePath!))
            .Append(solution.RootDirectory).Where(d => d is not null).Distinct(StringComparer.OrdinalIgnoreCase);
        var files = directories.SelectMany(d => SourceFiles(d!)).Append(solution.Path)
            .Concat(solution.Solution.Projects.SelectMany(p => p.Documents.Concat(p.AdditionalDocuments)).Select(d => d.FilePath).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(GitInfo.Read(solution.RootDirectory).Commit ?? "");
        foreach (var file in files)
        {
            Add(Path.GetFullPath(file));
            Add(File.Exists(file) ? File.ReadAllText(file) : "<arquivo removido>");
        }
        return Convert.ToHexString(hash.GetHashAndReset());
        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
    }

    public static async Task<bool> MatchesLoadedAsync(LoadedSolution solution, CancellationToken cancellationToken)
    {
        foreach (var document in solution.Solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not { } path) continue;
            if (!File.Exists(path) || (await document.GetTextAsync(cancellationToken)).ToString() != await File.ReadAllTextAsync(path, cancellationToken)) return false;
        }
        return true;
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (Path.GetExtension(file).ToLowerInvariant() is ".cs" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".json") yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (Path.GetFileName(child).ToLowerInvariant() is not ("bin" or "obj" or ".git" or "node_modules" or ".vs")
                && !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                foreach (var file in SourceFiles(child)) yield return file;
    }
}
