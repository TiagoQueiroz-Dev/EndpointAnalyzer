using System.Text;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Runtime;

public sealed record CatalogParameter(string Name, string Location, string Type);

/// <summary>Endpoint da API em execução que a IA pode chamar para buscar (ou preparar) dados.</summary>
public sealed class CatalogEndpoint
{
    public string Method { get; init; } = "";

    public string Route { get; init; } = "";

    public string Action { get; init; } = "";

    public string? Summary { get; init; }

    public List<CatalogParameter> Parameters { get; init; } = [];

    /// <summary>Formato do body: "{ placa: string }".</summary>
    public string? Body { get; init; }

    /// <summary>Formato da resposta de sucesso.</summary>
    public string? Response { get; init; }

    /// <summary>Endpoint analisado.</summary>
    public bool IsTarget { get; init; }

    public bool ReadOnly => Method is "GET" or "HEAD";

    internal Regex Pattern { get; init; } = null!;
}

/// <summary>
/// EndpointCatalog: todos os endpoints do projeto da API (GET inclusive), com parâmetros, DTOs e retornos, para a IA
/// descobrir de onde vêm os dados de que cada cenário precisa. Também restringe as requisições da IA às rotas que existem.
/// </summary>
public sealed class EndpointCatalog
{
    private static readonly HashSet<string> AllMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public List<CatalogEndpoint> Endpoints { get; } = [];

    public static async Task<EndpointCatalog> BuildAsync(LoadedSolution solution, EndpointInfo target, CancellationToken cancellationToken = default)
    {
        var scanner = new EndpointScanner(new AnalyzerOptions { HttpMethods = AllMethods });
        var endpoints = (await scanner.ScanAsync(solution, cancellationToken)).Where(e => e.Project == target.Project).ToList();
        var catalog = new EndpointCatalog();
        var resolver = new MethodResolver();

        foreach (var endpoint in endpoints)
        {
            var symbol = await resolver.ResolveAsync(solution, endpoint, cancellationToken);
            var parameters = new List<CatalogParameter>();
            string? body = null, response = null;
            if (symbol is not null)
            {
                var input = InputModel.Build(symbol, endpoint.Route, endpoint.HttpMethod);
                foreach (var root in input.Roots)
                {
                    if (root.Location == InputLocations.Body) body = TypeShape.Describe(root.Type);
                    else if (root.Kind == VarKind.Object && root.Location is InputLocations.Query or InputLocations.Header)
                        parameters.AddRange(root.Children.Select(c => new CatalogParameter(c.Name, root.Location, c.TypeDisplay)));
                    else parameters.Add(new CatalogParameter(root.Name, root.Location, root.TypeDisplay));
                }
                response = await ResponseTypeAsync(symbol, solution.Solution, cancellationToken) is { } type ? TypeShape.Describe(type) : null;
            }

            catalog.Endpoints.Add(new CatalogEndpoint
            {
                Method = endpoint.HttpMethod,
                Route = endpoint.Route,
                Action = $"{endpoint.Controller}.{endpoint.Action}",
                Summary = endpoint.Summary,
                Parameters = parameters,
                Body = body,
                Response = response,
                IsTarget = endpoint.Id == target.Id && endpoint.MethodId == target.MethodId,
                Pattern = RoutePattern(endpoint.Route),
            });
        }
        return catalog;
    }

    public static EndpointCatalog Of(IEnumerable<CatalogEndpoint> endpoints)
    {
        var catalog = new EndpointCatalog();
        foreach (var e in endpoints)
            catalog.Endpoints.Add(new CatalogEndpoint
            {
                Method = e.Method, Route = e.Route, Action = e.Action, Summary = e.Summary, Parameters = e.Parameters, Body = e.Body,
                Response = e.Response, IsTarget = e.IsTarget, Pattern = RoutePattern(e.Route),
            });
        return catalog;
    }

    /// <summary>Endpoint do catálogo para o método e a URL (com valores na rota e query string).</summary>
    public CatalogEndpoint? Match(string method, string url)
    {
        var path = url.Split('?', '#')[0];
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)) path = absolute.AbsolutePath;
        if (!path.StartsWith('/')) path = "/" + path;
        // Rota literal antes de rota com parâmetro ("/api/x/novos" antes de "/api/x/{id}").
        return Endpoints
            .Where(e => string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase) && e.Pattern.IsMatch(path))
            .OrderBy(e => e.Route.Count(c => c == '{'))
            .FirstOrDefault();
    }

    /// <summary>Texto do catálogo para os prompts.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var e in Endpoints.OrderBy(e => e.ReadOnly ? 0 : 1).ThenBy(e => e.Route, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"{e.Method} {e.Route}");
            if (e.IsTarget) sb.Append("   [ENDPOINT ANALISADO]");
            sb.AppendLine($"   ({e.Action}){(e.Summary is null ? "" : " — " + e.Summary)}");
            if (e.Parameters.Count > 0) sb.AppendLine($"  parâmetros: {string.Join(", ", e.Parameters.Select(p => $"{p.Name} ({p.Location}, {p.Type})"))}");
            if (e.Body is not null) sb.AppendLine($"  body: {e.Body}");
            if (e.Response is not null) sb.AppendLine($"  resposta: {e.Response}");
        }
        return sb.ToString();
    }

    /// <summary>"/api/x/{id:int}/itens/{*resto}" → ^/api/x/[^/]+/itens(/.*)?$ (sem diferenciar maiúsculas).</summary>
    internal static Regex RoutePattern(string route)
    {
        var sb = new StringBuilder("^");
        foreach (var segment in route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Regex.Match(segment, @"^\{\*{1,2}[^}]*\}$").Success) { sb.Append("(?:/.*)?"); continue; }
            if (Regex.Match(segment, @"^\{[^}]*\?\}$").Success) { sb.Append("(?:/[^/]+)?"); continue; }
            sb.Append('/');
            var last = 0;
            foreach (Match m in Regex.Matches(segment, @"\{[^}]*\}"))
            {
                sb.Append(Regex.Escape(segment[last..m.Index]));
                sb.Append("[^/]+");
                last = m.Index + m.Length;
            }
            sb.Append(Regex.Escape(segment[last..]));
        }
        if (sb.Length == 1) sb.Append('/');
        sb.Append("/?$");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Tipo da resposta de sucesso: o T de Task/ActionResult&lt;T&gt;, [ProducesResponseType(typeof(T))] ou o argumento
    /// de Ok(...)/Created...(...) no corpo da action.
    /// </summary>
    private static async Task<ITypeSymbol?> ResponseTypeAsync(IMethodSymbol method, Solution solution, CancellationToken cancellationToken)
    {
        var type = Unwrap(method.ReturnType);
        if (type is not null && !IsUntypedResult(type)) return type;

        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is not ("ProducesResponseTypeAttribute" or "ProducesAttribute")) continue;
            if (attribute.AttributeClass.TypeArguments.FirstOrDefault() is { } generic) return generic;
            if (attribute.ConstructorArguments.FirstOrDefault(a => a.Value is ITypeSymbol).Value is ITypeSymbol typed
                && (attribute.ConstructorArguments.Length < 2 || attribute.ConstructorArguments[1].Value is int and >= 200 and < 300))
                return typed;
        }

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax();
            var document = solution.GetDocument(syntax.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
            if (model is null) continue;
            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => null,
                };
                if (name is not ("Ok" or "Created" or "CreatedAtAction" or "CreatedAtRoute" or "Accepted" or "AcceptedAtAction")) continue;
                if (invocation.ArgumentList.Arguments.LastOrDefault() is not { } argument) continue;
                if (model.GetTypeInfo(argument.Expression).Type is { SpecialType: not SpecialType.System_Object } argType and not IErrorTypeSymbol
                    && argument.Expression is not LiteralExpressionSyntax { Token.ValueText: "null" })
                    return Unwrap(argType);
            }
        }
        return null;
    }

    private static bool IsUntypedResult(ITypeSymbol type) =>
        type.Name is "IActionResult" or "ActionResult" or "IResult" or "Results" or "IHttpActionResult" || type.SpecialType == SpecialType.System_Void;

    private static ITypeSymbol? Unwrap(ITypeSymbol type)
    {
        for (var i = 0; i < 4; i++)
        {
            if (type is INamedTypeSymbol { IsGenericType: true } named
                && named.Name is "Task" or "ValueTask" or "ActionResult" or "Ok" or "Created" or "CreatedAtRoute")
            {
                type = named.TypeArguments[0];
                continue;
            }
            if (type is INamedTypeSymbol { Name: "Task" or "ValueTask", IsGenericType: false }) return null;
            break;
        }
        return type;
    }
}

/// <summary>Formato compacto de um tipo para o prompt: "{ id: int, placa: string, itens: [{ ... }] }".</summary>
internal static class TypeShape
{
    private const int MaxDepth = 3;
    private const int MaxProperties = 25;

    public static string Describe(ITypeSymbol type) => Describe(type, 0, []);

    private static string Describe(ITypeSymbol type, int depth, List<ITypeSymbol> stack)
    {
        var (underlying, nullable) = InputModel.Underlying(type);
        var suffix = nullable || (type.NullableAnnotation == NullableAnnotation.Annotated && !type.IsValueType) ? "?" : "";
        var kind = InputModel.KindOf(underlying);

        switch (kind)
        {
            case VarKind.Enum:
                return $"{underlying.Name}({string.Join("|", InputModel.EnumMembersOf(underlying).Take(12).Select(m => $"{m.Name}={m.Value}"))}){suffix}";
            case VarKind.Collection when InputModel.ElementType(underlying) is { } element:
                return depth >= MaxDepth ? "[...]" : $"[{Describe(element, depth + 1, [.. stack, underlying])}]{suffix}";
            case VarKind.Object when underlying is INamedTypeSymbol named:
            {
                if (depth >= MaxDepth || stack.Any(t => SymbolEqualityComparer.Default.Equals(t, underlying))) return named.Name + suffix;
                var properties = new List<string>();
                var seen = new HashSet<string>();
                for (var t = named; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
                    foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
                    {
                        if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public || p.GetMethod is null) continue;
                        if (p.GetAttributes().Any(a => a.AttributeClass?.Name is "JsonIgnoreAttribute")) continue;
                        if (seen.Add(p.Name)) properties.Add($"{JsonName(p)}: {Describe(p.Type, depth + 1, [.. stack, underlying])}");
                    }
                if (properties.Count == 0) return named.Name + suffix;
                var shown = properties.Take(MaxProperties).ToList();
                return $"{{ {string.Join(", ", shown)}{(properties.Count > shown.Count ? ", ..." : "")} }}{suffix}";
            }
            default:
                return underlying.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + suffix;
        }
    }

    private static string JsonName(IPropertySymbol property)
    {
        var explicitName = property.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "JsonPropertyNameAttribute" or "JsonPropertyAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        return explicitName ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
    }
}
