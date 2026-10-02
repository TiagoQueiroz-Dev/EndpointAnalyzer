using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EndpointAnalyzer.Application;

/// <summary>Arquivo de código com o trecho em destaque (linhas 1-based, inclusivas).</summary>
public record SourceView(string File, int Line, int EndLine, IReadOnlyList<string> Lines);

/// <summary>Método do grafo onde procurar a origem de uma condição: arquivo e linha da declaração.</summary>
public record MethodLocation(string File, int Line);

/// <summary>
/// Mostra o código de onde veio uma regra, validação ou condição. Só lê documentos da solução carregada
/// (o mesmo texto que o Roslyn analisou, então as linhas batem com as da análise).
/// </summary>
public class SourceViewer(SolutionCache solutions)
{
    /// <summary>
    /// Código do arquivo destacando o trecho que começa em <paramref name="line"/>. Sem <paramref name="endLine"/>,
    /// o fim é o da instrução/declaração que começa nessa linha (o if inteiro, o RuleFor(...) inteiro...).
    /// </summary>
    public async Task<SourceView?> GetAsync(string solutionPath, string file, int line, int? endLine, CancellationToken cancellationToken = default)
    {
        var loaded = await solutions.GetAsync(solutionPath, cancellationToken: cancellationToken);
        var document = Find(loaded, file);
        if (document is null) return null;

        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree is null) return null;
        var text = await tree.GetTextAsync(cancellationToken);
        if (line < 1 || line > text.Lines.Count) return null;

        var end = endLine is { } e && e >= line ? Math.Min(e, text.Lines.Count) : EndOfStatementAt(tree, text, line);
        return View(loaded, document, text, line, end);
    }

    /// <summary>
    /// Procura, nos métodos informados (em ordem), o trecho que gerou a condição do registro ("C3" → "x != null").
    /// </summary>
    public async Task<SourceView?> FindConditionAsync(string solutionPath, string condition, IEnumerable<MethodLocation> methods, CancellationToken cancellationToken = default)
    {
        var loaded = await solutions.GetAsync(solutionPath, cancellationToken: cancellationToken);
        foreach (var method in methods.Distinct())
        {
            var document = Find(loaded, method.File);
            var tree = document is null ? null : await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null) continue;

            var text = await tree.GetTextAsync(cancellationToken);
            var declaration = DeclarationAt(tree, text, method.Line);
            if (declaration is null) continue;

            foreach (var (atom, origin) in SyntaxConditions.Origins(declaration))
            {
                if (atom != condition) continue;
                var span = text.Lines.GetLinePositionSpan(origin);
                return View(loaded, document!, text, span.Start.Line + 1, span.End.Line + 1);
            }
        }
        return null;
    }

    private static SourceView View(LoadedSolution loaded, Document document, SourceText text, int line, int endLine) =>
        new(loaded.RelativePath(document.FilePath), line, endLine, text.Lines.Select(l => l.ToString()).ToList());

    /// <summary>
    /// Documento da solução pelo caminho relativo da análise. Caminhos da IA podem vir parciais:
    /// nesse caso vale o único documento cujo caminho termina com o informado.
    /// </summary>
    private static Document? Find(LoadedSolution loaded, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var documents = loaded.Solution.Projects.SelectMany(p => p.Documents).Where(d => d.FilePath is not null).ToList();

        var full = Path.GetFullPath(Path.Combine(loaded.RootDirectory, file));
        var exact = documents.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath!), full, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var suffix = "/" + file.Replace('\\', '/').TrimStart('.', '/');
        var matches = documents
            .Where(d => d.FilePath!.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => Path.GetFullPath(d.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count == 1 ? matches[0].First() : null;
    }

    /// <summary>Primeiro token da linha (ignorando espaços e comentários).</summary>
    private static SyntaxToken? FirstTokenAt(SyntaxTree tree, SourceText text, int line)
    {
        var textLine = text.Lines[line - 1];
        var token = tree.GetRoot().FindToken(textLine.Start);
        // FindToken devolve o token cuja trivia contém a posição; ele precisa começar nesta linha.
        return token.Span.Start >= textLine.Start && token.Span.Start <= textLine.End ? token : null;
    }

    /// <summary>Última linha da instrução/declaração que começa em <paramref name="line"/>.</summary>
    private static int EndOfStatementAt(SyntaxTree tree, SourceText text, int line)
    {
        if (FirstTokenAt(tree, text, line) is not { Parent: { } node } token) return line;
        while (node.Parent is { } parent && parent.SpanStart == token.SpanStart && parent is not CompilationUnitSyntax) node = parent;

        // Linha de classe/namespace: destacar o tipo inteiro não ajuda.
        if (node is BaseTypeDeclarationSyntax or BaseNamespaceDeclarationSyntax or CompilationUnitSyntax) return line;
        return text.Lines.GetLineFromPosition(node.Span.End).LineNumber + 1;
    }

    /// <summary>Método (ou construtor, acessor, função local) declarado na linha informada.</summary>
    private static SyntaxNode? DeclarationAt(SyntaxTree tree, SourceText text, int line)
    {
        if (line < 1 || line > text.Lines.Count) return null;
        return FirstTokenAt(tree, text, line)?.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax);
    }
}
