using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

public static partial class RoslynExtensions
{
    public static int StartLine(this SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static int StartLine(this Location location) =>
        location.GetLineSpan().StartLinePosition.Line + 1;

    public static Location? SourceLocation(this ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(l => l.IsInSource);

    public static bool HasSource(this ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    /// <summary>Chave estável do símbolo, igual entre compilações diferentes.</summary>
    public static string Key(this ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.OriginalDefinition.ToDisplayString();

    /// <summary>Nome do atributo sem namespace e sem o sufixo "Attribute".</summary>
    public static string ShortName(this AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static IEnumerable<AttributeSyntax> AllAttributes(this MemberDeclarationSyntax member) =>
        member.AttributeLists.SelectMany(l => l.Attributes);

    public static IEnumerable<AttributeSyntax> AllAttributes(this ParameterSyntax parameter) =>
        parameter.AttributeLists.SelectMany(l => l.Attributes);

    /// <summary>Valor string de um argumento de atributo (literal ou constante).</summary>
    public static string? StringArgument(this AttributeSyntax attribute, SemanticModel? model, string? namedArgument = null)
    {
        var args = attribute.ArgumentList?.Arguments;
        if (args is null) return null;

        var arg = namedArgument is null
            ? args.Value.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)
            : args.Value.FirstOrDefault(a =>
                a.NameEquals?.Name.Identifier.Text == namedArgument || a.NameColon?.Name.Identifier.Text == namedArgument);
        if (arg is null) return null;

        if (model is not null)
        {
            var constant = model.GetConstantValue(arg.Expression);
            if (constant.HasValue) return constant.Value?.ToString();
        }

        return arg.Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : null;
    }

    /// <summary>Código compacto em uma linha, para expressões e mensagens.</summary>
    public static string Compact(this SyntaxNode node) => Compact(node.ToString());

    public static string Compact(string code) => WhitespaceRegex().Replace(code, " ").Trim();

    /// <summary>Recorta o código em no máximo <paramref name="maxLines"/> linhas.</summary>
    public static string Snippet(this SyntaxNode node, int maxLines = 12)
    {
        var lines = node.ToString().Split('\n');
        var text = string.Join('\n', lines.Take(maxLines)).TrimEnd();
        return lines.Length > maxLines ? text + "\n    // ..." : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
