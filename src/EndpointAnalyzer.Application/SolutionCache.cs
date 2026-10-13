using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Mantém as soluções já carregadas em memória (abrir uma .sln com MSBuild é caro).
/// </summary>
public class SolutionCache(ISolutionLoader loader)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> _solutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<LoadedSolution, SourceStamp> _sourceVersions = new();
    private sealed record SourceStamp(string Fingerprint);

    private async Task<LoadedSolution> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var solution = await loader.LoadAsync(path, cancellationToken);
        _sourceVersions.Add(solution, new SourceStamp(AnalysisSourceVersion.Capture(solution)));
        return solution;
    }

    /// <summary>Reutiliza o Roslyn enquanto fontes/configuração forem iguais; recarrega após mudanças locais.</summary>
    public async Task<LoadedSolution> GetCurrentAsync(string path, CancellationToken cancellationToken = default)
    {
        var solution = await GetAsync(path, cancellationToken: cancellationToken);
        if (_sourceVersions.TryGetValue(solution, out var stamp)
            && stamp.Fingerprint == AnalysisSourceVersion.Capture(solution)
            && await AnalysisSourceVersion.MatchesLoadedAsync(solution, cancellationToken)) return solution;
        return await GetAsync(path, reload: true, cancellationToken: cancellationToken);
    }

    public Task<LoadedSolution> GetAsync(string path, bool reload = false, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(path);
        if (reload) _solutions.TryRemove(key, out _);

        // O carregamento é compartilhado: não usa o token de quem pediu primeiro (uma requisição abortada, como ao
        // recarregar a página, cancelaria o carregamento que as outras esperam). Cada chamador espera com o próprio token.
        var lazy = _solutions.GetOrAdd(key, k => new Lazy<Task<LoadedSolution>>(() => LoadAsync(k, CancellationToken.None)));
        var task = lazy.Value;

        // Falhou ao carregar: não guarda o erro no cache, nem quando a falha chega depois desta consulta.
        if (!task.IsCompletedSuccessfully)
            _ = task.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully) _solutions.TryRemove(new KeyValuePair<string, Lazy<Task<LoadedSolution>>>(key, lazy));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task.WaitAsync(cancellationToken);
    }

    public IReadOnlyCollection<string> Loaded => _solutions.Keys.ToList();
}
