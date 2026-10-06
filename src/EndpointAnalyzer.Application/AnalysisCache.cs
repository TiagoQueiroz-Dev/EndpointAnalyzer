using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Cache em disco das respostas da IA. A chave é o hash do prompt enviado (contexto estruturado) + modelo + versão do analisador:
/// se o código do fluxo não mudou, a análise anterior é reaproveitada (mesmo sem git).
/// </summary>
public class AnalysisCache(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EndpointAnalyzer", "cache");

    public static string Key(string contextJson, string model, string analyzerVersion)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{analyzerVersion}|{model}|{contextJson}"));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    public Task<EndpointAnalysisResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        GetAsync<EndpointAnalysisResult>(key, cancellationToken);

    public Task SetAsync(string key, EndpointAnalysisResult result, CancellationToken cancellationToken = default) =>
        SetAsync<EndpointAnalysisResult>(key, result, cancellationToken);

    /// <summary>Qualquer resposta da IA (documentação, fluxo de negócio...): a chave já diz o que foi perguntado.</summary>
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var file = PathOf(key);
        if (!File.Exists(file)) return null;

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    public async Task SetAsync<T>(string key, T result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(PathOf(key));
        await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken);
    }

    private string PathOf(string key) => Path.Combine(directory, key + ".json");
}
