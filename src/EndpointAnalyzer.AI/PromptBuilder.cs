using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.AI;

/// <summary>
/// Monta o prompt a partir do contexto estruturado da análise estática.
/// </summary>
public static partial class PromptBuilder
{
    public const string SystemPrompt = """
        Você é um analisador de código C# e de regras de negócio.

        Você recebe o resultado de uma análise estática (Roslyn) de um endpoint ASP.NET Core:
        o fluxo de chamadas, as condições, as entidades alteradas e o código dos métodos alcançáveis.
        Seu trabalho é transformar isso em documentação de regras de negócio compreensível por pessoas
        que não leem código.

        Regras:
        - Analise exclusivamente as informações fornecidas. Não invente regras que não estejam sustentadas pelo código.
        - Toda regra, validação e alteração precisa de evidência: arquivo, método, linha e o trecho de código.
          Use os números de linha informados no contexto.
        - Escreva as descrições em português, em linguagem de negócio (ex.: "Toda nova programação inicia com status Pendente.").
        - Separe validações de entrada (formato, obrigatoriedade, DataAnnotations, FluentValidation) das regras de negócio.
        - Para cada propriedade alterada, informe a condição necessária para a alteração; use string vazia quando ela sempre acontece.
        - confidence vai de 0.0 a 1.0: alto quando o código mostra a regra diretamente, baixo quando é inferência.
        - Liste em uncertainties tudo que não pôde ser determinado com certeza (ex.: implementação não encontrada,
          comportamento que depende de banco, eventos, bibliotecas externas ou configuração).
        - Numere as regras como REGRA-001, REGRA-002... e as validações como VAL-001, VAL-002...

        Sobre o contexto:
        - business_graph é o fluxo relevante do endpoint. Cada nó tem category (BUSINESS, VALIDATION, DATA_ACCESS,
          PERSISTENCE, EXTERNAL_EFFECT, INFRASTRUCTURE) e reasons (por que é relevante). Nós com "summary" resumem
          infraestrutura colapsada (ex.: "Retorna erro de validação"); "helpers" lista métodos auxiliares ocultos
          que ainda podem mudar a interpretação da regra (ex.: comparação feita com o valor sanitizado).
        - conditionIds (C1, C2...) referenciam condition_registry: a alteração ou chamada só acontece quando todas
          as condições referenciadas são verdadeiras. Ao escrever a condição de cada alteração, use o texto das
          condições em linguagem de negócio, não os ids.
        - effects são os efeitos observáveis (exceções, alterações de entidades, persistência, eventos, HTTP).
          Ignore o que não aparece no contexto: a infraestrutura técnica foi removida de propósito.

        Rótulos do fluxograma (flowLabels), para quem não lê código:
        - Para cada item de flow_items.methods, devolva { key: o nome exatamente como recebido, label: o objetivo do
          método numa frase curta (até 8 palavras), verbo no presente, sem nomes de classes, métodos ou variáveis }.
          Ex.: "UnidadeService.CpfJahCadastrado" → "Verifica se o CPF já está cadastrado".
        - Para cada item de flow_items.decisions, devolva { key: a expressão exatamente como recebida, label: uma
          pergunta curta (até 10 palavras) sobre o que a condição verifica, em linguagem de negócio, sem código }.
          Ex.: "xUsuarioHierarquiaId == Domain.Program.HierarquiaIdRaiz && !string.IsNullOrWhiteSpace(pCnpj)"
          → "Usuário está na hierarquia raiz e informou o CNPJ?". Laços (kind foreach/while/for/do): descreva a
          repetição (ex.: "Há mais itens do pedido para processar?"); switch: pergunte pelo valor avaliado.

        Cenários de teste (scenarios):
        - scenario_matrix foi gerada de forma determinística a partir do código (tabela de decisão, partição de
          equivalência e valores-limite). Não crie, remova nem altere cenários, payloads, pré-condições ou resultados:
          seu papel é só descrever cada um para quem vai testar.
        - Para cada cenário, devolva { id: exatamente como recebido, title: o que o cenário testa em até 12 palavras,
          em linguagem de negócio, sem nomes de classes ou variáveis, description: 1 ou 2 frases com o estado
          necessário e o resultado esperado }. Ex.: "Data da programação no passado" / "Com a data de ontem, a
          programação é recusada com a mensagem de data inválida e nada é gravado."
        """;

    private static readonly HashSet<string> FlowOnlyProperties = ["control", "callFile", "callLine", "callEndLine"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Dados só do fluxograma (estrutura de controle, que repete as condições, e o ponto de chamada do popup): fora do prompt.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(CallNode)) return;
                    foreach (var property in info.Properties.Where(p => FlowOnlyProperties.Contains(p.Name)).ToList())
                        info.Properties.Remove(property);
                },
            },
        },
    };

    public static string BuildUserPrompt(EndpointAnalysisContext context)
    {
        var sb = new StringBuilder();
        var endpoint = context.Endpoint;

        sb.AppendLine($"Endpoint: {endpoint.HttpMethod} {endpoint.Route}");
        sb.AppendLine($"Action: {endpoint.Controller}.{endpoint.Action} ({endpoint.SourceFile}:{endpoint.SourceLine})");
        sb.AppendLine();

        sb.AppendLine("Fluxo:");
        sb.AppendLine(string.Join("\n→ ", context.CallGraph));
        sb.AppendLine();

        // Grafo de negócio (sem infraestrutura e helpers); o técnico fica fora do prompt (contexto mínimo).
        sb.AppendLine("<business_graph>");
        sb.AppendLine(Json(context.BusinessGraph ?? context.CallTree));
        sb.AppendLine("</business_graph>");
        sb.AppendLine();

        sb.AppendLine("<condition_registry>");
        sb.AppendLine(Json(context.ConditionRegistry));
        sb.AppendLine("</condition_registry>");
        sb.AppendLine();

        sb.AppendLine("<conditions>");
        sb.AppendLine(Json(context.Conditions));
        sb.AppendLine("</conditions>");
        sb.AppendLine();

        sb.AppendLine("<effects>");
        sb.AppendLine(Json(context.Effects));
        sb.AppendLine("</effects>");
        sb.AppendLine();

        sb.AppendLine("<entity_changes>");
        sb.AppendLine(Json(context.EntityChanges.Where(c => c.EffectClass != EffectClasses.Infrastructure)));
        sb.AppendLine("</entity_changes>");
        sb.AppendLine();

        sb.AppendLine("<persistence_points>");
        sb.AppendLine(Json(context.PersistencePoints));
        sb.AppendLine("</persistence_points>");
        sb.AppendLine();

        sb.AppendLine("<flow_items>");
        sb.AppendLine(Json(FlowItems(context)));
        sb.AppendLine("</flow_items>");
        sb.AppendLine();

        if (context.Scenarios is { Scenarios.Count: > 0 } matrix)
        {
            sb.AppendLine("<scenario_matrix>");
            sb.AppendLine(Json(ScenarioOutline(matrix)));
            sb.AppendLine("</scenario_matrix>");
            sb.AppendLine();
        }

        sb.AppendLine("<code>");
        foreach (var method in context.Methods)
            AppendSnippet(sb, "method", method);
        foreach (var type in context.Types)
            AppendSnippet(sb, "type", type);
        sb.AppendLine("</code>");
        sb.AppendLine();

        sb.AppendLine("""
            Retorne:
            1. Objetivo do endpoint (summary).
            2. Regras de negócio.
            3. Validações.
            4. Entidades criadas, alteradas e removidas, com os campos alterados.
            5. Condições necessárias para cada alteração.
            6. Evidência de código para cada conclusão.
            7. Pontos que não puderam ser determinados com certeza.
            8. Rótulos do fluxograma para todos os itens de flow_items (flowLabels).
            9. Título e descrição de cada cenário de scenario_matrix (scenarios).
            """);

        return sb.ToString();
    }

    /// <summary>
    /// Métodos e decisões que aparecem nos fluxogramas (grafo de negócio e técnico, sem descer na infraestrutura,
    /// que fica colapsada), para a IA escrever um rótulo em linguagem natural de cada um.
    /// </summary>
    public static object FlowItems(EndpointAnalysisContext context)
    {
        var methods = new List<string>();
        var decisions = new Dictionary<string, string>();

        void Walk(CallNode? node)
        {
            if (node is null) return;
            if (node.Summary is null && !methods.Contains($"{node.TypeName}.{node.MethodName}"))
                methods.Add($"{node.TypeName}.{node.MethodName}");
            foreach (var step in node.Control ?? [])
                decisions.TryAdd(step.Expression, step.Kind);
            if (node.Category == NodeCategories.Infrastructure) return;
            foreach (var child in node.Children) Walk(child);
        }

        Walk(context.BusinessGraph);
        Walk(context.CallTree);
        return new
        {
            methods,
            decisions = decisions.Select(d => new { expression = d.Key, kind = d.Value }),
        };
    }

    /// <summary>
    /// Cenários sem o payload: o que cada um testa, o estado e o resultado. Datas concretas saem (o "hoje" muda todo
    /// dia e não pode invalidar o cache), ficando só a referência relativa ("amanhã", "ontem").
    /// </summary>
    public static object ScenarioOutline(ScenarioMatrix matrix) => matrix.Scenarios.Select(s => new
    {
        id = s.Id,
        kind = s.Kind,
        technique = s.Technique,
        title = WithoutDates(s.Title),
        focus = s.Focus is null ? null : new { expression = s.Focus.Expression, value = s.Focus.Value, detail = s.Focus.Detail },
        preconditions = s.Preconditions.Select(p => WithoutDates(p.Description)),
        expected = new
        {
            outcome = s.Expected.Outcome,
            httpStatus = s.Expected.HttpStatus,
            messages = s.Expected.Messages.Select(WithoutDates),
            rule = s.Expected.Rule,
            effects = s.Expected.Effects.Select(e => e.Description),
            persisted = s.Expected.Persisted,
        },
    });

    private static string WithoutDates(string text) =>
        DateRegex().Replace(IsoDateRegex().Replace(text, "<data>"), "$1");

    [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d{2}-\d{2} \(([^)]*)\)")]
    private static partial System.Text.RegularExpressions.Regex DateRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")]
    private static partial System.Text.RegularExpressions.Regex IsoDateRegex();

    private static void AppendSnippet(StringBuilder sb, string tag, CodeSnippet snippet)
    {
        sb.AppendLine($"<{tag} name=\"{snippet.Name}\" file=\"{snippet.File}\" line=\"{snippet.Line}\">");
        sb.AppendLine(snippet.Code);
        sb.AppendLine($"</{tag}>");
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
