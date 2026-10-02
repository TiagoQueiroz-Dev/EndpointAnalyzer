using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EndpointAnalyzer.AI.ClaudeCode;

public sealed record ClaudeModelInfo(
    string Id,
    string Name,
    bool Available,
    string? Reason,
    IReadOnlyList<string> Efforts,
    string? DefaultEffort);

/// <summary>
/// Modelos que a conta logada no Claude Code pode usar. O Claude Code não tem um comando que liste os modelos,
/// então cada candidato é testado com uma chamada mínima (claude -p "ok"). O resultado fica em cache por conta.
/// </summary>
public class ClaudeModelCatalog(ClaudeCodeOptions options)
{
    private static readonly string[] AllEfforts = ["low", "medium", "high", "xhigh", "max"];

    private sealed record Candidate(string Id, string Name, string[] Efforts, string? DefaultEffort);

    // Haiku 4.5 não aceita effort.
    private static readonly Candidate[] Candidates =
    [
        new("claude-opus-5-5", "Claude Opus 5.5", AllEfforts, "high"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", AllEfforts, "high"),
        new("claude-fable-5-1", "Claude Fable 5.1", AllEfforts, "high"),
        new("claude-opus-5", "Claude Opus 5", AllEfforts, "high"),
        new("claude-sonnet-5", "Claude Sonnet 5", AllEfforts, "high"),
        new("claude-haiku-4-5", "Claude Haiku 4.5", [], null),
    ];

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<ClaudeModelInfo>>> _probes = new();

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointAnalyzer");

    public static bool IsKnownModel(string model) => Candidates.Any(c => c.Id == model);

    public static IReadOnlyList<string> EffortsOf(string model) =>
        Candidates.FirstOrDefault(c => c.Id == model)?.Efforts ?? AllEfforts;

    /// <summary>Lista os modelos indicando quais estão liberados para a conta.</summary>
    public async Task<IReadOnlyList<ClaudeModelInfo>> GetAsync(ClaudeAuthStatus account, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!account.LoggedIn)
            return Candidates.Select(c => new ClaudeModelInfo(c.Id, c.Name, false, "Entre com a conta do Claude.", c.Efforts, c.DefaultEffort)).ToList();

        var key = AccountKey(account);
        var file = Path.Combine(DataDirectory, $"models-{key}.json");

        if (refresh)
        {
            _probes.TryRemove(key, out _);
        }
        else if (!_probes.ContainsKey(key) && ReadCache(file) is { } cached)
        {
            return cached;
        }

        var probe = _probes.GetOrAdd(key, _ => ProbeAllAsync(file));
        try
        {
            return await probe.WaitAsync(cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            _probes.TryRemove(key, out _);
            throw;
        }
    }

    private async Task<IReadOnlyList<ClaudeModelInfo>> ProbeAllAsync(string file)
    {
        var models = await Task.WhenAll(Candidates.Select(ProbeAsync));

        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new CacheFile(DateTimeOffset.UtcNow, models), JsonOptions));
        return models;
    }

    private async Task<ClaudeModelInfo> ProbeAsync(Candidate candidate)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "claude-code");
        Directory.CreateDirectory(workingDirectory);

        var arguments = new List<string>
        {
            "-p", "--output-format", "json", "--model", candidate.Id,
            "--tools", "", "--setting-sources", "", "--no-session-persistence",
            "--system-prompt", "Responda apenas: ok",
        };
        if (candidate.Efforts.Length > 0) arguments.AddRange(["--effort", "low"]);

        try
        {
            var result = await ProcessRunner.RunAsync(
                ProcessRunner.StartInfo(options.Executable, arguments, workingDirectory),
                "ok", TimeSpan.FromSeconds(90), CancellationToken.None);

            var (available, reason) = ReadProbe(result.Output, result.Error);
            return new ClaudeModelInfo(candidate.Id, candidate.Name, available, reason, candidate.Efforts, candidate.DefaultEffort);
        }
        catch (Exception e) when (e is TimeoutException or ClaudeCodeNotInstalledException)
        {
            return new ClaudeModelInfo(candidate.Id, candidate.Name, false, e.Message, candidate.Efforts, candidate.DefaultEffort);
        }
    }

    private static (bool Available, string? Reason) ReadProbe(string output, string error)
    {
        var start = output.IndexOf('{');
        if (start >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(output[start..]);
                var root = doc.RootElement;
                var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                if (!isError) return (true, null);
                return (false, root.TryGetProperty("result", out var r) ? r.ToString() : "indisponível");
            }
            catch (JsonException)
            {
            }
        }

        var text = ProcessRunner.StripAnsi(error + output);
        return (false, text.Contains("unrecognized_model") ? "Modelo não reconhecido por esta versão do Claude Code." : "Modelo indisponível.");
    }

    private static IReadOnlyList<ClaudeModelInfo>? ReadCache(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file), JsonOptions);
            return cache is not null && DateTimeOffset.UtcNow - cache.CheckedAt < CacheDuration ? cache.Models : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private static string AccountKey(ClaudeAuthStatus account)
    {
        var raw = $"{account.Email}|{account.OrgName}|{account.SubscriptionType}|{account.AuthMethod}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    private sealed record CacheFile(DateTimeOffset CheckedAt, IReadOnlyList<ClaudeModelInfo> Models);
}
