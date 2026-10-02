using System.Collections.Concurrent;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Mantém as soluções já carregadas em memória (abrir uma .sln com MSBuild é caro).
/// </summary>
public class SolutionCache(ISolutionLoader loader)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> _solutions = new(StringComparer.OrdinalIgnoreCase);

    public Task<LoadedSolution> GetAsync(string path, bool reload = false, CancellationToken cancellationToken = default)
    {
        var key = Path.GetFullPath(path);
        if (reload) _solutions.TryRemove(key, out _);

        var lazy = _solutions.GetOrAdd(key, k => new Lazy<Task<LoadedSolution>>(() => loader.LoadAsync(k, cancellationToken)));
        var task = lazy.Value;

        // Falhou ao carregar: não guarda o erro no cache.
        if (task.IsFaulted || task.IsCanceled) _solutions.TryRemove(key, out _);
        return task;
    }

    public IReadOnlyCollection<string> Loaded => _solutions.Keys.ToList();
}
