using System.Collections.Concurrent;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Análises em andamento por solução + projeto + endpoint: cliques repetidos, outra aba ou a fila não iniciam a mesma
/// análise em paralelo (consumiriam o limite da IA duas vezes). A segunda requisição recebe 409 até a primeira terminar.
/// </summary>
public sealed class RunningAnalyses
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="AnalysisSessionException">409: já existe uma análise deste endpoint em andamento.</exception>
    public IDisposable Start(string solutionPath, string project, string endpointId)
    {
        var key = $"{Path.GetFullPath(solutionPath)}|{project}|{endpointId}";
        if (!_active.TryAdd(key, 0))
            throw new AnalysisSessionException(409, $"Já existe uma análise de {endpointId} em andamento. Aguarde a conclusão ou cancele-a antes de analisar de novo.");
        return new Release(() => _active.TryRemove(key, out _));
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }
}
