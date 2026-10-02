using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Core.Interfaces;

/// <summary>
/// Abstração do fornecedor de IA, para a aplicação não depender de um único fornecedor.
/// </summary>
public interface IAiProvider
{
    /// <summary>Nome do modelo, registrado no versionamento da análise.</summary>
    string Model { get; }

    Task<EndpointAnalysisResult> AnalyzeAsync(
        EndpointAnalysisContext context,
        CancellationToken cancellationToken = default);
}
