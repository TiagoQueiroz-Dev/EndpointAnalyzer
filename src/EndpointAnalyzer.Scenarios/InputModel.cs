using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;

namespace EndpointAnalyzer.Scenarios;

public static class InputLocations
{
    public const string Route = "route";
    public const string Query = "query";
    public const string Header = "header";
    public const string Form = "form";
    public const string Body = "body";
}

/// <summary>Campo que o endpoint recebe: parâmetro da action ou propriedade de um DTO (recursivo).</summary>
public sealed class InputField
{
    /// <summary>Caminho como no código, a partir do parâmetro: "request.Endereco.Cep", "id", "request.Itens[]".</summary>
    public required string Path { get; init; }

    /// <summary>Nome do membro no C#: "Cep" ("request" na raiz).</summary>
    public required string Member { get; init; }

    /// <summary>Nome no JSON (camelCase ou [JsonPropertyName]), na rota ou na query.</summary>
    public required string Name { get; init; }

    public required string Location { get; init; }

    /// <summary>Tipo sem Nullable&lt;T&gt;.</summary>
    public required ITypeSymbol Type { get; init; }

    public required VarKind Kind { get; init; }

    public bool Nullable { get; init; }

    /// <summary>Referência não anulável num contexto com nullable habilitado: o MVC trata como [Required].</summary>
    public bool ImplicitRequired { get; init; }

    public InputField? Parent { get; init; }

    public List<InputField> Children { get; } = [];

    /// <summary>Elemento de uma coleção.</summary>
    public InputField? Element { get; set; }

    public IPropertySymbol? Property { get; init; }

    public IParameterSymbol? Parameter { get; init; }

    public IReadOnlyList<EnumMember>? EnumMembers { get; init; }

    public string TypeDisplay => Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + (Nullable && Type.IsValueType ? "?" : "");

    public InputField? Child(string member) => Children.FirstOrDefault(c => c.Member == member);

    public IEnumerable<InputField> Flatten()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var f in child.Flatten())
                yield return f;
        if (Element is not null)
            foreach (var f in Element.Flatten())
                yield return f;
    }
}

/// <summary>
/// Estrutura do payload do endpoint (Fase 1): onde cada parâmetro é lido (rota, query, header, body) e os campos
/// dos DTOs, com tipo e nulidade. É a base para montar o payload de cada cenário.
/// </summary>
public sealed class InputModel
{
    private const int MaxDepth = 4;

    public List<InputField> Roots { get; } = [];

    public required string RouteTemplate { get; init; }

    public required string HttpMethod { get; init; }

    public InputField? Root(IParameterSymbol parameter) =>
        Roots.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Parameter?.OriginalDefinition, parameter.OriginalDefinition));

    public IEnumerable<InputField> All => Roots.SelectMany(r => r.Flatten());

    public static InputModel Build(IMethodSymbol entry, string route, string httpMethod)
    {
        var model = new InputModel { RouteTemplate = route, HttpMethod = httpMethod };
        var routeParams = RouteParameters(route);

        foreach (var parameter in entry.Parameters)
        {
            var attributes = parameter.GetAttributes();
            if (Has(attributes, "FromServices") || Has(attributes, "FromKeyedServices")) continue;
            if (parameter.Type.Name is "CancellationToken" or "HttpContext" or "HttpRequest" or "ClaimsPrincipal") continue;

            var bindingName = attributes.Select(a => NamedString(a, "Name")).FirstOrDefault(n => n is not null);
            var location =
                Has(attributes, "FromRoute") ? InputLocations.Route :
                Has(attributes, "FromQuery") ? InputLocations.Query :
                Has(attributes, "FromHeader") ? InputLocations.Header :
                Has(attributes, "FromForm") ? InputLocations.Form :
                Has(attributes, "FromBody") ? InputLocations.Body :
                routeParams.Contains(bindingName ?? parameter.Name) ? InputLocations.Route :
                IsSimple(Underlying(parameter.Type).Type) ? InputLocations.Query : InputLocations.Body;

            var (type, nullable) = Underlying(parameter.Type);
            var root = Create(parameter.Name, parameter.Name, location == InputLocations.Body ? "" : bindingName ?? parameter.Name,
                location, type, nullable || parameter.HasExplicitDefaultValue, parameter.NullableAnnotation, null, null, parameter, [], 0);
            model.Roots.Add(root);
        }

        return model;
    }

    private static InputField Create(string path, string member, string name, string location, ITypeSymbol type, bool nullable,
        NullableAnnotation annotation, InputField? parent, IPropertySymbol? property, IParameterSymbol? parameter, List<ITypeSymbol> stack, int depth)
    {
        var kind = KindOf(type);
        var field = new InputField
        {
            Path = path,
            Member = member,
            Name = name,
            Location = location,
            Type = type,
            Kind = kind,
            Nullable = nullable || (!type.IsValueType && annotation != NullableAnnotation.NotAnnotated),
            ImplicitRequired = !type.IsValueType && annotation == NullableAnnotation.NotAnnotated && location != InputLocations.Route,
            Parent = parent,
            Property = property,
            Parameter = parameter,
            EnumMembers = kind == VarKind.Enum ? EnumMembersOf(type) : null,
        };

        if (depth >= MaxDepth || stack.Any(t => SymbolEqualityComparer.Default.Equals(t, type))) return field;

        if (kind == VarKind.Collection && ElementType(type) is { } element)
        {
            var (elementType, elementNullable) = Underlying(element);
            field.Element = Create($"{path}[]", "[]", "", location, elementType, elementNullable, element.NullableAnnotation, field, null, null, [.. stack, type], depth + 1);
        }
        else if (kind == VarKind.Object && type is INamedTypeSymbol named)
        {
            foreach (var p in Properties(named))
            {
                var (propertyType, propertyNullable) = Underlying(p.Type);
                field.Children.Add(Create($"{path}.{p.Name}", p.Name, JsonName(p), location, propertyType, propertyNullable,
                    p.NullableAnnotation, field, p, null, [.. stack, type], depth + 1));
            }
        }

        return field;
    }

    /// <summary>Propriedades públicas de instância com set/init (inclusive das classes base), como o System.Text.Json desserializa.</summary>
    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        var chain = new List<INamedTypeSymbol>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType) chain.Insert(0, t);

        foreach (var t in chain)
            foreach (var p in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (p.IsStatic || p.IsIndexer || p.DeclaredAccessibility != Accessibility.Public) continue;
                if (p.SetMethod is null && !IsPrimaryConstructorProperty(p)) continue;
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name is "JsonIgnoreAttribute")) continue;
                if (seen.Add(p.Name)) yield return p;
            }
    }

    private static bool IsPrimaryConstructorProperty(IPropertySymbol property) =>
        property.ContainingType.InstanceConstructors.Any(c => c.Parameters.Any(p => p.Name == property.Name));

    private static string JsonName(IPropertySymbol property)
    {
        var explicitName = property.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "JsonPropertyNameAttribute" or "JsonPropertyAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        return explicitName ?? (property.Name.Length == 0 ? "" : char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
    }

    public static (ITypeSymbol Type, bool Nullable) Underlying(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            ? (n.TypeArguments[0], true)
            : (type, false);

    public static VarKind KindOf(ITypeSymbol type)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return VarKind.Bool;
            case SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64:
                return VarKind.Int;
            case SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal: return VarKind.Decimal;
            case SpecialType.System_String or SpecialType.System_Char: return VarKind.String;
            case SpecialType.System_DateTime: return VarKind.Date;
        }

        if (type.TypeKind == TypeKind.Enum) return VarKind.Enum;
        if (type.Name is "DateTimeOffset" or "DateOnly") return VarKind.Date;
        if (type.Name == "Guid") return VarKind.Guid;
        if (type.Name is "TimeSpan" or "TimeOnly" or "Uri" or "JsonElement" or "JsonNode" or "IFormFile" or "Stream") return VarKind.Other;
        if (type is IArrayTypeSymbol || ElementType(type) is not null) return VarKind.Collection;
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface) return VarKind.Object;
        return VarKind.Other;
    }

    public static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type.SpecialType == SpecialType.System_String) return null;
        var enumerable = type is INamedTypeSymbol { Name: "IEnumerable", TypeArguments.Length: 1 } self
            ? self
            : type.AllInterfaces.FirstOrDefault(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1);
        return enumerable?.TypeArguments[0];
    }

    private static bool IsSimple(ITypeSymbol type) => KindOf(type) is not (VarKind.Object or VarKind.Collection) || type.Name is "Uri";

    public static IReadOnlyList<EnumMember> EnumMembersOf(ITypeSymbol type) => type.GetMembers().OfType<IFieldSymbol>()
        .Where(f => f.HasConstantValue)
        .Select(f => new EnumMember(f.Name, Convert.ToInt64(f.ConstantValue)))
        .ToList();

    /// <summary>"{id:int}", "{pId}", "{*slug}", "{nome?}" → id, pId, slug, nome.</summary>
    public static HashSet<string> RouteParameters(string route)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(route, @"\{\**([A-Za-z_][\w]*)[^}]*\}"))
            names.Add(m.Groups[1].Value);
        return names;
    }

    private static bool Has(IEnumerable<AttributeData> attributes, string name) =>
        attributes.Any(a => a.AttributeClass?.Name == name + "Attribute" || a.AttributeClass?.Name == name);

    private static string? NamedString(AttributeData attribute, string name) =>
        attribute.AttributeClass?.Name is "FromRouteAttribute" or "FromQueryAttribute" or "FromHeaderAttribute" or "FromFormAttribute"
            ? attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string
            : null;
}
