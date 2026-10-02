using System.Globalization;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Valor de teste de uma regra: partição de equivalência ou valor-limite, válido ou inválido.</summary>
public sealed record Probe(Pred Constraint, bool Valid, string Label, string Technique);

/// <summary>Validação de entrada estruturada (Fase 3): campo, tipo de restrição, limites e mensagem.</summary>
public sealed class ValidationRule
{
    public required string Id { get; init; }

    public required InputField Field { get; init; }

    public required Var Var { get; init; }

    /// <summary>required, notNull, notEmpty, maxLength, minLength, length, range, gt, gte, lt, lte, email, enum, equal, notEqual, opaque.</summary>
    public required string Kind { get; init; }

    public Term? Min { get; init; }

    public Term? Max { get; init; }

    /// <summary>[Required(AllowEmptyStrings = true)]: só null falha.</summary>
    public bool AllowEmpty { get; init; }

    /// <summary>FluentValidation .When(...) / .Unless(...).</summary>
    public Pred? When { get; set; }

    /// <summary>Condição do When como no código, com o parâmetro da lambda trocado pelo caminho do campo ("request.VeiculoId.HasValue").</summary>
    public string? WhenCode { get; set; }

    public string? Message { get; set; }

    /// <summary>DataAnnotations, FluentValidation ou implícito (referência não anulável).</summary>
    public required string Origin { get; init; }

    /// <summary>Como está no código: "[MaxLength(500)]", "NotEmpty()".</summary>
    public required string Text { get; init; }

    public SourceReference? Source { get; init; }

    public Pred Violation => _violation ??= When is null ? BaseViolation() : Pred.And(When, BaseViolation());

    private Pred? _violation;

    public string FieldName => Field.Location == InputLocations.Body ? JsonPath(Field) : Field.Name;

    public static string JsonPath(InputField field)
    {
        var parts = new List<string>();
        for (var f = field; f?.Parent is not null; f = f.Parent) parts.Insert(0, f.Member == "[]" ? "[0]" : f.Name);
        return string.Join(".", parts).Replace(".[0]", "[0]");
    }

    /// <summary>Expressão da violação no estilo do código (a interface traduz para português).</summary>
    public string ViolationCode
    {
        get
        {
            var x = Field.Path;
            var core = Kind switch
            {
                "required" when Var.Kind == VarKind.String && !AllowEmpty => $"string.IsNullOrWhiteSpace({x})",
                "notEmpty" when Var.Kind == VarKind.String => $"string.IsNullOrWhiteSpace({x})",
                "required" or "notNull" => $"{x} == null",
                "notEmpty" when Var.Kind == VarKind.Collection => $"{x} == null || {x}.Count == 0",
                "notEmpty" => $"{x} == {(Var.Kind == VarKind.Date ? "DateTime.MinValue" : Var.Kind == VarKind.Guid ? "Guid.Empty" : "0")}",
                "maxLength" => $"{x}.{LengthName} > {Show(Max)}",
                "minLength" => $"{x}.{LengthName} < {Show(Min)}",
                "length" => Show(Min) == Show(Max) ? $"{x}.{LengthName} != {Show(Min)}" : $"{x}.{LengthName} < {Show(Min)} || {x}.{LengthName} > {Show(Max)}",
                "range" => $"{x} < {Show(Min)} || {x} > {Show(Max)}",
                "exclusiveRange" => $"{x} <= {Show(Min)} || {x} >= {Show(Max)}",
                "gt" => $"{x} <= {Show(Min)}",
                "gte" => $"{x} < {Show(Min)}",
                "lt" => $"{x} >= {Show(Max)}",
                "lte" => $"{x} > {Show(Max)}",
                "equal" => $"{x} != {Show(Min)}",
                "notEqual" => $"{x} == {Show(Min)}",
                "email" => $"!EmailValido({x})",
                "enum" => $"!Enum.IsDefined({x})",
                _ => $"{x}: {Text} violada",
            };
            return When is null ? core : $"{WhenCode ?? When.Text} && ({core})";
        }
    }

    private string LengthName => Var.Kind == VarKind.Collection ? "Count" : "Length";

    private static string Show(Term? t) => t switch
    {
        ConstTerm c => c.Display,
        NowTerm n => n.OffsetDays == 0 ? n.Exact ? "DateTime.Now" : "DateTime.Today" : $"DateTime.Today.AddDays({n.OffsetDays})",
        _ => "?",
    };

    private Pred BaseViolation()
    {
        var v = Var;
        var code = ViolationCode;
        Pred isNull = new NullPred(v, $"{Field.Path} == null");
        Pred notNull = Pred.Not(isNull);
        var value = new VarTerm(v);
        var length = new VarTerm(v, Measure.Length);
        Pred Cmp(Term l, string op, Term? r) => r is null ? Pred.False : new CmpPred(l, op, r, code);

        return Kind switch
        {
            "required" when v.Kind == VarKind.String && !AllowEmpty => new BlankPred(v, true, $"string.IsNullOrWhiteSpace({Field.Path})"),
            "notEmpty" when v.Kind == VarKind.String => new BlankPred(v, true, $"string.IsNullOrWhiteSpace({Field.Path})"),
            "required" or "notNull" => isNull,
            "notEmpty" when v.Kind == VarKind.Collection => Pred.Or(isNull, Cmp(length, "==", ConstTerm.Of(0))),
            "notEmpty" => Pred.Or(isNull, Cmp(value, "==", EmptyValue(v))),
            "maxLength" => Pred.And(notNull, Cmp(length, ">", Max)),
            "minLength" => Pred.And(notNull, Cmp(length, "<", Min)),
            "length" => Pred.And(notNull, Pred.Or(Cmp(length, "<", Min), Cmp(length, ">", Max))),
            "range" => Pred.And(notNull, Pred.Or(Cmp(value, "<", Min), Cmp(value, ">", Max))),
            "exclusiveRange" => Pred.And(notNull, Pred.Or(Cmp(value, "<=", Min), Cmp(value, ">=", Max))),
            "gt" => Pred.And(notNull, Cmp(value, "<=", Min)),
            "gte" => Pred.And(notNull, Cmp(value, "<", Min)),
            "lt" => Pred.And(notNull, Cmp(value, ">=", Max)),
            "lte" => Pred.And(notNull, Cmp(value, ">", Max)),
            "equal" => Pred.And(notNull, Cmp(value, "!=", Min)),
            "notEqual" => Pred.And(notNull, Cmp(value, "==", Min)),
            "email" => Pred.And(notNull, Pred.Not(new FormatPred(v, "email", $"EmailValido({Field.Path})"))),
            "enum" => Pred.And(notNull, Pred.Not(new EnumDefinedPred(v, $"Enum.IsDefined({Field.Path})"))),
            _ => new OpaquePred($"V:{Id}", code),
        };
    }

    private static ConstTerm EmptyValue(Var v) => v.Kind switch
    {
        VarKind.Date => ConstTerm.Of(Values.MinDateDays, "DateTime.MinValue"),
        VarKind.Guid => ConstTerm.Of(Values.EmptyGuid, "Guid.Empty"),
        VarKind.Bool => ConstTerm.Of(false),
        _ => ConstTerm.Of(0),
    };

    /// <summary>Partições inválidas e valores-limite (dos dois lados) da regra.</summary>
    public IEnumerable<Probe> Probes()
    {
        var v = Var;
        var name = FieldName;
        var length = new VarTerm(v, Measure.Length);
        var value = new VarTerm(v);
        var ep = ScenarioTechniques.EquivalencePartitioning;
        var bva = ScenarioTechniques.BoundaryValue;
        Pred notNull = Pred.Not(new NullPred(v, $"{Field.Path} == null"));
        Pred Len(int n) => Pred.And(notNull, new CmpPred(length, "==", ConstTerm.Of(n), $"{Field.Path}.{LengthName} == {n}"));
        Pred Eq(Term t) => Pred.And(notNull, new CmpPred(value, "==", t, $"{Field.Path} == {Show(t)}"));
        string Items(int n) => v.Kind == VarKind.Collection ? $"{n} item(ns)" : $"{n} caractere(s)";

        var probes = new List<Probe>();
        switch (Kind)
        {
            case "required" or "notEmpty" or "notNull":
                if (v.Nullable || v.Kind == VarKind.String)
                    probes.Add(new(new NullPred(v, $"{Field.Path} == null"), false, $"{name} ausente (null)", ep));
                if (v.Kind == VarKind.String && (Kind == "notEmpty" || (Kind == "required" && !AllowEmpty)))
                {
                    probes.Add(new(Len(0), false, $"{name} vazio (\"\")", ep));
                    probes.Add(new(Pred.And(Len(3), new BlankPred(v, true, $"string.IsNullOrWhiteSpace({Field.Path})")), false, $"{name} só com espaços", ep));
                }
                else if (Kind == "notEmpty" && v.Kind == VarKind.Collection)
                    probes.Add(new(Len(0), false, $"{name} sem itens", ep));
                else if (Kind == "notEmpty" && v.Kind is not VarKind.String)
                    probes.Add(new(Eq(EmptyValue(v)), false, $"{name} com o valor padrão ({EmptyValue(v).Display})", ep));
                break;
            case "maxLength" when Number(Max) is { } max:
                probes.Add(new(Len(max + 1), false, $"{name} com {Items(max + 1)} (máximo {max})", bva));
                probes.Add(new(Len(max), true, $"{name} com {Items(max)} (limite máximo)", bva));
                break;
            case "minLength" when Number(Min) is { } min:
                if (min > 0) probes.Add(new(Len(min - 1), false, $"{name} com {Items(min - 1)} (mínimo {min})", bva));
                probes.Add(new(Len(min), true, $"{name} com {Items(min)} (limite mínimo)", bva));
                break;
            case "length" when Number(Min) is { } lo && Number(Max) is { } hi:
                if (lo > 0) probes.Add(new(Len(lo - 1), false, $"{name} com {Items(lo - 1)} (mínimo {lo})", bva));
                probes.Add(new(Len(hi + 1), false, $"{name} com {Items(hi + 1)} (máximo {hi})", bva));
                probes.Add(new(Len(lo), true, $"{name} com {Items(lo)} (limite mínimo)", bva));
                if (hi != lo) probes.Add(new(Len(hi), true, $"{name} com {Items(hi)} (limite máximo)", bva));
                break;
            case "range" or "exclusiveRange" or "gt" or "gte" or "lt" or "lte":
                foreach (var (bound, op) in Bounds())
                {
                    var (invalid, valid) = Adjacent(bound, op);
                    if (invalid is not null) probes.Add(new(Eq(invalid), false, $"{name} = {Show(invalid)} ({Describe(op, bound)})", bva));
                    if (valid is not null) probes.Add(new(Eq(valid), true, $"{name} = {Show(valid)} (limite válido)", bva));
                }
                break;
            case "email":
                probes.Add(new(BaseViolation(), false, $"{name} com formato inválido", ep));
                break;
            case "enum":
                probes.Add(new(BaseViolation(), false, $"{name} com valor fora do enum", ep));
                break;
            case "equal" or "notEqual":
                probes.Add(new(BaseViolation(), false, $"{name} {(Kind == "equal" ? "diferente de" : "igual a")} {Show(Min)}", ep));
                break;
        }

        return When is null ? probes : probes.Select(p => p with { Constraint = Pred.And(When, p.Constraint) });
    }

    private string Describe(string op, Term bound) => op switch
    {
        ">" => $"deve ser maior que {Show(bound)}",
        ">=" => $"deve ser maior ou igual a {Show(bound)}",
        "<" => $"deve ser menor que {Show(bound)}",
        "<=" => $"deve ser menor ou igual a {Show(bound)}",
        _ => "",
    };

    /// <summary>Limites da regra com a comparação que o valor válido precisa cumprir.</summary>
    private IEnumerable<(Term Bound, string Op)> Bounds() => Kind switch
    {
        "range" => [(Min!, ">="), (Max!, "<=")],
        "exclusiveRange" => [(Min!, ">"), (Max!, "<")],
        "gt" => [(Min!, ">")],
        "gte" => [(Min!, ">=")],
        "lt" => [(Max!, "<")],
        "lte" => [(Max!, "<=")],
        _ => [],
    };

    /// <summary>Valores dos dois lados do limite: o primeiro que falha e o primeiro que passa.</summary>
    private (Term? Invalid, Term? Valid) Adjacent(Term bound, string op)
    {
        var step = Var.IsIntegral(Measure.Value) ? 1m : 0.01m;
        Term? Shift(decimal delta) => bound switch
        {
            ConstTerm { Number: { } n } => ConstTerm.Of(n + delta),
            NowTerm now when Var.Kind == VarKind.Date => now with { OffsetDays = now.OffsetDays + (int)Math.Sign(delta) },
            _ => null,
        };
        return op switch
        {
            ">" => (Shift(0) ?? bound, Shift(step)),
            ">=" => (Shift(-step), bound),
            "<" => (bound, Shift(-step)),
            "<=" => (Shift(step), bound),
            _ => (null, null),
        };
    }

    private static int? Number(Term? t) => t is ConstTerm { Number: { } n } ? (int)n : null;

    /// <summary>Mensagem padrão (quando não há ErrorMessage/WithMessage), como o ASP.NET Core e o FluentValidation geram.</summary>
    public string DefaultMessageText()
    {
        var display = Field.Member;
        var words = System.Text.RegularExpressions.Regex.Replace(display, "([a-z0-9])([A-Z])", "$1 $2");
        if (Origin == "FluentValidation")
            return Kind switch
            {
                "notEmpty" => $"'{words}' must not be empty.",
                "notNull" => $"'{words}' must not be empty.",
                "maxLength" => $"The length of '{words}' must be {Show(Max)} characters or fewer.",
                "minLength" => $"The length of '{words}' must be at least {Show(Min)} characters.",
                "length" => $"'{words}' must be between {Show(Min)} and {Show(Max)} characters.",
                "range" => $"'{words}' must be between {Show(Min)} and {Show(Max)}.",
                "exclusiveRange" => $"'{words}' must be between {Show(Min)} and {Show(Max)} (exclusive).",
                "gt" => $"'{words}' must be greater than '{Show(Min)}'.",
                "gte" => $"'{words}' must be greater than or equal to '{Show(Min)}'.",
                "lt" => $"'{words}' must be less than '{Show(Max)}'.",
                "lte" => $"'{words}' must be less than or equal to '{Show(Max)}'.",
                "email" => $"'{words}' is not a valid email address.",
                "enum" => $"'{words}' has a range of values which does not include the value.",
                "equal" => $"'{words}' must be equal to '{Show(Min)}'.",
                "notEqual" => $"'{words}' must not be equal to '{Show(Min)}'.",
                _ => $"'{words}': {Text}",
            };
        return Kind switch
        {
            "required" => $"The {display} field is required.",
            "maxLength" => $"The field {display} must be a string or array type with a maximum length of '{Show(Max)}'.",
            "minLength" => $"The field {display} must be a string or array type with a minimum length of '{Show(Min)}'.",
            "length" when Text.StartsWith("[StringLength", StringComparison.Ordinal) => Number(Min) is > 0
                ? $"The field {display} must be a string with a minimum length of {Show(Min)} and a maximum length of {Show(Max)}."
                : $"The field {display} must be a string with a maximum length of {Show(Max)}.",
            "length" => $"The field {display} must be a string or collection type with a minimum length of '{Show(Min)}' and maximum length of '{Show(Max)}'.",
            "range" => $"The field {display} must be between {Show(Min)} and {Show(Max)}.",
            "email" => $"The {display} field is not a valid e-mail address.",
            _ => $"The field {display} is invalid.",
        };
    }

    public string MessageOrDefault => Message ?? DefaultMessageText();
}

/// <summary>
/// Extrai as validações de entrada do endpoint: DataAnnotations (parâmetros e propriedades dos DTOs), o [Required]
/// implícito do MVC para referências não anuláveis e as regras RuleFor dos validators do FluentValidation.
/// </summary>
public static class ValidationRuleExtractor
{
    public static async Task<List<ValidationRule>> ExtractAsync(CallGraph graph, InputModel input, SymbolicResolver resolver, CancellationToken cancellationToken)
    {
        var rules = new List<ValidationRule>();
        var id = 0;
        string NextId() => $"V{++id}";

        foreach (var field in input.All)
        {
            var symbolAttributes = (field.Property?.GetAttributes() ?? field.Parameter?.GetAttributes() ?? []).AsEnumerable();
            // record Dto([Required] string Nome): o atributo fica no parâmetro do construtor primário.
            if (field.Property is { } property)
                symbolAttributes = symbolAttributes.Concat(property.ContainingType.InstanceConstructors
                    .SelectMany(c => c.Parameters.Where(p => p.Name == property.Name))
                    .SelectMany(p => p.GetAttributes()));

            var hasRequired = false;
            foreach (var attribute in symbolAttributes.DistinctBy(a => a.ApplicationSyntaxReference?.Span))
            {
                var rule = FromAttribute(attribute, field, resolver.Vars.Input(field), graph, NextId);
                if (rule is null) continue;
                hasRequired |= rule.Kind == "required";
                rules.Add(rule);
            }

            if (!hasRequired && field.ImplicitRequired && !(field.Parent is null && field.Location == InputLocations.Body)
                && field.Kind is VarKind.String or VarKind.Object or VarKind.Collection)
                rules.Add(new ValidationRule
                {
                    Id = NextId(),
                    Field = field,
                    Var = resolver.Vars.Input(field),
                    Kind = "required",
                    AllowEmpty = false,
                    Origin = "implícito",
                    Text = $"{field.TypeDisplay} não anulável ([Required] implícito do MVC)",
                    Source = field.Property?.SourceLocation() is { } location
                        ? new SourceReference { File = graph.Solution.RelativePath(location.SourceTree?.FilePath), Line = location.StartLine() }
                        : null,
                });
        }

        rules.AddRange(await FluentValidationAsync(graph, input, resolver, NextId, cancellationToken));
        return rules;
    }

    private static ValidationRule? FromAttribute(AttributeData attribute, InputField field, Var v, CallGraph graph, Func<string> nextId)
    {
        var name = attribute.AttributeClass?.Name.Replace("Attribute", "") ?? "";
        var args = attribute.ConstructorArguments;
        string? Named(string key) => attribute.NamedArguments.FirstOrDefault(a => a.Key == key).Value.Value?.ToString();
        Term? Arg(int index) => index < args.Length && args[index].Value is { } value && value is not ITypeSymbol
            ? ConstTerm.Of(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) : null;

        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
        var text = syntax is null ? $"[{name}]" : $"[{syntax.Compact()}]";

        string? kind = name switch
        {
            // Value type não anulável sempre tem valor: [Required] não tem efeito.
            "Required" when field.Type.IsValueType && !field.Nullable => null,
            "Required" => "required",
            "MaxLength" => "maxLength",
            "MinLength" => "minLength",
            "StringLength" => "length",
            "Length" => "length",
            "Range" when args.Length == 2 => "range",
            "EmailAddress" => "email",
            _ => null,
        };
        if (kind is null) return null;

        Term? min = null, max = null;
        switch (name)
        {
            case "MaxLength": max = Arg(0); break;
            case "MinLength": min = Arg(0); break;
            case "StringLength":
                max = Arg(0);
                min = ConstTerm.Of(int.TryParse(Named("MinimumLength"), out var minimum) ? minimum : 0);
                break;
            case "Length": min = Arg(0); max = Arg(1); break;
            case "Range": min = Arg(0); max = Arg(1); break;
        }
        if (kind is "maxLength" or "length" && max is null || kind is "minLength" && min is null || kind == "range" && (min is null || max is null))
            return null;

        return new ValidationRule
        {
            Id = nextId(),
            Field = field,
            Var = v,
            Kind = kind,
            Min = min,
            Max = max,
            AllowEmpty = Named("AllowEmptyStrings") is "True",
            Message = Named("ErrorMessage"),
            Origin = "DataAnnotations",
            Text = text,
            Source = syntax is null ? null : new SourceReference { File = graph.Solution.RelativePath(syntax.SyntaxTree.FilePath), Line = syntax.StartLine() },
        };
    }

    /// <summary>RuleFor(x => x.Campo).Validador()...WithMessage(...).When(...) dos AbstractValidator&lt;T&gt; dos tipos recebidos.</summary>
    private static async Task<List<ValidationRule>> FluentValidationAsync(CallGraph graph, InputModel input, SymbolicResolver resolver,
        Func<string> nextId, CancellationToken cancellationToken)
    {
        var rules = new List<ValidationRule>();
        var roots = input.Roots.Where(r => r.Kind == VarKind.Object && r.Type is INamedTypeSymbol).ToList();
        if (roots.Count == 0) return rules;

        foreach (var project in graph.Solution.Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                if (!root.DescendantNodes().OfType<SimpleNameSyntax>().Any(n => n.Identifier.Text == "AbstractValidator")) continue;
                var model = compilation.GetSemanticModel(tree);

                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(cls, cancellationToken) is not INamedTypeSymbol validator) continue;
                    var validated = ValidatedType(validator);
                    var target = validated is null ? null : roots.FirstOrDefault(r => r.Type.Key() == validated.Key());
                    if (target is null) continue;

                    foreach (var statement in cls.DescendantNodes().OfType<ExpressionStatementSyntax>())
                        rules.AddRange(RuleChain(statement, model, target, graph, resolver, nextId));
                }
            }
        }

        return rules;
    }

    private static IEnumerable<ValidationRule> RuleChain(ExpressionStatementSyntax statement, SemanticModel model, InputField target,
        CallGraph graph, SymbolicResolver resolver, Func<string> nextId)
    {
        // a.RuleFor(...).X().Y() → [RuleFor, X, Y]
        var chain = new List<InvocationExpressionSyntax>();
        for (var e = statement.Expression; e is InvocationExpressionSyntax inv;)
        {
            chain.Insert(0, inv);
            e = inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null!;
            if (e is null) break;
        }
        if (chain.Count < 2 || NameOf(chain[0]) != "RuleFor") yield break;
        if (chain[0].ArgumentList.Arguments.FirstOrDefault()?.Expression is not SimpleLambdaExpressionSyntax { Body: ExpressionSyntax body } lambda)
            yield break;

        var field = FieldOf(body, lambda.Parameter.Identifier.Text, target);
        if (field is null) yield break;
        var v = resolver.Vars.Input(field);
        var lambdaSymbol = model.GetDeclaredSymbol(lambda.Parameter);

        var pending = new List<ValidationRule>();
        foreach (var call in chain.Skip(1))
        {
            var name = NameOf(call);
            var args = call.ArgumentList.Arguments;
            Term? Arg(int i) => i < args.Count ? resolver.Term(args[i].Expression, new Scope(model, null)) : null;

            switch (name)
            {
                case "WithMessage" when pending.Count > 0:
                    pending[^1].Message = args.Count > 0 && model.GetConstantValue(args[0].Expression) is { HasValue: true, Value: string m } ? m
                        : args.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
                    continue;
                case "When" or "Unless" or "WhenAsync" or "UnlessAsync":
                    if (args.FirstOrDefault()?.Expression is SimpleLambdaExpressionSyntax { Body: ExpressionSyntax condition } whenLambda
                        && model.GetDeclaredSymbol(whenLambda.Parameter) is { } whenSymbol)
                    {
                        var scope = new Scope(model, null, new Dictionary<ISymbol, Term>(SymbolEqualityComparer.Default) { [whenSymbol] = new VarTerm(resolver.Vars.Input(target)) });
                        var pred = resolver.Pred(condition, scope);
                        var code = System.Text.RegularExpressions.Regex.Replace(condition.Compact(),
                            $@"\b{System.Text.RegularExpressions.Regex.Escape(whenLambda.Parameter.Identifier.Text)}\b", target.Path);
                        if (name.StartsWith("Unless", StringComparison.Ordinal))
                        {
                            pred = Pred.Not(pred);
                            code = $"!({code})";
                        }
                        foreach (var rule in pending.Where(r => r.When is null))
                        {
                            rule.When = pred;
                            rule.WhenCode = code;
                        }
                    }
                    continue;
                case "WithName" or "WithErrorCode" or "OverridePropertyName" or "WithSeverity" or "WithState" or "Cascade" or "DependentRules":
                    continue;
            }

            var (kind, min, max) = name switch
            {
                "NotNull" => ("notNull", null, null),
                "NotEmpty" => ("notEmpty", null, null),
                "MaximumLength" => ("maxLength", null, Arg(0)),
                "MinimumLength" => ("minLength", Arg(0), null),
                "Length" when args.Count == 2 => ("length", Arg(0), Arg(1)),
                "Length" when args.Count == 1 => ("length", Arg(0), Arg(0)),
                "GreaterThan" => ("gt", Arg(0), null),
                "GreaterThanOrEqualTo" => ("gte", Arg(0), null),
                "LessThan" => ("lt", null, Arg(0)),
                "LessThanOrEqualTo" => ("lte", null, Arg(0)),
                "InclusiveBetween" => ("range", Arg(0), Arg(1)),
                "ExclusiveBetween" => ("exclusiveRange", Arg(0), Arg(1)),
                "EmailAddress" => ("email", null, null),
                "IsInEnum" => ("enum", null, null),
                "Equal" => ("equal", Arg(0), null),
                "NotEqual" => ("notEqual", Arg(0), null),
                _ => ("opaque", (Term?)null, (Term?)null),
            };
            // Limite que não é constante (outro campo, serviço): a regra fica, mas sem valores de teste.
            if (kind is not ("notNull" or "notEmpty" or "email" or "enum" or "opaque") && (min ?? max) is not (ConstTerm or NowTerm))
                kind = "opaque";

            pending.Add(new ValidationRule
            {
                Id = nextId(),
                Field = field,
                Var = v,
                Kind = kind,
                Min = min,
                Max = max,
                Origin = "FluentValidation",
                Text = $"RuleFor({lambda.Compact()}).{call.Expression switch { MemberAccessExpressionSyntax m => m.Name.ToString(), var x => x.ToString() }}{call.ArgumentList.Compact()}",
                Source = new SourceReference { File = graph.Solution.RelativePath(statement.SyntaxTree.FilePath), Line = call.StartLine() },
            });
        }

        foreach (var rule in pending) yield return rule;
    }

    private static string? NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    /// <summary>x.Endereco.Cep → campo request.Endereco.Cep.</summary>
    private static InputField? FieldOf(ExpressionSyntax body, string parameter, InputField root)
    {
        var members = new List<string>();
        var e = body;
        while (e is MemberAccessExpressionSyntax ma)
        {
            members.Insert(0, ma.Name.Identifier.Text);
            e = ma.Expression;
        }
        if (e is not IdentifierNameSyntax id || id.Identifier.Text != parameter) return null;
        var field = root;
        foreach (var member in members)
        {
            field = field.Child(member);
            if (field is null) return null;
        }
        return field == root ? null : field;
    }

    private static INamedTypeSymbol? ValidatedType(INamedTypeSymbol validator)
    {
        for (var t = validator.BaseType; t is not null; t = t.BaseType)
            if (t is { Name: "AbstractValidator", TypeArguments.Length: 1 })
                return t.TypeArguments[0] as INamedTypeSymbol;
        return null;
    }
}
