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

    public SolutionCache Solutions { get; private set; } = null!;

    public IReadOnlyList<EndpointInfo> Endpoints { get; private set; } = [];

    private readonly Dictionary<string, EndpointAnalysisContext> _contexts = [];

    public async Task InitializeAsync()
    {
        var provider = new ServiceCollection()
            // A validação em runtime (sobe a SampleApi) tem testes próprios: aqui fica desligada.
            .AddEndpointAnalyzer(configureRuntime: o => o.Enabled = false)
            .AddSingleton(new AnalysisCache(CacheDirectory))
            .AddSingleton<IAiProviderSelector>(new SingleAiProviderSelector(Ai))
            .BuildServiceProvider();

        Service = provider.GetRequiredService<EndpointAnalysisService>();
        Sources = provider.GetRequiredService<SourceViewer>();
        Solutions = provider.GetRequiredService<SolutionCache>();
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

    /// <summary>Só o fluxo de negócio (BusinessFlowAiAnalyzer) tem resposta fixa; as etapas de runtime não são respondidas.</summary>
    public Task<System.Text.Json.JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Purpose != AI.BusinessFlowAiAnalyzer.Purpose)
            throw new NotSupportedException("A IA falsa não responde às etapas de runtime.");
        return Task.FromResult(System.Text.Json.JsonDocument.Parse("""
            {
              "steps": [
                { "id": "B1", "type": "entry", "description": "Receber a programação", "evidenceIds": ["N1"], "next": "B2", "branches": [] },
                { "id": "B2", "type": "validation", "description": "Data no passado?", "evidenceIds": ["C1"], "next": "",
                  "branches": [{ "condition": "Sim", "target": "B3" }, { "condition": "Não", "target": "B4" }] },
                { "id": "B3", "type": "error", "description": "Retornar erro de data inválida", "evidenceIds": ["E1"], "next": "", "branches": [] },
                { "id": "B4", "type": "persistence", "description": "Gravar a programação", "evidenceIds": ["P1"], "next": "B5", "branches": [] },
                { "id": "B5", "type": "result", "description": "Retornar o id criado", "evidenceIds": ["N1"], "next": "", "branches": [] }
              ],
              "collapsedNodes": [{ "stepId": "B4", "nodeIds": ["N2"], "reason": "chamada técnica" }],
              "uncertainSteps": []
            }
            """).RootElement.Clone());
    }
}
