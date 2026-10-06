using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Core.Interfaces;

/// <summary>
/// Transforma o call graph técnico (obtido por análise estática) num fluxo de negócio. A IA não descobre o fluxo:
/// só interpreta, agrupa e traduz o que está nas evidências; cada passo devolvido cita os ids que o sustentam.
/// </summary>
public interface IBusinessFlowAiAnalyzer
{
    Task<BusinessFlowResult> AnalyzeAsync(
        EndpointAnalysisContext context,
        CallGraphResult callGraph,
        EvidenceCollection evidences,
        IAiProvider ai,
        CancellationToken cancellationToken = default);
}
