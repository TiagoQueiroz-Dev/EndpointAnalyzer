using EndpointAnalyzer.Application;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;

namespace EndpointAnalyzer.Tests;

public class SolutionCacheTests
{
    [Fact]
    public async Task Cancelar_a_primeira_requisicao_nao_cancela_o_carregamento_das_outras()
    {
        using var dir = new TempSolution();
        var loader = new ManualLoader();
        var cache = new SolutionCache(loader);
        using var first = new CancellationTokenSource();

        var aborted = cache.GetAsync(dir.Path, cancellationToken: first.Token);
        var waiting = cache.GetAsync(dir.Path);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);

        loader.Complete(dir.Path);

        Assert.Same(loader.Solution, await waiting);
        Assert.Same(loader.Solution, await cache.GetAsync(dir.Path));
        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public async Task Falha_que_chega_depois_da_consulta_nao_fica_no_cache()
    {
        using var dir = new TempSolution();
        var loader = new ManualLoader();
        var cache = new SolutionCache(loader);

        var failing = cache.GetAsync(dir.Path);
        loader.Fail(new InvalidOperationException("MSBuild falhou"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);

        var retry = cache.GetAsync(dir.Path);
        loader.Complete(dir.Path);

        Assert.Same(loader.Solution, await retry);
        Assert.Equal(2, loader.Loads);
    }

    /// <summary>Carregamento controlado pelo teste: cada LoadAsync espera Complete ou Fail.</summary>
    private sealed class ManualLoader : ISolutionLoader
    {
        private TaskCompletionSource<LoadedSolution> _load = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Loads { get; private set; }

        public LoadedSolution? Solution { get; private set; }

        public Task<LoadedSolution> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            Loads++;
            _load = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _load.Task.WaitAsync(cancellationToken);
        }

        public void Complete(string path)
        {
            Solution = new LoadedSolution
            {
                Solution = new AdhocWorkspace().CurrentSolution,
                Path = path,
                RootDirectory = System.IO.Path.GetDirectoryName(path)!,
            };
            _load.SetResult(Solution);
        }

        public void Fail(Exception exception) => _load.SetException(exception);
    }

    private sealed class TempSolution : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("solution-cache-").FullName;

        public TempSolution()
        {
            Path = System.IO.Path.Combine(_directory, "Api.sln");
            File.WriteAllText(Path, "");
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
