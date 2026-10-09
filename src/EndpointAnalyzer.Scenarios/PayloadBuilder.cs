using System.Globalization;
using System.Text.Json.Nodes;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Monta a requisição do cenário (rota, query, headers, body JSON) com os valores encontrados pelo solver.</summary>
public sealed class PayloadBuilder(InputModel input, VarTable vars, bool enumsAsStrings)
{
    private const int MaxItems = 20;

    public ScenarioRequest Build(Assignment a)
    {
        var request = new ScenarioRequest { Method = input.HttpMethod };
        var route = input.RouteTemplate;
        Dictionary<string, string?>? routeValues = null, query = null, headers = null;

        foreach (var root in input.Roots)
        {
            var v = vars.Input(root);
            switch (root.Location)
            {
                case InputLocations.Route:
                {
                    var text = Raw(v, a.Get(v));
                    (routeValues ??= [])[root.Name] = text;
                    break;
                }
                case InputLocations.Query or InputLocations.Header:
                {
                    var target = root.Location == InputLocations.Query ? query ??= [] : headers ??= [];
                    if (root.Kind == VarKind.Object)
                        foreach (var child in root.Children) target[child.Name] = Raw(vars.Input(child), a.Get(vars.Input(child)));
                    else
                        target[root.Name] = Raw(v, a.Get(v));
                    break;
                }
                default:
                    request.Body = Node(root, a, 0);
                    break;
            }
        }

        route = RouteTemplate.Bind(route, routeValues ?? []);
        request.Route = routeValues;
        request.Query = query;
        request.Headers = headers;
        var queryString = query is null ? "" : string.Join("&", query.Where(q => q.Value is not null)
            .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value!)}"));
        request.Url = route + (queryString.Length > 0 ? "?" + queryString : "");
        return request;
    }

    private JsonNode? Node(InputField field, Assignment a, int depth)
    {
        var v = vars.Input(field);
        var value = a.Get(v);
        if (value.IsNull) return null;

        switch (field.Kind)
        {
            case VarKind.Object:
            {
                var obj = new JsonObject();
                foreach (var child in field.Children) obj[child.Name] = depth > 6 ? null : Node(child, a, depth + 1);
                return obj;
            }
            case VarKind.Collection:
            {
                var array = new JsonArray();
                var count = Math.Min(value.Length ?? 1, MaxItems);
                for (var i = 0; i < count; i++)
                    array.Add(field.Element is null ? JsonValue.Create("item") : Node(field.Element, a, depth + 1));
                return array;
            }
            case VarKind.String:
                return JsonValue.Create(value.Text ?? "");
            case VarKind.Bool:
                return JsonValue.Create(value.Bool ?? true);
            case VarKind.Int:
                return JsonValue.Create((long)(value.Number ?? 0));
            case VarKind.Decimal:
                return JsonValue.Create(value.Number ?? 0);
            case VarKind.Enum:
                return enumsAsStrings && v.EnumMembers?.FirstOrDefault(m => m.Value == value.Number) is { } member
                    ? JsonValue.Create(member.Name)
                    : JsonValue.Create((long)(value.Number ?? 0));
            default:
                return JsonValue.Create(Raw(v, value));
        }
    }

    /// <summary>Valor como vai na rota/query/JSON (sem aspas).</summary>
    public string? Raw(Var v, Value value)
    {
        if (value.IsNull) return null;
        return v.Kind switch
        {
            VarKind.Bool => value.Bool == false ? "false" : "true",
            VarKind.Date => DateValue(v, value.Number ?? 0),
            VarKind.Enum => enumsAsStrings && v.EnumMembers?.FirstOrDefault(m => m.Value == value.Number) is { } member
                ? member.Name
                : ((long)(value.Number ?? 0)).ToString(CultureInfo.InvariantCulture),
            VarKind.Int => ((long)(value.Number ?? 0)).ToString(CultureInfo.InvariantCulture),
            VarKind.Decimal => (value.Number ?? 0).ToString(CultureInfo.InvariantCulture),
            _ => value.Text ?? "",
        };
    }

    private static string DateValue(Var v, decimal days)
    {
        var date = Values.Date(days);
        return v.TypeName == "DateOnly" ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }
}
