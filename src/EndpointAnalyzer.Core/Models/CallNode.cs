using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>
/// Categoria de relevância de um nó do call graph.
/// </summary>
public static class NodeCategories
{
    public const string Business = "BUSINESS";
    public const string Validation = "VALIDATION";
    public const string DataAccess = "DATA_ACCESS";
    public const string Persistence = "PERSISTENCE";
    public const string ExternalEffect = "EXTERNAL_EFFECT";
    public const string Infrastructure = "INFRASTRUCTURE";
    public const string Utility = "UTILITY";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Tipos de <see cref="ControlStep"/>.</summary>
public static class ControlKinds
{
    public const string If = "if";             // if/else e guardas (if sem else que sempre sai)
    public const string Ternary = "ternary";   // a ? b : c
    public const string And = "and";           // a && Chamada()
    public const string Or = "or";             // a || Chamada()
    public const string Coalesce = "coalesce"; // a ?? Chamada()
    public const string NullCheck = "nullcheck"; // a?.Chamada()
    public const string Switch = "switch";     // switch (instrução ou expressão)
    public const string Catch = "catch";       // try/catch
    public const string While = "while";
    public const string DoWhile = "do";
    public const string For = "for";
    public const string ForEach = "foreach";
}

/// <summary>Uma estrutura de controle no caminho até a chamada e o ramo em que ela está.</summary>
public class ControlStep
{
    /// <summary>Um de <see cref="ControlKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Identifica a estrutura dentro do método chamador (tipo + posição no código).</summary>
    public string Key { get; set; } = "";

    /// <summary>O que é avaliado, como no código: condição do if/while, valor do switch, "x em lista" do foreach.</summary>
    public string Expression { get; set; } = "";

    /// <summary>Ramo da chamada: "sim"/"não" (if, laços, &&...), o case do switch ou o tipo da exceção do catch.</summary>
    public string Branch { get; set; } = "";

    /// <summary>Guarda: como o ramo "sim" sai do método ou do laço (throw, return, continue, break).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Exit { get; set; }

    /// <summary>Linhas da estrutura (1-based, inclusive) no arquivo do método chamador.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int EndLine { get; set; }
}

/// <summary>
/// Nó do call graph a partir do método do endpoint.
/// </summary>
public class CallNode
{
    public string MethodName { get; set; } = "";

    /// <summary>Tipo concreto em que o método executa (o receiver), ex.: VeiculoTipoService.</summary>
    public string TypeName { get; set; } = "";

    /// <summary>Tipo que declara o método quando difere do receiver, ex.: ServiceBase&lt;VeiculoTipo, int&gt;.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeclaringType { get; set; }

    public string SourceFile { get; set; } = "";

    public int SourceLine { get; set; }

    /// <summary>
    /// Onde a chamada acontece no método chamador (arquivo e linhas da instrução que chama), diferente de
    /// <see cref="SourceFile"/>/<see cref="SourceLine"/>, que apontam para a declaração do método chamado.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallFile { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CallLine { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CallEndLine { get; set; }

    /// <summary>&lt;summary&gt; do comentário XML do método (ou do membro de interface que ele implementa).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>Condição local (no método chamador) para a chamada acontecer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    /// <summary>
    /// Estruturas de controle que envolvem a chamada no método chamador, da mais externa para a mais interna
    /// (if, guarda, switch, laço, catch...). É a mesma informação de <see cref="Condition"/>, mas estruturada
    /// para desenhar o fluxograma: chamadas com a mesma <see cref="ControlStep.Key"/> estão na mesma estrutura.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ControlStep>? Control { get; set; }

    /// <summary>Referências ao registro de condições (C1, C2...).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ConditionIds { get; set; }

    /// <summary>Tipo declarado do receiver quando a chamada foi resolvida para outro tipo (ex.: IVeiculoTipoService).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedFrom { get; set; }

    /// <summary>Símbolo usado como receiver da chamada (ex.: _veiculoTipoService).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Receiver { get; set; }

    /// <summary>Como a implementação foi escolhida: receiver concreto, DI, construtor, atribuição, busca.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; set; }

    /// <summary>Mais de uma implementação possível: não foi expandido.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Ambiguous { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Candidates { get; set; }

    /// <summary>Método já visitado em outro ponto do grafo (não foi expandido novamente).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AlreadyVisited { get; set; }

    /// <summary>Expansão interrompida por MAX_CALL_DEPTH.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DepthLimitReached { get; set; }

    /// <summary>BUSINESS, VALIDATION, DATA_ACCESS, PERSISTENCE, EXTERNAL_EFFECT, INFRASTRUCTURE, UTILITY, UNKNOWN.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>Score de relevância (complementa a análise contextual).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Score { get; set; }

    /// <summary>Justificativa de relevância do nó.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Reasons { get; set; }

    /// <summary>Como o resultado da chamada é usado: condição, retorno, argumento, atribuição, descartado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Usage { get; set; }

    /// <summary>Nó sintético que resume infraestrutura colapsada (ex.: "Retorna erro de validação").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    /// <summary>Quantidade de chamadas técnicas escondidas neste nó colapsado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CollapsedCount { get; set; }

    /// <summary>Helpers ocultos do grafo principal, preservados para não perder significado (ex.: SanitizaPlaca).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Helpers { get; set; }

    public List<CallNode> Children { get; set; } = [];

    /// <summary>Identificador interno do nó (ligação entre grafo técnico e de negócio).</summary>
    [JsonIgnore]
    public int Id { get; set; }

    [JsonIgnore]
    public string FullName => Summary ?? $"{TypeName}.{MethodName}";

    public IEnumerable<CallNode> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.Flatten())
                yield return node;
    }
}
