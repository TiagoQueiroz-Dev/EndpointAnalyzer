using System.Collections.Concurrent;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

/// <summary>
/// Análises em andamento por solução + projeto + endpoint: cliques repetidos, outra aba ou a fila não iniciam a mesma
/// análise em paralelo (consumiriam o limite da IA duas vezes). A segunda requisição recebe 409 até a primeira terminar.
/// Cada análise expõe o acompanhamento (conta da IA em uso e trocas) para a interface consultar enquanto ela roda.
/// </summary>
public sealed class RunningAnalyses
{
    private readonly ConcurrentDictionary<string, AnalysisProgress> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="AnalysisSessionException">409: já existe uma análise deste endpoint em andamento.</exception>
    public RunningAnalysis Start(string solutionPath, string project, string endpointId)
    {
        var key = Key(solutionPath, project, endpointId);
        var progress = new AnalysisProgress();
        if (!_active.TryAdd(key, progress))
            throw new AnalysisSessionException(409, $"Já existe uma análise de {endpointId} em andamento. Aguarde a conclusão ou cancele-a antes de analisar de novo.");
        return new RunningAnalysis(progress, () => _active.TryRemove(KeyValuePair.Create(key, progress)));
    }

    public AnalysisProgress? Find(string solutionPath, string project, string endpointId) =>
        _active.GetValueOrDefault(Key(solutionPath, project, endpointId));

    private static string Key(string solutionPath, string project, string endpointId) => $"{Path.GetFullPath(solutionPath)}|{project}|{endpointId}";
}

public sealed class RunningAnalysis(AnalysisProgress progress, Action release) : IDisposable
{
    private int _released;

    public AnalysisProgress Progress { get; } = progress;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) release();
    }
}

/// <summary>O que a análise em andamento está usando agora: provedor, conta, modelo e as trocas de conta até aqui.</summary>
public sealed class AnalysisProgress
{
    private FailoverAiProvider? _ai;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public void Track(FailoverAiProvider ai) => _ai = ai;

    public AnalysisProgressSnapshot Snapshot()
    {
        var ai = _ai;
        if (ai is null) return new AnalysisProgressSnapshot(true, StartedAt, null, null, null, [], false);
        var current = ai.Current;
        return new AnalysisProgressSnapshot(true, StartedAt, current.Provider, current.Label, ai.Model, ai.HandoffsSnapshot(), ai.Exhausted);
    }
}

/// <param name="Account">Conta em uso (ou a última, se todas esgotaram).</param>
public sealed record AnalysisProgressSnapshot(bool Running, DateTimeOffset StartedAt, string? Provider, string? Account, string? Model,
    IReadOnlyList<AiHandoff> Handoffs, bool Exhausted);
