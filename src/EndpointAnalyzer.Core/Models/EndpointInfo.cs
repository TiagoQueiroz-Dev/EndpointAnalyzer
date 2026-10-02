namespace EndpointAnalyzer.Core.Models;

/// <summary>
/// Endpoint HTTP descoberto no código-fonte.
/// </summary>
public class EndpointInfo
{
    /// <summary>Identificador estável: "POST /api/programacoes".</summary>
    public string Id => $"{HttpMethod} {Route}";

    public string HttpMethod { get; set; } = "";

    public string Route { get; set; } = "";

    public string Controller { get; set; } = "";

    public string Action { get; set; } = "";

    /// <summary>Grupo do endpoint (tag do OpenAPI): [Tags("...")] ou o nome do controller.</summary>
    public string Group { get; set; } = "";

    /// <summary>Resumo do endpoint: [EndpointSummary], [SwaggerOperation(Summary)] ou &lt;summary&gt; do XML.</summary>
    public string? Summary { get; set; }

    public string SourceFile { get; set; } = "";

    public int SourceLine { get; set; }

    /// <summary>Documentation comment id do método (usado pelo Roslyn para reencontrar o símbolo).</summary>
    public string MethodId { get; set; } = "";

    public string Project { get; set; } = "";
}
