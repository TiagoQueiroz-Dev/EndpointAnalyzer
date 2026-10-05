using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Variáveis do endpoint: campos do payload, consultas ao estado e membros delas.</summary>
public sealed class VarTable(InputModel input)
{
    private readonly Dictionary<string, Var> _vars = [];

    public IEnumerable<Var> All => _vars.Values;

    public Var Input(InputField field)
    {
        var key = $"in:{field.Path}";
        if (_vars.TryGetValue(key, out var existing)) return existing;
        var parent = field.Parent is null ? null : Input(field.Parent);
        return _vars[key] = new Var
        {
            Key = key,
            Kind = field.Kind,
            Origin = VarOrigin.Input,
            // O body sempre é enviado; os campos seguem a nulidade do tipo.
            Nullable = field.Parent is not null || field.Location != InputLocations.Body ? field.Nullable : false,
            Display = field.Path,
            Field = field,
            Parent = parent,
            EnumMembers = field.EnumMembers,
            TypeName = field.Type.Name,
        };
    }

    public Var? Input(IParameterSymbol parameter) => input.Root(parameter) is { } root ? Input(root) : null;

    public Var Root(StateRoot root, ITypeSymbol type)
    {
        if (_vars.TryGetValue(root.Key, out var existing)) return existing;
        var (underlying, nullable) = InputModel.Underlying(type);
        var kind = InputModel.KindOf(underlying);
        return _vars[root.Key] = new Var
        {
            Key = root.Key,
            Kind = kind,
            Origin = root.Origin,
            Nullable = nullable || (!underlying.IsValueType && kind != VarKind.Collection),
            Display = root.Render(null),
            Root = root,
            EnumMembers = kind == VarKind.Enum ? InputModel.EnumMembersOf(underlying) : null,
            TypeName = underlying.Name,
        };
    }

    /// <summary>Membro de um objeto: campo do DTO (payload) ou propriedade do resultado de uma consulta.</summary>
    public Var? Member(Var parent, string name, ITypeSymbol type)
    {
        if (parent.Origin == VarOrigin.Input)
            return parent.Field?.Child(name) is { } child ? Input(child) : null;

        var key = $"{parent.Key}#{name}";
        if (_vars.TryGetValue(key, out var existing)) return existing;
        var (underlying, nullable) = InputModel.Underlying(type);
        var kind = InputModel.KindOf(underlying);
        return _vars[key] = new Var
        {
            Key = key,
            Kind = kind,
            Origin = parent.Origin,
            Nullable = nullable || (!underlying.IsValueType && type.NullableAnnotation != NullableAnnotation.NotAnnotated),
            Display = $"{parent.Display}.{name}",
            Root = parent.Root,
            Member = parent.Member is null ? name : $"{parent.Member}.{name}",
            Parent = parent,
            EnumMembers = kind == VarKind.Enum ? InputModel.EnumMembersOf(underlying) : null,
            TypeName = underlying.Name,
        };
    }
}

/// <summary>Onde uma expressão é avaliada: modelo semântico, método do grafo e símbolos já ligados (parâmetro de lambda).</summary>
public sealed record Scope(SemanticModel Model, AnalyzedMethod? Method, IReadOnlyDictionary<ISymbol, Term>? Bindings = null);

/// <summary>
/// Traduz condições do código (Roslyn) para predicados sobre o payload e o estado: segue parâmetros até o argumento
/// do chamador (e daí até o endpoint), locais até a inicialização e chamadas até a consulta que produz o valor
/// (repositório, serviço, DbContext). O que não dá para traduzir vira um predicado opaco (suposição do cenário).
/// </summary>
public sealed class SymbolicResolver(CallGraph graph, InputModel input, VarTable vars)
{
    private const int MaxDepth = 16;

    private readonly Dictionary<(SyntaxNode, AnalyzedMethod?), Term?> _terms = [];
    private readonly Dictionary<AnalyzedMethod, Pred> _callerPaths = [];

    public VarTable Vars => vars;

    public InputModel Input => input;

    public Scope ScopeOf(AnalyzedMethod method) => new(method.Model, method);

    // ---- Termos ----

    public Term? Term(ExpressionSyntax expression, Scope scope, int depth = 0)
    {
        if (depth > MaxDepth) return null;
        if (scope.Bindings is null)
        {
            var cacheKey = ((SyntaxNode)expression, scope.Method);
            if (_terms.TryGetValue(cacheKey, out var cached)) return cached;
            return _terms[cacheKey] = ResolveTerm(expression, scope, depth);
        }
        return ResolveTerm(expression, scope, depth);
    }

    private Term? ResolveTerm(ExpressionSyntax expression, Scope scope, int depth)
    {
        var e = Unwrap(expression);
        var model = scope.Model;

        if (Constant(e, model) is { } constant) return constant;

        var symbol = model.GetSymbolInfo(e).Symbol;

        if (symbol is IPropertySymbol { Name: "Today" or "Now" or "UtcNow", IsStatic: true } now
            && now.ContainingType.Name is "DateTime" or "DateTimeOffset")
            return new NowTerm(0, Exact: now.Name != "Today");
        if (symbol is IFieldSymbol { Name: "MinValue", IsStatic: true } min && min.ContainingType.Name is "DateTime" or "DateTimeOffset" or "DateOnly")
            return ConstTerm.Of(Values.MinDateDays, "DateTime.MinValue");
        if (symbol is IFieldSymbol { Name: "Empty", IsStatic: true } empty && empty.ContainingType.Name == "Guid")
            return ConstTerm.Of(Values.EmptyGuid, "Guid.Empty");
        if (e is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.DefaultLiteralExpression) || e is DefaultExpressionSyntax)
            return DefaultOf(model.GetTypeInfo(e).ConvertedType ?? model.GetTypeInfo(e).Type);

        if (scope.Bindings is not null && symbol is not null && scope.Bindings.TryGetValue(symbol, out var bound)) return bound;

        switch (e)
        {
            case IdentifierNameSyntax:
                return symbol switch
                {
                    IParameterSymbol p => Parameter(p, scope, depth),
                    ILocalSymbol l => Local(l, scope, depth),
                    IFieldSymbol or IPropertySymbol => MemberOfThis(symbol, scope, depth),
                    _ => null,
                };

            case MemberAccessExpressionSyntax ma:
                return MemberAccess(ma, symbol, scope, depth);

            case ConditionalAccessExpressionSyntax ca when ca.WhenNotNull is MemberBindingExpressionSyntax mb:
                return MemberOf(Term(ca.Expression, scope, depth + 1), mb.Name.Identifier.Text, model.GetSymbolInfo(mb).Symbol);

            case ConditionalAccessExpressionSyntax { WhenNotNull: MemberAccessExpressionSyntax { Expression: MemberBindingExpressionSyntax inner } outer } ca2:
                return MemberOf(MemberOf(Term(ca2.Expression, scope, depth + 1), inner.Name.Identifier.Text, model.GetSymbolInfo(inner).Symbol),
                    outer.Name.Identifier.Text, model.GetSymbolInfo(outer).Symbol);

            case InvocationExpressionSyntax invocation:
                return Invocation(invocation, symbol as IMethodSymbol, scope, depth);

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) && Unwrap(b.Right) is ThrowExpressionSyntax:
                return Term(b.Left, scope, depth + 1);

            case BinaryExpressionSyntax b when ArithmeticOp(b) is { } op:
            {
                var left = Term(b.Left, scope, depth + 1);
                var right = Term(b.Right, scope, depth + 1);
                return left is null || right is null ? null : new ArithTerm(op, left, right);
            }

            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.UnaryMinusExpression) && Term(u.Operand, scope, depth + 1) is { } operand:
                return operand is ConstTerm { Number: { } n } ? ConstTerm.Of(-n) : new ArithTerm("-", ConstTerm.Of(0), operand);
        }

        return null;
    }

    private Term? MemberAccess(MemberAccessExpressionSyntax ma, ISymbol? symbol, Scope scope, int depth)
    {
        var name = ma.Name.Identifier.Text;

        if (symbol is IPropertySymbol { Name: "Value" } && IsNullable(scope.Model.GetTypeInfo(ma.Expression).Type))
            return Term(ma.Expression, scope, depth + 1);
        if (symbol is IPropertySymbol { Name: "Date" } && Term(ma.Expression, scope, depth + 1) is NowTerm today)
            return today with { Exact = false };
        if (symbol is IPropertySymbol { Name: "Date" } or IPropertySymbol { Name: "Value" } && Term(ma.Expression, scope, depth + 1) is VarTerm { Var.Kind: VarKind.Date } date)
            return date;
        if (symbol is IPropertySymbol { Name: "Length" or "Count" } && Term(ma.Expression, scope, depth + 1) is VarTerm { Measure: Measure.Value, Var.Kind: VarKind.String or VarKind.Collection } sized)
            return sized with { Measure = Measure.Length };

        if (ma.Expression is ThisExpressionSyntax or BaseExpressionSyntax)
            return symbol is null ? null : MemberOfThis(symbol, scope, depth);

        // Membro estático que não é constante (configuração, Program.HierarquiaIdRaiz): valor do contexto.
        if (symbol is IFieldSymbol { IsStatic: true } or IPropertySymbol { IsStatic: true })
            return Context($"{symbol.ContainingType.Name}.{symbol.Name}", TypeOf(symbol)!, ma);

        return MemberOf(Term(ma.Expression, scope, depth + 1), name, symbol);
    }

    private Term? MemberOf(Term? target, string name, ISymbol? symbol)
    {
        if (target is not VarTerm { Measure: Measure.Value } vt || TypeOf(symbol) is not { } type) return null;
        if (name is "Length" or "Count" && vt.Var.Kind is VarKind.String or VarKind.Collection) return vt with { Measure = Measure.Length };
        return vars.Member(vt.Var, name, type) is { } member ? new VarTerm(member) : null;
    }

    private Term? Invocation(InvocationExpressionSyntax invocation, IMethodSymbol? method, Scope scope, int depth)
    {
        if (method is null) return null;
        var receiver = invocation.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;

        // Conversões que não mudam a regra: x.Trim(), x.ToUpper(), x.GetValueOrDefault().
        if (receiver is not null && method.Name is "Trim" or "TrimStart" or "TrimEnd" or "ToUpper" or "ToLower" or "ToUpperInvariant" or "ToLowerInvariant"
                or "GetValueOrDefault" or "ToString" && invocation.ArgumentList.Arguments.Count == 0)
            return Term(receiver, scope, depth + 1);

        // mapper.Map<AdicionarPedidoCommand>(viewModel): o AutoMapper copia as propriedades de mesmo nome do payload.
        if (method.Name == "Map" && method.ContainingNamespace?.ToDisplayString().StartsWith("AutoMapper", StringComparison.Ordinal) == true
            && invocation.ArgumentList.Arguments.Count == 1
            && Term(invocation.ArgumentList.Arguments[0].Expression, scope, depth + 1) is VarTerm { Var: { Origin: VarOrigin.Input, Kind: VarKind.Object }, Measure: Measure.Value } source)
            return source;

        // DateTime.Today.AddDays(n)
        if (receiver is not null && method.Name == "AddDays" && Term(receiver, scope, depth + 1) is NowTerm now
            && invocation.ArgumentList.Arguments.Count == 1 && Constant(invocation.ArgumentList.Arguments[0].Expression, scope.Model) is ConstTerm { Number: { } days })
            return now with { OffsetDays = now.OffsetDays + (int)days };

        // lista.Count()
        var reduced = method.ReducedFrom ?? method;
        if (reduced.Name is "Count" or "LongCount" && reduced.ContainingType.Name == "Enumerable" && invocation.ArgumentList.Arguments.Count == 0
            && receiver is not null && Term(receiver, scope, depth + 1) is VarTerm { Var.Kind: VarKind.Collection } collection)
            return collection with { Measure = Measure.Length };

        // Método do grafo com um único return: segue o valor retornado (ObterOuFalhar → repository.ObterPorId).
        if (scope.Method?.CallSites.FirstOrDefault(cs => cs.Syntax == invocation) is { } site && graph.MethodOf(site.Node) is { } target
            && !IsBoundary(target.Symbol) && SingleReturn(target.Declaration) is { } returned)
        {
            var inner = Term(returned, ScopeOf(target), depth + 1);
            // Só vale seguir quando chega no payload, numa constante ou numa consulta a repositório/DbContext;
            // ObterTodos().FirstOrDefault(...) fica melhor descrito pela chamada original.
            if (inner is ConstTerm or NowTerm || inner is VarTerm { Var.Origin: VarOrigin.Input } || inner is VarTerm { Var.Root.Repository: true })
                return inner;
        }

        if (method.ReturnsVoid) return null;
        var returnType = UnwrapTask(scope.Method?.ResolveType(method.ReturnType) ?? method.ReturnType);
        return new VarTerm(vars.Root(StateRootOf(invocation, method, scope, depth), returnType));
    }

    /// <summary>Consulta ao estado: chave estável (tipo.método + argumentos resolvidos) e texto com os valores do payload.</summary>
    private StateRoot StateRootOf(InvocationExpressionSyntax invocation, IMethodSymbol method, Scope scope, int depth)
    {
        var template = Template(invocation, scope, depth);
        var receiverType = invocation.Expression is MemberAccessExpressionSyntax ma
            ? scope.Model.GetTypeInfo(ma.Expression).Type?.Name ?? method.ContainingType.Name
            : method.ContainingType.Name;
        var key = $"st:{receiverType}.{method.Name}({string.Concat(template.SkipWhile(p => p is string s && !s.Contains('(')).Select(p => p is Var v ? v.Key : p.ToString()))}";
        return new StateRoot
        {
            Key = key,
            TypeName = UnwrapTask(scope.Method?.ResolveType(method.ReturnType) ?? method.ReturnType).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Template = template,
            Boundary = IsBoundary(method),
            Repository = IsRepository(method),
            Source = scope.Method is { } m ? new SourceReference { File = m.SourceFile, Method = m.DisplayName, Line = invocation.StartLine() } : null,
        };
    }

    /// <summary>Texto da expressão com as partes que vêm do payload trocadas por variáveis.</summary>
    private List<object> Template(ExpressionSyntax expression, Scope scope, int depth)
    {
        var replacements = new List<(Microsoft.CodeAnalysis.Text.TextSpan Span, Var Var)>();
        void Walk(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes())
            {
                if (child is ExpressionSyntax e and (IdentifierNameSyntax or MemberAccessExpressionSyntax or ConditionalAccessExpressionSyntax)
                    && !(child.Parent is MemberAccessExpressionSyntax parent && parent.Name == child)
                    && !(child.Parent is InvocationExpressionSyntax inv && inv.Expression == child)
                    && Term(e, scope, depth + 1) is VarTerm { Var.Origin: VarOrigin.Input, Measure: Measure.Value } input)
                {
                    replacements.Add((child.Span, input.Var));
                    continue;
                }
                Walk(child);
            }
        }
        Walk(expression);

        var parts = new List<object>();
        var text = expression.ToString();
        var start = expression.SpanStart;
        var position = 0;
        foreach (var (span, v) in replacements.OrderBy(r => r.Span.Start))
        {
            parts.Add(Spaces(text[position..(span.Start - start)]));
            parts.Add(v);
            position = span.End - start;
        }
        parts.Add(Spaces(text[position..]));
        return parts.Where(p => p is not string { Length: 0 }).ToList();
    }

    /// <summary>Junta espaços e quebras de linha sem cortar as bordas (o trecho fica entre valores do payload).</summary>
    private static string Spaces(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private Term? Parameter(IParameterSymbol parameter, Scope scope, int depth)
    {
        if (scope.Method is not { } method) return null;

        // Parâmetro de outro símbolo: construtor primário (dependência injetada, como um campo) ou lambda.
        if (!SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol?.OriginalDefinition, method.Symbol.OriginalDefinition))
            return parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? MemberOfThis(parameter, scope, depth) : null;

        if (method == graph.EntryPoint)
            return vars.Input(parameter) is { } v ? new VarTerm(v) : null;

        if (CallSiteOf(method) is not { } site) return null;
        // Handler de mensagem (mediator.Send(command) → Handle(command, ct)): só o parâmetro da mensagem vem da chamada.
        var argument = MessageOf(method) is { } message
            ? parameter.Ordinal == message.ParameterOrdinal ? message.Argument : null
            : Argument(site.Syntax, method.Symbol, parameter);
        if (argument is not null) return Term(argument, ScopeOf(site.Caller), depth + 1);
        return parameter.HasExplicitDefaultValue ? ConstantValue(parameter.ExplicitDefaultValue, parameter.ExplicitDefaultValue?.ToString() ?? "null") : null;
    }

    private Term? Local(ILocalSymbol local, Scope scope, int depth)
    {
        if (scope.Method is not { } method) return null;
        var declaration = method.Declaration;

        foreach (var node in declaration.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator when declarator.Identifier.Text == local.Name
                    && SymbolEqualityComparer.Default.Equals(method.Model.GetDeclaredSymbol(declarator), local):
                    // Reatribuída depois: o valor depende do caminho.
                    if (IsReassigned(local, method)) return null;
                    return declarator.Initializer is { } init ? Term(init.Value, scope, depth + 1) : null;

                case ForEachStatementSyntax forEach when forEach.Identifier.Text == local.Name
                    && SymbolEqualityComparer.Default.Equals(method.Model.GetDeclaredSymbol(forEach), local):
                    return Term(forEach.Expression, scope, depth + 1) is VarTerm { Var: { Origin: VarOrigin.Input, Field.Element: { } element } }
                        ? new VarTerm(vars.Input(element))
                        : null;
            }
        }

        return null;
    }

    private static bool IsReassigned(ILocalSymbol local, AnalyzedMethod method) =>
        method.Declaration.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
            a.Left is IdentifierNameSyntax id && id.Identifier.Text == local.Name
            && SymbolEqualityComparer.Default.Equals(method.Model.GetSymbolInfo(a.Left).Symbol, local));

    /// <summary>
    /// Campo/propriedade do próprio objeto: numa entidade (programacao.Finalizar() → Status) é o membro do receiver
    /// da chamada; num service/controller (_usuario, ModelState) é um valor do contexto da requisição.
    /// </summary>
    private Term? MemberOfThis(ISymbol symbol, Scope scope, int depth)
    {
        if (scope.Method is { } method && method != graph.EntryPoint && CallSiteOf(method) is { } site && MessageOf(method) is null
            && site.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var receiver } }
            && receiver is not (ThisExpressionSyntax or BaseExpressionSyntax)
            && Term(receiver, ScopeOf(site.Caller), depth + 1) is VarTerm { Measure: Measure.Value } owner && owner.Var.Kind == VarKind.Object)
            return MemberOf(owner, symbol.Name, symbol);

        if (TypeOf(symbol) is not { } type) return null;
        var owningType = symbol.ContainingType?.Name ?? "";
        return Context($"{(symbol.IsStatic ? owningType + "." : "")}{symbol.Name}", type, null, owningType);
    }

    private VarTerm Context(string name, ITypeSymbol type, SyntaxNode? at, string? owner = null)
    {
        var root = new StateRoot
        {
            Key = $"ctx:{owner}.{name}",
            TypeName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Template = [name],
            Origin = VarOrigin.Context,
        };
        return new VarTerm(vars.Root(root, type));
    }

    // ---- Predicados ----

    public Pred Pred(ExpressionSyntax expression, Scope scope)
    {
        var e = UnwrapParens(expression);
        var model = scope.Model;

        switch (e)
        {
            case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.LogicalNotExpression):
                return Scenarios.Pred.Not(Pred(u.Operand, scope));

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression):
                return Scenarios.Pred.And(Pred(b.Left, scope), Pred(b.Right, scope));

            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression):
                return Scenarios.Pred.Or(Pred(b.Left, scope), Pred(b.Right, scope));

            case BinaryExpressionSyntax b when ComparisonOp(b) is { } op:
                return Comparison(b, op, scope) ?? Opaque(e, scope);

            case IsPatternExpressionSyntax isp:
                return Pattern(Term(isp.Expression, scope), isp.Expression, isp.Pattern, scope) ?? Opaque(e, scope);

            case MemberAccessExpressionSyntax { Name.Identifier.Text: "HasValue" } hv:
                return Term(hv.Expression, scope) is VarTerm { Measure: Measure.Value } t
                    ? Scenarios.Pred.Not(new NullPred(t.Var, $"{hv.Expression.Compact()} == null"))
                    : Opaque(e, scope);

            case MemberAccessExpressionSyntax { Name.Identifier.Text: "IsValid", Expression: var ms } when ms.ToString().EndsWith("ModelState", StringComparison.Ordinal):
                return new ModelValidPred();

            case InvocationExpressionSyntax inv when model.GetSymbolInfo(inv).Symbol is IMethodSymbol method:
                if (Call(inv, method, scope) is { } call) return call;
                break;
        }

        return Term(e, scope) switch
        {
            VarTerm { Var.Kind: VarKind.Bool, Measure: Measure.Value } t => new BoolPred(t.Var, e.Compact()),
            ConstTerm { Bool: { } value } => value ? Scenarios.Pred.True : Scenarios.Pred.False,
            _ => Opaque(e, scope),
        };
    }

    private Pred? Call(InvocationExpressionSyntax inv, IMethodSymbol method, Scope scope)
    {
        var args = inv.ArgumentList.Arguments;
        var reduced = method.ReducedFrom ?? method;
        var receiver = inv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;

        if (method.ContainingType.SpecialType == SpecialType.System_String && method.IsStatic
            && method.Name is "IsNullOrEmpty" or "IsNullOrWhiteSpace" && args.Count == 1)
            return Term(args[0].Expression, scope) is VarTerm { Measure: Measure.Value } t && t.Var.Kind == VarKind.String
                ? new BlankPred(t.Var, method.Name == "IsNullOrWhiteSpace", inv.Compact())
                : null;

        if (method.Name == "IsDefined" && method.ContainingType.Name == "Enum" && args.Count >= 1
            && Term(args[^1].Expression, scope) is VarTerm { Var.Kind: VarKind.Enum } e)
            return new EnumDefinedPred(e.Var, inv.Compact());

        if (reduced.Name == "Any" && reduced.ContainingType.Name == "Enumerable" && args.Count == 0 && receiver is not null
            && Term(receiver, scope) is VarTerm { Var.Kind: VarKind.Collection, Var.Origin: VarOrigin.Input } c)
            return new CmpPred(c with { Measure = Measure.Length }, ">", ConstTerm.Of(0), inv.Compact());

        if (method.Name == "Equals" && args.Count == 1 && receiver is not null
            && Term(receiver, scope) is { } l && Term(args[0].Expression, scope) is { } r)
            return new CmpPred(l, "==", r, inv.Compact());

        return null;
    }

    private Pred? Comparison(BinaryExpressionSyntax b, string op, Scope scope)
    {
        var left = Term(b.Left, scope);
        var right = Term(b.Right, scope);
        var code = b.Compact();

        // x == null / x != null
        if (op is "==" or "!=" && (left is ConstTerm { IsNull: true } || right is ConstTerm { IsNull: true }))
        {
            var other = left is ConstTerm { IsNull: true } ? right : left;
            var otherSyntax = left is ConstTerm { IsNull: true } ? b.Right : b.Left;
            if (other is not VarTerm { Measure: Measure.Value } v) return null;
            Pred isNull = new NullPred(v.Var, $"{otherSyntax.Compact()} == null");
            return op == "==" ? isNull : Scenarios.Pred.Not(isNull);
        }

        if (left is null || right is null) return null;

        // flag == true / flag != false
        var (flagTerm, flagConst) = (left, right) switch
        {
            (VarTerm { Var.Kind: VarKind.Bool } t, ConstTerm { Bool: { } c }) => (t, (bool?)c),
            (ConstTerm { Bool: { } c }, VarTerm { Var.Kind: VarKind.Bool } t) => (t, c),
            _ => (null, null),
        };
        if (op is "==" or "!=" && flagTerm is not null && flagConst is { } bc)
        {
            Pred flag = new BoolPred(flagTerm.Var, (left is VarTerm ? b.Left : b.Right).Compact());
            return bc == (op == "==") ? flag : Scenarios.Pred.Not(flag);
        }

        return new CmpPred(left, op, right, code);
    }

    /// <summary>x is null, x is not null, x is A or B, x is &gt; 5, x is { Status: Ativo }, x is Tipo t.</summary>
    public Pred? Pattern(Term? subject, ExpressionSyntax subjectSyntax, PatternSyntax pattern, Scope scope)
    {
        var text = $"{subjectSyntax.Compact()} is {pattern.Compact()}";
        switch (pattern)
        {
            case DiscardPatternSyntax or VarPatternSyntax:
                return Scenarios.Pred.True;
            case ConstantPatternSyntax c when Term(c.Expression, scope) is ConstTerm k:
                if (subject is not VarTerm { Measure: Measure.Value } v) return null;
                if (k.IsNull) return new NullPred(v.Var, $"{subjectSyntax.Compact()} == null");
                if (k.Bool is { } b && v.Var.Kind == VarKind.Bool)
                    return b ? new BoolPred(v.Var, subjectSyntax.Compact()) : Scenarios.Pred.Not(new BoolPred(v.Var, subjectSyntax.Compact()));
                return new CmpPred(subject, "==", k, text);
            case RelationalPatternSyntax r when subject is not null && Term(r.Expression, scope) is ConstTerm k2:
                return new CmpPred(subject, r.OperatorToken.Text, k2, text);
            case UnaryPatternSyntax u when u.IsKind(SyntaxKind.NotPattern):
                return Pattern(subject, subjectSyntax, u.Pattern, scope) is { } inner ? Scenarios.Pred.Not(inner) : null;
            case BinaryPatternSyntax bp:
            {
                var l = Pattern(subject, subjectSyntax, bp.Left, scope);
                var r2 = Pattern(subject, subjectSyntax, bp.Right, scope);
                if (l is null || r2 is null) return null;
                return bp.IsKind(SyntaxKind.OrPattern) ? Scenarios.Pred.Or(l, r2) : Scenarios.Pred.And(l, r2);
            }
            case ParenthesizedPatternSyntax pp:
                return Pattern(subject, subjectSyntax, pp.Pattern, scope);
            case TypePatternSyntax or DeclarationPatternSyntax or RecursivePatternSyntax:
            {
                if (subject is not VarTerm { Measure: Measure.Value } typed) return null;
                var parts = new List<Pred> { Scenarios.Pred.Not(new NullPred(typed.Var, $"{subjectSyntax.Compact()} == null")) };
                var declared = pattern switch
                {
                    TypePatternSyntax t => scope.Model.GetTypeInfo(t.Type).Type,
                    DeclarationPatternSyntax d => scope.Model.GetTypeInfo(d.Type).Type,
                    RecursivePatternSyntax { Type: { } rt } => scope.Model.GetTypeInfo(rt).Type,
                    _ => null,
                };
                var staticType = subjectSyntax.SyntaxTree == scope.Model.SyntaxTree ? scope.Model.GetTypeInfo(subjectSyntax).Type : null;
                // Teste de tipo diferente do tipo estático: não dá para saber pelo payload.
                if (declared is not null && staticType is not null
                    && !SymbolEqualityComparer.Default.Equals(InputModel.Underlying(declared).Type, InputModel.Underlying(staticType).Type))
                    return null;
                if (pattern is RecursivePatternSyntax { PropertyPatternClause: { } props })
                    foreach (var sub in props.Subpatterns)
                    {
                        if (sub.NameColon?.Name.Identifier.Text is not { } name) return null;
                        var symbol = scope.Model.GetSymbolInfo(sub.NameColon.Name).Symbol;
                        var member = MemberOf(typed, name, symbol);
                        if (Pattern(member, SyntaxFactory.ParseExpression($"{subjectSyntax}.{name}"), sub.Pattern, scope) is not { } subPred) return null;
                        parts.Add(subPred);
                    }
                return Scenarios.Pred.And(parts);
            }
        }
        return null;
    }

    private int _opaque;
    private readonly Dictionary<string, string> _opaqueIds = [];

    public Pred Opaque(SyntaxNode node, Scope scope)
    {
        var text = node.Compact();
        var id = $"{scope.Method?.DisplayName}:{text}";
        if (!_opaqueIds.TryGetValue(id, out var shortId)) _opaqueIds[id] = shortId = $"O{++_opaque}";
        return new OpaquePred(shortId, text);
    }

    public OpaquePred Opaque(string text, string scopeName)
    {
        var id = $"{scopeName}:{text}";
        if (!_opaqueIds.TryGetValue(id, out var shortId)) _opaqueIds[id] = shortId = $"O{++_opaque}";
        return new OpaquePred(shortId, text);
    }

    // ---- Caminho (condições para um trecho executar) ----

    /// <summary>Condição para <paramref name="node"/> executar: estruturas que o envolvem no método + caminho desde o endpoint.</summary>
    public Pred PathOf(SyntaxNode node, AnalyzedMethod method) => Scenarios.Pred.And(Local(node, method), CallerPath(method));

    private Pred CallerPath(AnalyzedMethod method)
    {
        if (_callerPaths.TryGetValue(method, out var cached)) return cached;
        _callerPaths[method] = Scenarios.Pred.True; // ciclos
        var path = CallSiteOf(method) is { } site ? PathOf(site.Syntax, site.Caller) : Scenarios.Pred.True;
        return _callerPaths[method] = path;
    }

    private Pred Local(SyntaxNode node, AnalyzedMethod method)
    {
        var scope = ScopeOf(method);
        var parts = new List<Pred>();
        var declaration = method.Declaration;

        for (SyntaxNode child = node, current = node.Parent!; current is not null && child != declaration; child = current, current = current.Parent!)
        {
            switch (current)
            {
                case IfStatementSyntax ifs when child == ifs.Statement:
                    parts.Add(Pred(ifs.Condition, scope));
                    break;
                case IfStatementSyntax ifs when child == ifs.Else:
                    parts.Add(Scenarios.Pred.Not(Pred(ifs.Condition, scope)));
                    break;
                case ElseClauseSyntax:
                    break;
                case BlockSyntax block when child is StatementSyntax statement:
                    var index = block.Statements.IndexOf(statement);
                    for (var i = 0; i < index; i++)
                    {
                        // Guardas anteriores (if (x) throw/return;) e seções de switch que saem do método.
                        if (block.Statements[i] is IfStatementSyntax { Else: null } guard && SyntaxConditions.AlwaysExits(guard.Statement)
                            && ExitsMethod(guard.Statement))
                            parts.Add(Scenarios.Pred.Not(Pred(guard.Condition, scope)));
                        else if (block.Statements[i] is SwitchStatementSyntax sw)
                            foreach (var section in sw.Sections.Where(s => s.Statements.LastOrDefault() is { } last && ExitsMethod(last)))
                                parts.Add(Scenarios.Pred.Not(Section(sw, section, scope)));
                    }
                    break;
                case SwitchSectionSyntax section when current.Parent is SwitchStatementSyntax sw2:
                    parts.Add(Section(sw2, section, scope));
                    break;
                case SwitchExpressionArmSyntax arm when current.Parent is SwitchExpressionSyntax se && child != arm.Pattern:
                    parts.Add(Arm(se, arm, scope));
                    break;
                case ConditionalExpressionSyntax c when child == c.WhenTrue:
                    parts.Add(Pred(c.Condition, scope));
                    break;
                case ConditionalExpressionSyntax c when child == c.WhenFalse:
                    parts.Add(Scenarios.Pred.Not(Pred(c.Condition, scope)));
                    break;
                case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalAndExpression):
                    parts.Add(Pred(b.Left, scope));
                    break;
                case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.LogicalOrExpression):
                    parts.Add(Scenarios.Pred.Not(Pred(b.Left, scope)));
                    break;
                case BinaryExpressionSyntax b when child == b.Right && b.IsKind(SyntaxKind.CoalesceExpression):
                    parts.Add(Term(b.Left, scope) is VarTerm { Measure: Measure.Value } l ? new NullPred(l.Var, $"{b.Left.Compact()} == null") : Opaque($"{b.Left.Compact()} == null", method.DisplayName));
                    break;
                case ConditionalAccessExpressionSyntax ca when child == ca.WhenNotNull:
                    parts.Add(Term(ca.Expression, scope) is VarTerm { Measure: Measure.Value } t
                        ? Scenarios.Pred.Not(new NullPred(t.Var, $"{ca.Expression.Compact()} == null"))
                        : Scenarios.Pred.Not(Opaque($"{ca.Expression.Compact()} == null", method.DisplayName)));
                    break;
                case CatchClauseSyntax cc:
                    parts.Add(Opaque($"exceção capturada: {cc.Declaration?.Type.Compact() ?? "Exception"}", $"{method.DisplayName}@{cc.SpanStart}"));
                    break;
                case WhileStatementSyntax w when child == w.Statement:
                    parts.Add(Pred(w.Condition, scope));
                    break;
                case ForStatementSyntax f when child == f.Statement && f.Condition is not null:
                    parts.Add(Pred(f.Condition, scope));
                    break;
                case ForEachStatementSyntax fe when child == fe.Statement:
                    parts.Add(Term(fe.Expression, scope) is VarTerm { Var.Kind: VarKind.Collection } items
                        ? new CmpPred(items with { Measure = Measure.Length }, ">", ConstTerm.Of(0), $"{fe.Expression.Compact()}.Count > 0")
                        : Opaque($"para cada {fe.Identifier.Text} em {fe.Expression.Compact()}", method.DisplayName));
                    break;
                case LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax:
                    return Scenarios.Pred.And(parts);
            }
        }

        return Scenarios.Pred.And(parts);
    }

    /// <summary>Sai do método (throw/return), e não só do laço (continue/break).</summary>
    public static bool ExitsMethod(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax or ReturnStatementSyntax => true,
        BlockSyntax block => block.Statements.LastOrDefault() is { } last && ExitsMethod(last),
        _ => false,
    };

    public Pred Section(SwitchStatementSyntax sw, SwitchSectionSyntax section, Scope scope)
    {
        var subject = Term(sw.Expression, scope);
        Pred? Label(SwitchLabelSyntax label) => label switch
        {
            CaseSwitchLabelSyntax c => Term(c.Value, scope) is ConstTerm k && subject is not null
                ? k.IsNull && subject is VarTerm v ? new NullPred(v.Var, $"{sw.Expression.Compact()} == null") : new CmpPred(subject, "==", k, $"{sw.Expression.Compact()} == {c.Value.Compact()}")
                : null,
            CasePatternSwitchLabelSyntax p => Pattern(subject, sw.Expression, p.Pattern, scope) is { } pattern
                ? p.WhenClause is null ? pattern : Scenarios.Pred.And(pattern, Pred(p.WhenClause.Condition, scope))
                : null,
            _ => null,
        };

        if (section.Labels.Any(l => l is DefaultSwitchLabelSyntax))
        {
            var others = sw.Sections.Where(s => s != section).SelectMany(s => s.Labels).Select(Label).ToList();
            return others.Any(o => o is null)
                ? Opaque($"{sw.Expression.Compact()} (demais casos)", $"{scope.Method?.DisplayName}@{sw.SpanStart}")
                : Scenarios.Pred.And(others.Select(o => Scenarios.Pred.Not(o!)));
        }

        var labels = section.Labels.Select(Label).ToList();
        return labels.Any(l => l is null)
            ? Opaque(SyntaxConditions.SectionCondition(sw.Expression, section), scope.Method?.DisplayName ?? "")
            : Scenarios.Pred.Or(labels!);
    }

    public Pred Arm(SwitchExpressionSyntax se, SwitchExpressionArmSyntax arm, Scope scope)
    {
        var subject = Term(se.GoverningExpression, scope);
        Pred? ArmPred(SwitchExpressionArmSyntax a) => Pattern(subject, se.GoverningExpression, a.Pattern, scope) is { } p
            ? a.WhenClause is null ? p : Scenarios.Pred.And(p, Pred(a.WhenClause.Condition, scope))
            : null;

        var previous = se.Arms.TakeWhile(a => a != arm).Select(ArmPred).ToList();
        var own = ArmPred(arm);
        if (own is null || previous.Any(p => p is null))
            return Opaque($"{se.GoverningExpression.Compact()} is {arm.Pattern.Compact()}", $"{scope.Method?.DisplayName}@{arm.SpanStart}");
        return Scenarios.Pred.And(previous.Select(p => Scenarios.Pred.Not(p!)).Append(own));
    }

    // ---- Auxiliares ----

    /// <summary>Chamador e sintaxe da chamada que levou a este método no grafo.</summary>
    public (SyntaxNode Syntax, AnalyzedMethod Caller)? CallSiteOf(AnalyzedMethod method) =>
        method.Caller?.CallSites.FirstOrDefault(cs => cs.Node == method.Node) is { } site ? (site.Syntax, method.Caller) : null;

    /// <summary>Mensagem recebida quando o método é o handler despachado pela chamada (mediator.Send(command)).</summary>
    private static DispatchedMessage? MessageOf(AnalyzedMethod method) =>
        method.Caller?.CallSites.FirstOrDefault(cs => cs.Node == method.Node)?.Message;

    private static ExpressionSyntax? Argument(SyntaxNode call, IMethodSymbol callee, IParameterSymbol parameter)
    {
        var arguments = call switch
        {
            InvocationExpressionSyntax invocation => invocation.ArgumentList.Arguments,
            BaseObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments ?? default,
            ConstructorInitializerSyntax initializer => initializer.ArgumentList.Arguments,
            _ => default,
        };

        var ordinal = parameter.Ordinal;
        // Extensão chamada como x.Metodo(a): o parâmetro "this" é o receiver.
        if (callee.IsExtensionMethod && call is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma })
        {
            if (ordinal == 0) return ma.Expression;
            ordinal--;
        }

        var named = arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == parameter.Name);
        if (named is not null) return named.Expression;
        return ordinal < arguments.Count && arguments[ordinal].NameColon is null ? arguments[ordinal].Expression : null;
    }

    /// <summary>Consulta ao estado: repositório, DbContext ou código sem fonte (EF, bibliotecas).</summary>
    public static bool IsBoundary(IMethodSymbol method) => !method.HasSource() || IsRepository(method);

    /// <summary>Repositório, DbContext/DbSet ou extensão do EF Core: a fonte do estado consultado.</summary>
    public static bool IsRepository(IMethodSymbol method)
    {
        static bool Looks(string name) => name.EndsWith("Repository", StringComparison.Ordinal) || name.EndsWith("Repositorio", StringComparison.Ordinal)
            || name.EndsWith("Context", StringComparison.Ordinal) || name.EndsWith("Dao", StringComparison.Ordinal)
            || name.EndsWith("Store", StringComparison.Ordinal) || name.EndsWith("Queries", StringComparison.Ordinal) || name.EndsWith("Query", StringComparison.Ordinal);
        var type = (method.ReducedFrom ?? method).ContainingType;
        return Looks(type.Name) || type.AllInterfaces.Any(i => Looks(i.Name)) || type.Name.StartsWith("DbSet", StringComparison.Ordinal)
            || type.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true;
    }

    private static ExpressionSyntax? SingleReturn(SyntaxNode declaration)
    {
        if (declaration is BaseMethodDeclarationSyntax { ExpressionBody: { } arrow }) return arrow.Expression;
        if (declaration is LocalFunctionStatementSyntax { ExpressionBody: { } localArrow }) return localArrow.Expression;
        var returns = declaration.DescendantNodes()
            .Where(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) || n == declaration)
            .OfType<ReturnStatementSyntax>()
            .Where(r => !r.Ancestors().TakeWhile(a => a != declaration).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .ToList();
        return returns.Count == 1 ? returns[0].Expression : null;
    }

    public static ExpressionSyntax Unwrap(ExpressionSyntax e)
    {
        while (true)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax p: e = p.Expression; continue;
                case AwaitExpressionSyntax a: e = a.Expression; continue;
                case PostfixUnaryExpressionSyntax s when s.IsKind(SyntaxKind.SuppressNullableWarningExpression): e = s.Operand; continue;
                case CastExpressionSyntax c: e = c.Expression; continue;
                case CheckedExpressionSyntax ch: e = ch.Expression; continue;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait", Expression: var inner } }: e = inner; continue;
                default: return e;
            }
        }
    }

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        return e;
    }

    private static ConstTerm? Constant(ExpressionSyntax e, SemanticModel model)
    {
        var constant = model.GetConstantValue(e);
        if (!constant.HasValue) return null;
        var symbol = model.GetSymbolInfo(e).Symbol;
        var display = symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } member ? $"{member.ContainingType.Name}.{member.Name}" : e.Compact();
        return ConstantValue(constant.Value, display);
    }

    private static ConstTerm ConstantValue(object? value, string display) => value switch
    {
        null => ConstTerm.Null,
        bool b => ConstTerm.Of(b),
        string s => ConstTerm.Of(s, display),
        char c => ConstTerm.Of(c.ToString(), display),
        _ => ConstTerm.Of(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture), display),
    };

    private static ConstTerm? DefaultOf(ITypeSymbol? type)
    {
        if (type is null) return null;
        if (IsNullable(type) || !type.IsValueType) return ConstTerm.Null;
        return InputModel.KindOf(type) switch
        {
            VarKind.Date => ConstTerm.Of(Values.MinDateDays, "default"),
            VarKind.Int or VarKind.Decimal or VarKind.Enum => ConstTerm.Of(0, "default"),
            VarKind.Bool => ConstTerm.Of(false),
            VarKind.Guid => ConstTerm.Of(Values.EmptyGuid, "Guid.Empty"),
            _ => null,
        };
    }

    private static bool IsNullable(ITypeSymbol? type) => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static ITypeSymbol? TypeOf(ISymbol? symbol) => symbol switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        ILocalSymbol l => l.Type,
        IParameterSymbol p => p.Type,
        IMethodSymbol m => m.ReturnType,
        _ => null,
    };

    public static ITypeSymbol UnwrapTask(ITypeSymbol type) =>
        type is INamedTypeSymbol { Name: "Task" or "ValueTask", TypeArguments.Length: 1 } task ? task.TypeArguments[0] : type;

    private static string? ArithmeticOp(BinaryExpressionSyntax b) => b.Kind() switch
    {
        SyntaxKind.AddExpression => "+",
        SyntaxKind.SubtractExpression => "-",
        SyntaxKind.MultiplyExpression => "*",
        SyntaxKind.DivideExpression => "/",
        SyntaxKind.ModuloExpression => "%",
        _ => null,
    };

    private static string? ComparisonOp(BinaryExpressionSyntax b) => b.Kind() switch
    {
        SyntaxKind.EqualsExpression => "==",
        SyntaxKind.NotEqualsExpression => "!=",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        _ => null,
    };
}
