using System.Text.Json;
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

    /// <summary>
    /// Só as partes das abas pedidas: Resumo (documentação), Negócio (rótulos do fluxograma), Cenários (títulos).
    /// O padrão ignora o filtro e devolve tudo.
    /// </summary>
    Task<EndpointAnalysisResult> AnalyzeAsync(
        EndpointAnalysisContext context,
        AnalysisSections sections,
        CancellationToken cancellationToken = default) =>
        AnalyzeAsync(context, cancellationToken);

    /// <summary>
    /// Pergunta livre com resposta em JSON validado por <see cref="AiJsonRequest.Schema"/> (structured outputs).
    /// Usado pela validação dos cenários em runtime (planejamento, aquisição de dados, exploração).
    /// </summary>
    Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"O provedor {Model} não suporta respostas estruturadas livres.");
}

/// <param name="Purpose">Etapa que fez a pergunta (registrada no log da validação).</param>
/// <param name="Schema">JSON Schema do objeto de resposta (todas as propriedades obrigatórias, sem propriedades extras).</param>
public sealed record AiJsonRequest(string Purpose, string SystemPrompt, string UserPrompt, IReadOnlyDictionary<string, JsonElement> Schema);
