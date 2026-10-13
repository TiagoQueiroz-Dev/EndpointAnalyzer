using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

/// <summary>Normalização dos textos comparados: formatação, literais (dados de teste) e segredos.</summary>
internal static partial class DiffText
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\bawait\s+")]
    private static partial Regex Await();

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?(?:Z|[+-]\d{2}:\d{2})?")]
    private static partial Regex Date();

    [GeneratedRegex(@"(?<![\w.])\d+(?:[.,]\d+)?(?!\w)")]
    private static partial Regex Number();

    /// <summary>Argumento literal de uma chamada: ExisteCarroceriaTipo(null) e ExisteCarroceriaTipo(1) testam o mesmo estado.</summary>
    [GeneratedRegex(@"(?<=[(,]\s*)(?:null|true|false|default)(?=\s*[,)])")]
    private static partial Regex LiteralArgument();

    [GeneratedRegex(@"auth|token|cookie|secret|senha|password|passwd|api[-_]?key|credential|bearer", RegexOptions.IgnoreCase)]
    private static partial Regex Sensitive();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Credential();

    public const string Redacted = "***";

    /// <summary>Código sem formatação: espaços, await, ?. e ! (null-forgiving) não mudam o comportamento.</summary>
    public static string Code(string? text) => string.IsNullOrWhiteSpace(text) ? ""
        : Spaces().Replace(Await().Replace(text, "").Replace("?.", ".").Replace("!.", "."), "");

    public static string Text(string? text) => Spaces().Replace(text ?? "", " ").Trim();

    /// <summary>Troca valores literais (números, textos, datas, GUIDs) por #: o que sobra é a forma da regra ou do cenário.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var masked = StringLiteral().Replace(text, "#");
        masked = Guid().Replace(masked, "#");
        masked = Date().Replace(masked, "#");
        masked = Number().Replace(masked, "#");
        return Text(LiteralArgument().Replace(masked, "#"));
    }

    public static bool IsSensitive(string name) => Sensitive().IsMatch(name);

    /// <summary>Cópia sem credenciais: propriedades sensíveis e valores "Bearer ..." viram ***.</summary>
    public static JsonNode? Redact(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key,
            IsSensitive(p.Key) && p.Value is not null ? JsonValue.Create(Redacted) : Redact(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Redact).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(RedactText(v.GetValue<string>())),
        _ => node.DeepClone(),
    };

    public static string RedactText(string text) => Credential().Replace(text, m => $"{m.Groups[1].Value} {Redacted}");

    /// <summary>Forma do JSON (nomes e tipos, sem os valores): mudar só os valores é dado de teste.</summary>
    public static JsonNode? Shape(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Shape(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Shape).ToArray()),
        _ => JsonValue.Create(node.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? "boolean" : node.GetValueKind().ToString().ToLowerInvariant()),
    };

    public static JsonNode? S(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value);

    public static JsonNode? N(int? value) => value is { } v ? JsonValue.Create(v) : null;

    /// <summary>true ou ausente: um campo booleano desligado não aparece como diferença de "nulo para false".</summary>
    public static JsonNode? Flag(bool value) => value ? JsonValue.Create(true) : null;

    /// <summary>Conjunto (sem ordem nem repetição): a ordenação não é diferença.</summary>
    public static JsonNode? Set(IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : new JsonArray(list.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }

    /// <summary>Lista para exibição, na ordem original.</summary>
    public static JsonNode? List(IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        return list.Count == 0 ? null : new JsonArray(list.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    }

    /// <summary>Arquivo e método, sem a linha: mudar só a linha não é diferença.</summary>
    public static string? Origin(string? file, string? method)
    {
        var parts = new[] { file?.Replace('\\', '/'), method }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public static string? Origin(SourceReference? source) => source is null ? null : Origin(source.File, source.Method);
}

/// <summary>Campo canônico de um item: valor comparado, valor exibido e, quando existe, a forma sem os dados de teste.</summary>
/// <param name="Descriptive">Texto que descreve o item (título, detalhe do foco): acompanha uma mudança de comportamento.</param>
internal sealed record DiffFacet(string Path, string Label, string Impact, JsonNode? Value, JsonNode? Display, JsonNode? Masked, bool ContextOnly,
    bool Descriptive);

/// <summary>
/// Projeção canônica de uma regra, validação, efeito ou cenário: chaves de correspondência (da mais forte à mais fraca) e os
/// campos comparáveis, sem ids voláteis (CEN-xx, Dx, Cx), linhas nem ordem.
/// </summary>
internal sealed class DiffProjection(string title, string? reference, int position, string impact)
{
    public string Title { get; } = title;

    /// <summary>Id do item no relatório (CEN-03, D2): só para exibição.</summary>
    public string? Ref { get; } = reference;

    public int Position { get; } = position;

    /// <summary>Impacto da inclusão/remoção do item.</summary>
    public string Impact { get; } = impact;

    public List<string?> Keys { get; } = [];

    /// <summary>A partir deste nível, a chave é só apoio (origem/posição): a correspondência fica incerta.</summary>
    public int SupportFrom { get; private set; } = int.MaxValue;

    public List<DiffFacet> Facets { get; } = [];

    /// <summary>Objeto original (cenário, regra...) para comparações que dependem do par.</summary>
    public object? Source { get; init; }

    public string SemanticKey => Keys.FirstOrDefault(k => k is not null) ?? Title;

    public DiffProjection Key(string? key)
    {
        Keys.Add(key);
        return this;
    }

    public DiffProjection SupportKey(string? key)
    {
        SupportFrom = Math.Min(SupportFrom, Keys.Count);
        Keys.Add(key);
        return this;
    }

    public DiffProjection Add(string path, string label, string impact, JsonNode? value, JsonNode? display = null, JsonNode? masked = null,
        bool contextOnly = false, bool descriptive = false)
    {
        Facets.Add(new DiffFacet(path, label, impact, value, display, masked, contextOnly, descriptive));
        return this;
    }

    public DiffFacet? Facet(string path) => Facets.FirstOrDefault(f => f.Path == path);

    public JsonObject Snapshot() => new(Facets.Where(f => (f.Display ?? f.Value) is not null)
        .Select(f => KeyValuePair.Create(f.Path, (f.Display ?? f.Value)!.DeepClone())));
}

internal sealed record DiffMatch(DiffProjection? Base, DiffProjection? Current, bool Uncertain = false, string? Note = null);

/// <summary>
/// Correspondência semântica e um-para-um entre os itens das duas versões. Cada nível de chave tenta parear o que sobrou:
/// itens equivalentes primeiro e, depois, só pares únicos (1×1). O que continua sem par mas compartilha alguma chave com o
/// outro lado é ambíguo e fica incerto; só o resto é inclusão ou remoção.
/// </summary>
internal static class DiffMatcher
{
    public const string SupportNote = "Correspondência só pela origem no código: confira se é o mesmo item.";

    public static List<DiffMatch> Match(IReadOnlyList<DiffProjection> bases, IReadOnlyList<DiffProjection> currents)
    {
        var restB = bases.ToList();
        var restC = currents.ToList();
        var matches = new List<DiffMatch>();
        var levels = bases.Concat(currents).Select(p => p.Keys.Count).DefaultIfEmpty(0).Max();
        for (var level = 0; level < levels; level++)
        {
            var byKey = restC.Where(c => KeyAt(c, level) is not null).GroupBy(c => KeyAt(c, level)!).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var group in restB.Where(b => KeyAt(b, level) is not null).GroupBy(b => KeyAt(b, level)!).ToList())
            {
                if (!byKey.TryGetValue(group.Key, out var cs)) continue;
                var bs = group.ToList();
                foreach (var b in bs.ToList())
                    if (cs.FirstOrDefault(c => Equivalent(b, c)) is { } same) Pair(b, same, false, null);
                if (bs.Count == 1 && cs.Count == 1)
                {
                    var support = level >= bs[0].SupportFrom || level >= cs[0].SupportFrom;
                    Pair(bs[0], cs[0], support, support ? SupportNote : null);
                }

                void Pair(DiffProjection b, DiffProjection c, bool uncertain, string? note)
                {
                    matches.Add(new DiffMatch(b, c, uncertain, note));
                    bs.Remove(b);
                    cs.Remove(c);
                    restB.Remove(b);
                    restC.Remove(c);
                }
            }
        }
        matches.AddRange(Leftovers(restB, restC));
        return matches.OrderBy(m => (m.Current ?? m.Base)!.Position).ThenBy(m => m.Current is null ? 1 : 0).ToList();
    }

    /// <summary>Iguais a menos de dados de teste, origem ou ambiente.</summary>
    public static bool Equivalent(DiffProjection b, DiffProjection c) => Compare(b, c).All(f => !DiffImpacts.IsPrimary(f.Impact));

    /// <summary>Campos diferentes do par. Campos de contexto (tentativas, duração) só aparecem junto com outra diferença.</summary>
    public static List<AnalysisDiffField> Compare(DiffProjection b, DiffProjection c)
    {
        var fields = new List<AnalysisDiffField>();
        var context = new List<AnalysisDiffField>();
        var descriptions = new List<(AnalysisDiffField Field, string Impact)>();
        foreach (var path in b.Facets.Select(f => f.Path).Concat(c.Facets.Select(f => f.Path)).Distinct())
        {
            var (fb, fc) = (b.Facet(path), c.Facet(path));
            if (JsonNode.DeepEquals(fb?.Value, fc?.Value)) continue;
            var facet = (fc ?? fb)!;
            var onlyValues = fb?.Masked is not null && fc?.Masked is not null && JsonNode.DeepEquals(fb.Masked, fc.Masked);
            var field = Field(facet, onlyValues ? DiffImpacts.TestData : facet.Impact, fb, fc);
            if (onlyValues && facet.Descriptive) descriptions.Add((field, facet.Impact));
            (facet.ContextOnly ? context : fields).Add(field);
        }
        // "501 caracteres" → "601 caracteres" junto com o limite alterado é consequência do comportamento, não dado de teste.
        if (fields.Any(f => f.Impact == DiffImpacts.Behavior))
            foreach (var (field, impact) in descriptions) field.Impact = impact;
        return fields.Count == 0 ? [] : [.. fields, .. context];
    }

    /// <summary>Campos relevantes de um item que só existe de um lado (o restante fica na estrutura completa).</summary>
    public static List<AnalysisDiffField> Fields(DiffProjection? b, DiffProjection? c) =>
        (c ?? b)!.Facets.Where(f => !f.ContextOnly && DiffImpacts.IsPrimary(f.Impact) && (f.Display ?? f.Value) is not null)
            .Select(f => Field(f, f.Impact, b is null ? null : f, c is null ? null : f)).ToList();

    private static AnalysisDiffField Field(DiffFacet facet, string impact, DiffFacet? before, DiffFacet? after) => new()
    {
        Path = facet.Path,
        Label = facet.Label,
        Impact = impact,
        Before = (before?.Display ?? before?.Value)?.DeepClone(),
        After = (after?.Display ?? after?.Value)?.DeepClone(),
    };

    private static string? KeyAt(DiffProjection p, int level) => level < p.Keys.Count ? p.Keys[level] : null;

    /// <summary>Sobras ligadas por alguma chave (em qualquer nível) ao outro lado: correspondência ambígua, nunca inclusão/remoção.</summary>
    private static IEnumerable<DiffMatch> Leftovers(List<DiffProjection> bases, List<DiffProjection> currents)
    {
        var items = bases.Select(p => (Item: p, IsBase: true)).Concat(currents.Select(p => (Item: p, IsBase: false))).ToList();
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var owners = new Dictionary<(int, string), int>();
        for (var i = 0; i < items.Count; i++)
            for (var level = 0; level < items[i].Item.Keys.Count; level++)
            {
                if (items[i].Item.Keys[level] is not { } key) continue;
                if (owners.TryGetValue((level, key), out var j)) parent[Find(i)] = Find(j);
                else owners[(level, key)] = i;
            }

        foreach (var cluster in Enumerable.Range(0, items.Count).GroupBy(Find).Select(g => g.Select(i => items[i]).ToList()))
        {
            var bs = cluster.Where(x => x.IsBase).Select(x => x.Item).ToList();
            var cs = cluster.Where(x => !x.IsBase).Select(x => x.Item).ToList();
            if (bs.Count == 0 || cs.Count == 0)
            {
                foreach (var b in bs) yield return new DiffMatch(b, null);
                foreach (var c in cs) yield return new DiffMatch(null, c);
                continue;
            }
            var note = $"Correspondência ambígua: {bs.Count} item(ns) da versão anterior e {cs.Count} da atual podem ser os mesmos; "
                + "não foi possível decidir com segurança se houve inclusão, remoção ou alteração.";
            // Só para exibir lado a lado: os mais parecidos (posição como desempate), sempre como incertos.
            var candidates = (from b in bs from c in cs select (b, c, Score: Similarity(b, c), Distance: Math.Abs(b.Position - c.Position)))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Distance).ToList();
            foreach (var (b, c, _, _) in candidates)
            {
                if (!bs.Contains(b) || !cs.Contains(c)) continue;
                bs.Remove(b);
                cs.Remove(c);
                yield return new DiffMatch(b, c, true, note);
            }
            foreach (var b in bs) yield return new DiffMatch(b, null, true, note);
            foreach (var c in cs) yield return new DiffMatch(null, c, true, note);
        }
    }

    private static int Similarity(DiffProjection b, DiffProjection c) =>
        b.Facets.Count(f => f.Value is not null && JsonNode.DeepEquals(f.Value, c.Facet(f.Path)?.Value));
}
