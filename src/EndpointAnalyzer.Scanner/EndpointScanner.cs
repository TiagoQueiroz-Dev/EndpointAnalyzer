using EndpointAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

public interface IEndpointScanner
{
    Task<IReadOnlyList<EndpointInfo>> ScanAsync(LoadedSolution solution, CancellationToken cancellationToken = default);
}

/// <summary>
/// Percorre os documentos C#, encontra controllers e monta os endpoints HttpPost/HttpPut/HttpPatch.
/// </summary>
public class EndpointScanner(AnalyzerOptions options) : IEndpointScanner
{
    private static readonly Dictionary<string, string> HttpAttributes = new()
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpPatch"] = "PATCH",
        ["HttpDelete"] = "DELETE",
    };

    public async Task<IReadOnlyList<EndpointInfo>> ScanAsync(LoadedSolution loaded, CancellationToken cancellationToken = default)
    {
        var endpoints = new List<EndpointInfo>();
        var seen = new HashSet<string>();

        foreach (var project in loaded.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (IsGenerated(tree.FilePath)) continue;

                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(cls, cancellationToken) is not INamedTypeSymbol type || !IsController(type))
                        continue;

                    var controllerRoutes = GetControllerRoutes(type, compilation);
                    var area = FindAttributes(type, "Area").Select(a => a.StringArgument(Model(a, compilation))).FirstOrDefault(a => a != null);
                    var controllerGroup = FindAttributes(type, "Tags")
                        .Select(a => EndpointMetadata.Tag([a], Model(a, compilation)))
                        .FirstOrDefault(t => t is not null)
                        ?? StripSuffix(type.Name, "Controller");

                    foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
                    {
                        foreach (var endpoint in CreateEndpoints(loaded, project, type, method, model, controllerRoutes, area, controllerGroup))
                        {
                            if (options.HttpMethods.Contains(endpoint.HttpMethod) && seen.Add(endpoint.Id + endpoint.MethodId))
                                endpoints.Add(endpoint);
                        }
                    }
                }
            }
        }

        // Como o Scalar: grupos em ordem alfabética e, dentro do grupo, a ordem do código.
        var groupOrder = StringComparer.Create(new System.Globalization.CultureInfo("pt-BR"), ignoreCase: true);
        return endpoints
            .OrderBy(e => e.Group, groupOrder)
            .ThenBy(e => e.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SourceLine)
            .ThenBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<EndpointInfo> CreateEndpoints(
        LoadedSolution loaded,
        Project project,
        INamedTypeSymbol controller,
        MethodDeclarationSyntax method,
        SemanticModel model,
        IReadOnlyList<string?> controllerRoutes,
        string? area,
        string controllerGroup)
    {
        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) || method.Modifiers.Any(SyntaxKind.StaticKeyword))
            yield break;

        var attributes = method.AllAttributes().ToList();
        if (attributes.Any(a => a.ShortName() == "NonAction")) yield break;

        if (model.GetDeclaredSymbol(method) is not IMethodSymbol symbol) yield break;

        // [Route] no método vale para os atributos Http* sem template.
        var methodRoutes = attributes.Where(a => a.ShortName() == "Route").Select(a => a.StringArgument(model)).ToList();

        var controllerName = StripSuffix(controller.Name, "Controller");
        var actionName = StripSuffix(symbol.Name, "Async");
        var group = EndpointMetadata.Tag(attributes, model) ?? controllerGroup;
        var summary = EndpointMetadata.Summary(method, symbol, attributes, model);

        foreach (var attribute in attributes)
        {
            if (!HttpAttributes.TryGetValue(attribute.ShortName(), out var verb)) continue;

            var template = attribute.StringArgument(model) ?? attribute.StringArgument(model, "Template");
            var actionTemplates = template is not null
                ? [template]
                : methodRoutes.Count > 0 ? methodRoutes : [null];

            foreach (var controllerRoute in controllerRoutes)
            {
                foreach (var actionTemplate in actionTemplates)
                {
                    yield return new EndpointInfo
                    {
                        HttpMethod = verb,
                        Route = RouteTemplate.Combine(controllerRoute, actionTemplate, controllerName, actionName, area),
                        Controller = controller.Name,
                        Action = symbol.Name,
                        Group = group,
                        Summary = summary,
                        SourceFile = loaded.RelativePath(method.SyntaxTree.FilePath),
                        SourceLine = method.Identifier.GetLocation().StartLine(),
                        MethodId = symbol.GetDocumentationCommentId() ?? "",
                        Project = project.Name,
                    };
                }
            }
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsStatic || type.DeclaredAccessibility != Accessibility.Public) return false;
        if (FindAttributes(type, "NonController").Any()) return false;

        if (type.Name.EndsWith("Controller", StringComparison.Ordinal)) return true;
        if (FindAttributes(type, "ApiController").Any() || FindAttributes(type, "Controller").Any()) return true;

        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.Name is "ControllerBase" or "Controller") return true;

        return false;
    }

    /// <summary>Templates de [Route] do controller (ou da classe base mais próxima que tiver).</summary>
    private static IReadOnlyList<string?> GetControllerRoutes(INamedTypeSymbol type, Compilation compilation)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var routes = DeclaredAttributes(t)
                .Where(a => a.ShortName() == "Route")
                .Select(a => a.StringArgument(Model(a, compilation)))
                .ToList();
            if (routes.Count > 0) return routes;
        }

        return [null];
    }

    /// <summary>Atributos declarados na classe e nas classes base que estão em código-fonte.</summary>
    private static IEnumerable<AttributeSyntax> FindAttributes(INamedTypeSymbol type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
            foreach (var attribute in DeclaredAttributes(t).Where(a => a.ShortName() == name))
                yield return attribute;
    }

    private static IEnumerable<AttributeSyntax> DeclaredAttributes(INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .SelectMany(d => d.AllAttributes());

    private static SemanticModel? Model(SyntaxNode node, Compilation compilation) =>
        compilation.SyntaxTrees.Contains(node.SyntaxTree) ? compilation.GetSemanticModel(node.SyntaxTree) : null;

    private static string StripSuffix(string value, string suffix) =>
        value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;

    private static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
}

public static class RouteTemplate
{
    /// <summary>
    /// Combina o template do controller com o da action, como o roteamento do ASP.NET Core:
    /// templates iniciados com "/" ou "~/" ignoram o prefixo do controller.
    /// </summary>
    public static string Combine(string? controllerTemplate, string? actionTemplate, string controllerName, string actionName, string? area = null)
    {
        string combined;
        if (actionTemplate is not null && (actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/")))
            combined = actionTemplate.TrimStart('~');
        else if (string.IsNullOrEmpty(controllerTemplate))
            combined = actionTemplate ?? "";
        else if (string.IsNullOrEmpty(actionTemplate))
            combined = controllerTemplate;
        else
            combined = controllerTemplate.TrimEnd('/') + "/" + actionTemplate.TrimStart('/');

        combined = ReplaceToken(combined, "controller", controllerName);
        combined = ReplaceToken(combined, "action", actionName);
        if (area is not null) combined = ReplaceToken(combined, "area", area);

        return "/" + combined.Trim('/');
    }

    private static string ReplaceToken(string template, string token, string value) =>
        template.Replace($"[{token}]", value, StringComparison.OrdinalIgnoreCase);
}
