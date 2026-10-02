using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Carrega samples/SampleApi.sln uma única vez para todos os testes de integração.
/// </summary>
public sealed class SampleSolutionFixture : IAsyncLifetime
{
    public string SolutionPath { get; } = Path.Combine(RepositoryRoot(), "samples", "SampleApi.sln");

    public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "endpoint-analyzer-tests", Guid.NewGuid().ToString("N"));

    public FakeAiProvider Ai { get; } = new();

    public EndpointAnalysisService Service { get; private set; } = null!;

    public SourceViewer Sources { get; private set; } = null!;

    public IReadOnlyList<EndpointInfo> Endpoints { get; private set; } = [];

    private readonly Dictionary<string, EndpointAnalysisContext> _contexts = [];

    public async Task InitializeAsync()
    {
        var provider = new ServiceCollection()
            .AddEndpointAnalyzer()
            .AddSingleton(new AnalysisCache(CacheDirectory))
            .AddSingleton<IAiProviderSelector>(new SingleAiProviderSelector(Ai))
            .BuildServiceProvider();

        Service = provider.GetRequiredService<EndpointAnalysisService>();
        Sources = provider.GetRequiredService<SourceViewer>();
        Endpoints = await Service.ListEndpointsAsync(SolutionPath);
    }

    public async Task<EndpointAnalysisContext> ContextAsync(string endpointId)
    {
        if (_contexts.TryGetValue(endpointId, out var cached)) return cached;

        var endpoint = await Service.FindEndpointAsync(SolutionPath, endpointId);
        return _contexts[endpointId] = await Service.BuildContextAsync(SolutionPath, endpoint);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(CacheDirectory)) Directory.Delete(CacheDirectory, recursive: true);
        return Task.CompletedTask;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "EndpointAnalyzer.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("EndpointAnalyzer.sln não encontrado.");
    }
}

[CollectionDefinition(Name)]
public class SampleSolutionCollection : ICollectionFixture<SampleSolutionFixture>
{
    public const string Name = "SampleSolution";
}

/// <summary>Provedor de IA falso: registra o contexto recebido e devolve uma resposta fixa.</summary>
public sealed class FakeAiProvider : IAiProvider
{
    public int Calls { get; private set; }

    public string Model => "fake-model";

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new EndpointAnalysisResult
        {
            Summary = $"Resumo de {context.Endpoint.Id}",
            BusinessRules = [new BusinessRule { Id = "REGRA-001", Description = "Regra de teste", Confidence = 0.9 }],
        });
    }
}
