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

/// <summary>Modelos e capacidades informados pelo Claude Code para o login do projeto.</summary>
public class ClaudeModelCatalog
{
    private const int CacheVersion = 3;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ClaudeCodeOptions _options;
    private readonly string _dataDirectory;
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ClaudeModelInfo>>> _discover;

    public ClaudeModelCatalog(ClaudeCodeOptions options)
        : this(options, DataDirectory, TimeProvider.System,
            cancellationToken => ClaudeCodeModelDiscovery.GetAsync(options, cancellationToken)) { }

    internal ClaudeModelCatalog(ClaudeCodeOptions options, string dataDirectory, TimeProvider clock,
        Func<CancellationToken, Task<IReadOnlyList<ClaudeModelInfo>>> discover)
    {
        _options = options;
        _dataDirectory = dataDirectory;
        _clock = clock;
        _discover = discover;
    }

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointAnalyzer");

    public async Task<IReadOnlyList<ClaudeModelInfo>> GetAsync(ClaudeAuthStatus account, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!account.LoggedIn) return [];
        var raw = $"{account.Email}|{account.OrgName}|{account.SubscriptionType}|{account.AuthMethod}|{_options.ConfigDirectory}|{_options.Executable}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        var file = Path.Combine(_dataDirectory, $"models-{key}.json");
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && ReadCache(file) is { } cached) return cached;
            var models = await _discover(cancellationToken);
            if (models.Count == 0)
                throw new InvalidOperationException("O Claude Code não retornou modelos para esta conta. Atualize o Claude Code e tente novamente.");
            Directory.CreateDirectory(_dataDirectory);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(
                new CacheFile(CacheVersion, _clock.GetUtcNow(), models), JsonOptions), cancellationToken);
            return models;
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<ClaudeModelInfo>? ReadCache(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(file), JsonOptions);
            return cache is { Version: CacheVersion, Models.Count: > 0 }
                && _clock.GetUtcNow() - cache.CheckedAt < CacheDuration ? cache.Models : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record CacheFile(int Version, DateTimeOffset CheckedAt, IReadOnlyList<ClaudeModelInfo> Models);
}
