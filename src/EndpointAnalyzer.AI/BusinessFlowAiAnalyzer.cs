using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI;

/// <summary>
/// Fase 4 do fluxo de negócio: envia o call graph classificado e as evidências (dados estruturados, sem o projeto
/// inteiro) e recebe o fluxo de negócio em JSON validado por schema (structured outputs). A resposta ainda passa
/// pela validação contra as evidências antes de virar fluxograma.
/// </summary>
public class BusinessFlowAiAnalyzer : IBusinessFlowAiAnalyzer
{
    public const string Purpose = "fluxo de negócio";

    public const string SystemPrompt = """
        Você recebe um Call Graph obtido por análise estática de código C# (Roslyn) de um endpoint ASP.NET Core,
        com cada nó classificado (kind) e as evidências extraídas do código.

        Transforme o fluxo técnico em um fluxo de negócio.

        Regras:
        1. Não invente comportamento.
        2. Toda etapa retornada deve possuir evidência no input.
        3. Remova ou agrupe detalhes puramente técnicos.
        4. Preserve validações que alterem o resultado da operação.
        5. Preserve branches relevantes.
        6. Preserve exceções e mensagens relevantes.
        7. Preserve consultas que influenciem decisões.
        8. Preserve persistências.
        9. Preserve integrações externas.
        10. Traduza nomes técnicos para descrições de negócio.
        11. Não remova uma chamada somente por parecer infraestrutura se ela alterar comportamento.
        12. Quando não houver evidência suficiente, marque como incerto (uncertainSteps).
        13. Informe quais nós técnicos foram agrupados ou removidos (collapsedNodes).
        14. Cada passo deve referenciar os IDs das evidências que o sustentam (evidenceIds).
        15. Remova as abstrações: mostre só o uso que o fluxo faz delas sobre as entidades.

        Abstrações (nós com kind "Abstraction"):
        - São métodos genéricos reaproveitados por várias entidades (ex.: ServiceBase<TEntity, TKey>.ObterTodos,
          Repository<T>.ObterPorId, Repository<T>.Adicionar). O campo "via" diz qual abstração é; "entity" diz em qual
          entidade ela opera aqui; "caller" e "usage" mostram o método e a instrução que a usam; "hides" lista o que ela
          executa por dentro (já resolvido: não cite nem descreva).
        - Nunca crie um passo para a abstração em si e nunca use o nome dela na descrição ("Obter todos", "Obter por id",
          "Acessar repositório", "Consultar DbSet"). Descreva o que o chamador faz com a entidade, a partir de "usage" e
          do código do chamador. Ex.: usage "_hierarquiaService.ObterTodos().FirstOrDefault(p => p.Id == pRequest.HierarquiaSuperiorId)"
          → "Buscar a hierarquia superior informada"; "_unidadeRepository.Adicionar(xUnidade)" → "Gravar a nova unidade".
        - Quando o resultado só alimenta uma decisão, junte a consulta ao passo da decisão (ex.: "CNPJ já está cadastrado?").
        - Consultas (Q*) e persistências (P*) feitas por abstrações já apontam para o ponto de uso: cite-as normalmente.

        Formato:
        - Ids dos passos: B1, B2, B3... B1 é a entrada (type "entry", evidência N1).
        - type: entry, validation, businessRule, query, transformation, persistence, externalIntegration, error ou result.
        - evidenceIds: só ids que existem no input — nós (N1, N2...), condições (C1... e R1...), exceções (E1...),
          persistências (P1...), consultas (Q1...) e integrações externas (X1...). Passo sem evidência válida é descartado.
        - Passo que decide (validação ou regra com mais de uma saída): preencha branches, cada uma com condition
          (rótulo curto da saída em linguagem de negócio, ex.: "CNPJ já cadastrado", "Sim", "Não") e target (id do
          passo de destino), e deixe next vazio. Passo sem decisão: next com o id do passo seguinte e branches vazio.
          Passos de erro e de resultado encerram o fluxo: next vazio e branches vazio.
        - Uma exceção relevante vira um passo "error" com a mensagem e o status HTTP quando informados
          (ex.: "Retornar erro 400: CNPJ já cadastrado").
        - description: frase curta (até 10 palavras), verbo no infinitivo, sem nomes de classes, métodos ou variáveis.
          Em passos que decidem, use uma pergunta (ex.: "CNPJ já está cadastrado?").
        - collapsedNodes: para cada passo que agrupa nós técnicos, { stepId, nodeIds, reason }; nós removidos do fluxo
          de negócio (infraestrutura sem efeito no comportamento) vão com stepId vazio.
        - Escreva em português.
        """;

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Caminho completo e linha ficam fora do prompt: a IA vê só "source" (Arquivo.cs:linha).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(CallGraphNode) && !typeof(EvidenceBase).IsAssignableFrom(info.Type)) return;
                    foreach (var property in info.Properties.Where(p => p.Name is "file" or "line").ToList())
                        info.Properties.Remove(property);
                },
            },
        },
    };

    private static readonly JsonSerializerOptions ResultJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<BusinessFlowResult> AnalyzeAsync(EndpointAnalysisContext context, CallGraphResult callGraph, EvidenceCollection evidences,
        IAiProvider ai, CancellationToken cancellationToken = default)
    {
        var json = await ai.CompleteJsonAsync(new AiJsonRequest(Purpose, SystemPrompt, BuildUserPrompt(context, callGraph, evidences), Schema()), cancellationToken);
        return json.Deserialize<BusinessFlowResult>(ResultJson) ?? throw new AiProviderException("Resposta vazia da IA para o fluxo de negócio.");
    }

    /// <summary>Endpoint, call graph, condições, exceções, persistências, entidades, integrações e o código estritamente necessário.</summary>
    public static string BuildUserPrompt(EndpointAnalysisContext context, CallGraphResult callGraph, EvidenceCollection evidences)
    {
        var endpoint = context.Endpoint;
        var sb = new StringBuilder();
        sb.AppendLine($"Endpoint: {endpoint.HttpMethod} {endpoint.Route}");
        if (!string.IsNullOrWhiteSpace(endpoint.Summary)) sb.AppendLine($"Descrição: {endpoint.Summary}");
        sb.AppendLine();

        Section(sb, "call_graph", callGraph);
        Section(sb, "conditions", evidences.Conditions);
        Section(sb, "exceptions", evidences.Exceptions);
        Section(sb, "persistence", evidences.Persistence);
        Section(sb, "queries", evidences.Queries);
        Section(sb, "external_calls", evidences.ExternalCalls);
        Section(sb, "entities", context.EntityChanges
            .Where(c => c.EffectClass != EffectClasses.Infrastructure)
            .Select(c => new { entity = c.Entity, operation = c.Operation, properties = c.Properties }));

        // Só o código dos métodos do grafo de negócio (o mesmo recorte da documentação), sem DTOs e arquivos inteiros.
        sb.AppendLine("<code>");
        foreach (var method in context.Methods)
        {
            sb.AppendLine($"<method name=\"{method.Name}\" file=\"{method.File}\" line=\"{method.Line}\">");
            sb.AppendLine(method.Code);
            sb.AppendLine("</method>");
        }
        sb.AppendLine("</code>");
        sb.AppendLine();
        sb.AppendLine("Retorne o fluxo de negócio (steps), os nós técnicos agrupados ou removidos (collapsedNodes) e os passos incertos (uncertainSteps).");
        return sb.ToString();
    }

    public static Dictionary<string, JsonElement> Schema()
    {
        var branch = Obj(new() { ["condition"] = Str(), ["target"] = Str() });
        var step = Obj(new()
        {
            ["id"] = Str(),
            ["type"] = new { type = "string", @enum = BusinessFlowStepTypes.All },
            ["description"] = Str(),
            ["evidenceIds"] = Arr(Str()),
            ["next"] = Str(),
            ["branches"] = Arr(branch),
        });
        var collapsed = Obj(new() { ["stepId"] = Str(), ["nodeIds"] = Arr(Str()), ["reason"] = Str() });
        var uncertain = Obj(new() { ["stepId"] = Str(), ["reason"] = Str() });
        var root = Obj(new()
        {
            ["steps"] = Arr(step),
            ["collapsedNodes"] = Arr(collapsed),
            ["uncertainSteps"] = Arr(uncertain),
        });
        return JsonSerializer.SerializeToElement(root).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static void Section<T>(StringBuilder sb, string tag, T value)
    {
        sb.AppendLine($"<{tag}>");
        sb.AppendLine(JsonSerializer.Serialize(value, PromptJson));
        sb.AppendLine($"</{tag}>");
        sb.AppendLine();
    }

    private static object Str() => new { type = "string" };

    private static object Arr(object items) => new { type = "array", items };

    private static Dictionary<string, object> Obj(Dictionary<string, object> properties) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = properties.Keys.ToArray(),
        ["additionalProperties"] = false,
    };
}
