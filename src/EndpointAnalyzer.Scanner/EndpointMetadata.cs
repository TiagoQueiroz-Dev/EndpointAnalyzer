using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Grupo e resumo de cada endpoint, como aparecem no OpenAPI (Swagger/Scalar):
/// grupo = tag ([Tags], [SwaggerOperation(Tags = ...)] ou nome do controller);
/// resumo = [EndpointSummary], [SwaggerOperation(Summary = ...)] ou o &lt;summary&gt; do comentário XML.
/// </summary>
public static partial class EndpointMetadata
{
    /// <summary>Tag declarada no método ou no controller; null quando não há (usa-se o nome do controller).</summary>
    public static string? Tag(IEnumerable<AttributeSyntax> attributes, SemanticModel? model)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute.ShortName())
            {
                case "Tags" when attribute.StringArgument(model) is { Length: > 0 } tag:
                    return tag;
                // Tags = new[] { "Pedido" } ou Tags = new[] { SwaggerAnotacoes.TAG_VEICULO } ou Tags = [ ... ]
                case "SwaggerOperation" when NamedArgument(attribute, "Tags") is { } tags && FirstString(tags, model) is { } first:
                    return first;
            }
        }

        return null;
    }

    public static string? Summary(MethodDeclarationSyntax method, IMethodSymbol symbol, IReadOnlyList<AttributeSyntax> attributes, SemanticModel model)
    {
        foreach (var attribute in attributes)
        {
            switch (attribute.ShortName())
            {
                case "EndpointSummary" when attribute.StringArgument(model) is { Length: > 0 } summary:
                    return summary;
                case "SwaggerOperation" when (attribute.StringArgument(model, "Summary") ?? attribute.StringArgument(model)) is { Length: > 0 } summary:
                    return summary;
            }
        }

        return XmlSummary(symbol.GetDocumentationCommentXml()) ?? XmlSummary(DocumentationFromTrivia(method));
    }

    /// <summary>
    /// &lt;summary&gt; de um método do call graph; sem comentário próprio, usa o do membro de interface implementado
    /// (o comentário costuma ficar na interface, e a implementação usa &lt;inheritdoc/&gt; ou nada).
    /// </summary>
    public static string? MethodSummary(IMethodSymbol method, SyntaxNode declaration)
    {
        var own = XmlSummary(method.GetDocumentationCommentXml()) ?? XmlSummary(DocumentationFromTrivia(declaration));
        if (own is not null) return own;

        var type = method.ContainingType;
        foreach (var member in type.AllInterfaces.SelectMany(i => i.GetMembers(method.Name)).OfType<IMethodSymbol>())
        {
            if (!SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method)) continue;
            var summary = XmlSummary(member.GetDocumentationCommentXml())
                ?? member.DeclaringSyntaxReferences.Select(r => XmlSummary(DocumentationFromTrivia(r.GetSyntax()))).FirstOrDefault(s => s is not null);
            if (summary is not null) return summary;
        }

        return null;
    }

    /// <summary>Primeiro elemento string (literal ou constante) de um array/coleção.</summary>
    private static string? FirstString(ExpressionSyntax expression, SemanticModel? model)
    {
        var elements = expression switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions.ToList(),
            ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.ToList(),
            CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression).ToList(),
            _ => [expression],
        };

        foreach (var element in elements)
        {
            if (element is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                return literal.Token.ValueText;
            if (model?.GetConstantValue(element) is { HasValue: true, Value: string constant })
                return constant;
        }

        return null;
    }

    private static ExpressionSyntax? NamedArgument(AttributeSyntax attribute, string name) =>
        attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name)?.Expression;

    /// <summary>
    /// Sem GenerateDocumentationFile o Roslyn não interpreta os comentários ///; lê o texto bruto da trivia.
    /// </summary>
    private static string? DocumentationFromTrivia(SyntaxNode node)
    {
        var lines = node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.SingleLineCommentTrivia))
            .Select(t => t.ToFullString())
            .SelectMany(text => text.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("///"))
            .Select(line => line[3..]);

        var text = string.Join("\n", lines);
        return text.Length == 0 ? null : $"<doc>{text}</doc>";
    }

    private static string? XmlSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var summary = XElement.Parse(xml).Descendants("summary").FirstOrDefault();
            if (summary is null) return null;

            // <see cref="T:Ns.Tipo"/> → Tipo
            foreach (var reference in summary.Descendants().Where(e => e.Name == "see" || e.Name == "paramref" || e.Name == "typeparamref").ToList())
            {
                var value = (string?)reference.Attribute("cref") ?? (string?)reference.Attribute("name") ?? (string?)reference.Attribute("langword") ?? "";
                reference.ReplaceWith(value[(value.LastIndexOfAny([':', '.']) + 1)..]);
            }

            var text = WhitespaceRegex().Replace(summary.Value, " ").Trim();
            return text.Length == 0 ? null : text;
        }
        catch (System.Xml.XmlException)
        {
            var match = SummaryRegex().Match(xml);
            return match.Success ? WhitespaceRegex().Replace(match.Groups[1].Value, " ").Trim() : null;
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryRegex();
}
