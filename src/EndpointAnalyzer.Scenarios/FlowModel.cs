using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scenarios;

/// <summary>Ponto do código com a condição para executar e a ordem de execução (posição em cada nível da cadeia de chamadas).</summary>
public sealed class Site
{
    public required AnalyzedMethod Method { get; init; }

    public required SyntaxNode Node { get; init; }

    public required IReadOnlyList<int> Order { get; init; }

    public required Pred Path { get; init; }

    /// <summary>Estrutura que declara a condição (if, catch, switch): a linha das regras do relatório.</summary>
    public SyntaxNode? Anchor { get; init; }

    public SourceReference Source => new() { File = Method.SourceFile, Method = Method.DisplayName, Line = Node.StartLine() };

    public int DeclaredLine => (Anchor ?? Node).StartLine();
}

/// <summary>Regra que interrompe a operação com erro: throw, return de erro no controller, notificação de domínio, guarda.</summary>
public sealed class CodeRule
{
    public required string Id { get; init; }

    public required Site Site { get; init; }

    public required Pred Trigger { get; init; }

    /// <summary>Gatilho como no código: "request.Data &lt; DateTime.Today".</summary>
    public required string TriggerCode { get; init; }

    /// <summary>throw, return, notify ou guard.</summary>
    public required string Kind { get; init; }

    public string? Exception { get; init; }

    public string? Message { get; init; }

    /// <summary>Mensagem interpolada ($"Programação {id} não encontrada."): os trechos são preenchidos com os valores do cenário.</summary>
    public InterpolatedStringExpressionSyntax? MessageSyntax { get; init; }

    public int? Status { get; set; }

    public required string StatusSource { get; set; }

    public string? StatusNote { get; set; }

    /// <summary>if (!ModelState.IsValid) return ...: coberta pelos cenários de validação.</summary>
    public bool IsModelStateCheck { get; init; }
}

/// <summary>Saída antecipada sem erro (return no meio do método): o resto do método não executa.</summary>
public sealed class EarlyExit
{
    public required Site Site { get; init; }

    public required Pred Trigger { get; init; }

    public required string TriggerCode { get; init; }

    /// <summary>Return no próprio endpoint (encerra a requisição) com o status devolvido.</summary>
    public bool InEntry { get; init; }

    public int? Status { get; init; }
}

/// <summary>
/// if (!Existe(...)) return ...: o método chamado na condição já notifica o erro numa regra própria; o return não
/// é regra nem saída antecipada (a regra do método chamado encerra o cenário).
/// </summary>
public sealed class DelegatedCheck
{
    public required Site Site { get; init; }

    public required string TriggerCode { get; init; }

    public required string Callee { get; init; }
}

/// <summary>Onde acontece um efeito: alteração de entidade, campo alterado, SaveChanges, evento, HTTP.</summary>
public sealed class EffectSite
{
    public required Site Site { get; init; }

    public required string Kind { get; init; }

    public required string Target { get; init; }

    public required string Description { get; init; }

    public EntityChange? Change { get; init; }

    public PropertyChange? Property { get; init; }
}

/// <summary>
/// Modelo do fluxo para gerar e simular cenários: regras na ordem de execução, saídas antecipadas, efeitos e os
/// status HTTP (return do controller, try/catch na cadeia de chamadas, middleware de exceções ou heurística pelo nome).
/// </summary>
public sealed class FlowModel
{
    public List<CodeRule> Rules { get; } = [];

    public List<EarlyExit> Exits { get; } = [];

    public List<EffectSite> Effects { get; } = [];

    public List<DelegatedCheck> Delegated { get; } = [];

    /// <summary>Status do controller quando há notificação de erro (ApiResponse: Ok sem notificação, BadRequest com).</summary>
    public int? NotificationStatus { get; private set; }

    public int SuccessStatus { get; private set; } = 200;

    public string SuccessStatusSource { get; private set; } = "inferido";

    /// <summary>[ApiController]: ModelState inválido devolve 400 antes da action.</summary>
    public bool ApiController { get; private set; }

    public List<string> Notes { get; } = [];

    private readonly CallGraph _graph;
    private readonly SymbolicResolver _resolver;
    private readonly ExceptionStatusMap _exceptions;
    private readonly HashSet<SyntaxNode> _exitStatements = [];

    private FlowModel(CallGraph graph, SymbolicResolver resolver, ExceptionStatusMap exceptions)
    {
        _graph = graph;
        _resolver = resolver;
        _exceptions = exceptions;
    }

    public static FlowModel Build(CallGraph graph, EndpointAnalysisContext context, SymbolicResolver resolver, ExceptionStatusMap exceptions)
    {
        var flow = new FlowModel(graph, resolver, exceptions);
        var business = context.CallGraph.ToHashSet();
        var methods = graph.Methods.Where(m => m == graph.EntryPoint || business.Contains(m.DisplayName)).ToList();
        foreach (var method in methods) flow._analyzed.Add(method.Symbol.OriginalDefinition);
        // Antes dos caminhos: if (!Existe(...)) return ... não é condição própria quando Existe já notifica numa regra.
        foreach (var method in methods) flow.MarkDelegatedChecks(method);

        foreach (var method in methods) flow.CollectRules(method);
        flow.Rules.Sort((a, b) => Compare(a.Site.Order, b.Site.Order));
        flow.Exits.Sort((a, b) => Compare(a.Site.Order, b.Site.Order));

        flow.CollectSuccessStatus();
        flow.ApplyNotificationStatus();
        flow.CollectEffects(context);
        flow.ApiController = HasApiController(graph.EntryPoint.Symbol.ContainingType);
        return flow;
    }

    // ---- Ordem ----

    public IReadOnlyList<int> OrderOf(SyntaxNode node, AnalyzedMethod method) => OrderOf(node.SpanStart, method);

    private IReadOnlyList<int> OrderOf(int position, AnalyzedMethod method)
    {
        var order = new List<int> { position };
        var current = method;
        for (var depth = 0; depth < 64 && _resolver.CallSiteOf(current) is { } site; depth++)
        {
            order.Insert(0, site.Syntax.SpanStart);
            current = site.Caller;
        }
        return order;
    }

    public static int Compare(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>A saída antecipada impede <paramref name="later"/>: mesmo método (ou chamada feita por ele) e depois dela.</summary>
    public static bool Blocks(EarlyExit exit, IReadOnlyList<int> later)
    {
        var exitOrder = exit.Site.Order;
        if (exit.InEntry) return Compare(later, exitOrder) > 0;
        var prefix = exitOrder.Count - 1;
        if (later.Count < prefix) return false;
        for (var i = 0; i < prefix; i++)
            if (later[i] != exitOrder[i]) return false;
        return Compare(later, exitOrder) > 0;
    }

    /// <param name="orderAt">Posição de execução: o fim do return (as chamadas dentro dele executam antes de sair).</param>
    private Site SiteOf(SyntaxNode node, AnalyzedMethod method, SyntaxNode? pathFrom = null, int? orderAt = null) => new()
    {
        Method = method,
        Node = node,
        Order = orderAt is { } position ? OrderOf(position, method) : OrderOf(node, method),
        Path = _resolver.PathOf(pathFrom ?? node, method),
        Anchor = pathFrom,
    };

    // ---- Regras ----

    private sealed record Exit(string Kind, SyntaxNode Statement, string? Exception, ITypeSymbol? ExceptionType, string? Message,
        InterpolatedStringExpressionSyntax? MessageSyntax, HashSet<int> Statuses);

    private void CollectRules(AnalyzedMethod method)
    {
        var scope = _resolver.ScopeOf(method);
        var isEntry = method == _graph.EntryPoint;

        foreach (var node in method.Declaration.DescendantNodes())
        {
            if (!method.IsReachable(node) || InsideLambda(node, method.Declaration)) continue;

            switch (node)
            {
                case IfStatementSyntax ifs:
                    Branch(method, ifs, ifs.Statement, _resolver.Pred(ifs.Condition, scope), ifs.Condition.Compact(), isEntry);
                    if (ifs.Else is { Statement: not IfStatementSyntax } elseClause)
                        Branch(method, ifs, elseClause.Statement, Pred.Not(_resolver.Pred(ifs.Condition, scope)), SyntaxConditions.Negate(ifs.Condition), isEntry);
                    break;

                case SwitchStatementSyntax sw:
                    foreach (var section in sw.Sections)
                        if (ExitOf(section.Statements, method) is { } exit)
                            Add(method, section, sw, _resolver.Section(sw, section, scope), SyntaxConditions.SectionCondition(sw.Expression, section), exit, isEntry);
                    break;

                // valor ?? throw new X("...")
                case ThrowExpressionSyntax te when te.Parent is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression):
                {
                    var trigger = _resolver.Term(b.Left, scope) is VarTerm { Measure: Measure.Value } t
                        ? (Pred)new NullPred(t.Var, $"{b.Left.Compact()} == null")
                        : _resolver.Opaque($"{b.Left.Compact()} == null", method.DisplayName);
                    AddThrow(method, te, b, trigger, $"{b.Left.Compact()} == null", te.Expression, "throw");
                    break;
                }

                // cond ? valor : throw new X("...")
                case ThrowExpressionSyntax te when te.Parent is ConditionalExpressionSyntax c:
                {
                    var condition = _resolver.Pred(c.Condition, scope);
                    var whenFalse = te == c.WhenFalse;
                    AddThrow(method, te, c, whenFalse ? Pred.Not(condition) : condition,
                        whenFalse ? SyntaxConditions.Negate(c.Condition) : c.Condition.Compact(), te.Expression, "throw");
                    break;
                }

                case ThrowExpressionSyntax te when te.Parent is SwitchExpressionArmSyntax arm && arm.Parent is SwitchExpressionSyntax se:
                    AddThrow(method, te, se, _resolver.Arm(se, arm, scope), $"{se.GoverningExpression.Compact()} is {arm.Pattern.Compact()}", te.Expression, "throw");
                    break;

                // throw fora de if/switch (e fora de catch): sempre lança quando o trecho executa.
                case ThrowStatementSyntax { Expression: { } thrown } ts when !_exitStatements.Contains(ts) && !IsBranchExit(ts)
                    && !ts.Ancestors().TakeWhile(a => a != method.Declaration).Any(a => a is CatchClauseSyntax):
                    AddThrow(method, ts, ts, Pred.True, "sempre", thrown, "throw");
                    break;

                case InvocationExpressionSyntax inv when Guard(inv, scope) is { } guard:
                    AddGuard(method, inv, guard.Trigger, guard.Code, guard.Exception);
                    break;

                // catch (Exception e) { Bus.PublicarEvento(new DomainNotification("Commit", e.Message)); }: falha de infraestrutura notificada.
                case CatchClauseSyntax cc when !isEntry && ExitOf(cc.Block.Statements, method) is { Kind: "notify" } caught:
                {
                    var type = cc.Declaration?.Type.Compact() ?? "Exception";
                    var trigger = _resolver.Opaque($"exceção capturada: {type}", $"{method.DisplayName}@{cc.SpanStart}");
                    Add(method, cc.Block, cc, trigger, $"exceção capturada em {method.DisplayName}: {type}", caught, isEntry);
                    break;
                }
            }
        }
    }

    private void MarkDelegatedChecks(AnalyzedMethod method)
    {
        foreach (var ifs in method.Declaration.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (!method.IsReachable(ifs) || InsideLambda(ifs, method.Declaration)) continue;
            var statements = ifs.Statement is BlockSyntax block ? (IEnumerable<StatementSyntax>)block.Statements : [ifs.Statement];
            if (ExitOf(statements, method) is not { Kind: "return" } exit) continue;
            var isEntry = method == _graph.EntryPoint;
            var error = exit.Statuses.Any(s => s >= 400) && !exit.Statuses.Any(s => s < 400);
            if ((!isEntry || !error) && ConditionNotifier(ifs.Condition, method) is { Delegated: true, Call: { } call })
                _resolver.AssumedTrue.Add(call);
        }
    }

    private void Branch(AnalyzedMethod method, IfStatementSyntax ifs, StatementSyntax body, Pred trigger, string code, bool isEntry)
    {
        var statements = body is BlockSyntax block ? (IEnumerable<StatementSyntax>)block.Statements : [body];
        if (ExitOf(statements, method) is not { } exit) return;

        // if (!ValidarAlgo()) return;: o método da condição registra o erro (notificação ou falha de validação).
        var error = exit.Statuses.Any(s => s >= 400) && !exit.Statuses.Any(s => s < 400);
        if (exit.Kind == "return" && (!isEntry || !error) && ConditionNotifier(ifs.Condition, method) is { } recorder)
        {
            if (recorder.Delegated)
            {
                _exitStatements.Add(exit.Statement);
                Delegated.Add(new DelegatedCheck { Site = SiteOf(body, method, ifs), TriggerCode = code, Callee = recorder.Callee });
                return;
            }
            exit = exit with { Kind = "notify", Message = recorder.Message, MessageSyntax = null };
        }
        Add(method, body, ifs, trigger, code, exit, isEntry);
    }

    private void Add(AnalyzedMethod method, SyntaxNode at, SyntaxNode pathFrom, Pred trigger, string code, Exit exit, bool isEntry)
    {
        _exitStatements.Add(exit.Statement);
        // return: as chamadas dentro dele executam antes de sair; catch: a exceção acontece durante o try.
        int? orderAt = exit.Statement is ReturnStatementSyntax returned ? returned.Span.End - 1
            : pathFrom is CatchClauseSyntax { Parent: TryStatementSyntax tried } ? tried.SpanStart : null;
        var site = SiteOf(at, method, pathFrom, orderAt);

        if (exit.Kind == "return")
        {
            var error = exit.Statuses.FirstOrDefault(s => s >= 400);
            var ok = exit.Statuses.FirstOrDefault(s => s < 400);
            // return ApiResponse(...): Ok sem notificação, BadRequest com notificação; os erros são as regras que notificam.
            if (isEntry && error > 0 && ok > 0) NotificationStatus ??= error;
            // return no meio de um método interno, ou return de sucesso no controller: saída antecipada, não erro.
            if (!isEntry || error == 0 || ok > 0)
            {
                Exits.Add(new EarlyExit { Site = site, Trigger = trigger, TriggerCode = code, InEntry = isEntry, Status = isEntry && ok > 0 ? ok : null });
                return;
            }
            Rules.Add(new CodeRule
            {
                Id = $"R{Rules.Count + 1}",
                Site = site,
                Trigger = trigger,
                TriggerCode = code,
                Kind = "return",
                Message = exit.Message,
                MessageSyntax = exit.MessageSyntax,
                Status = error,
                StatusSource = "código",
                IsModelStateCheck = Evaluator.Atoms(trigger).Any(a => a is ModelValidPred),
            });
            return;
        }

        if (exit.Kind == "notify")
        {
            var status = exit.Statuses.FirstOrDefault(s => s >= 400);
            Rules.Add(new CodeRule
            {
                Id = $"R{Rules.Count + 1}",
                Site = site,
                Trigger = trigger,
                TriggerCode = code,
                Kind = "notify",
                Message = exit.Message,
                MessageSyntax = exit.MessageSyntax,
                Status = status > 0 ? status : 400,
                StatusSource = status > 0 ? "código" : "inferido",
                StatusNote = status > 0 ? null : "notificação de domínio: o status depende de como o controller devolve as notificações",
                IsModelStateCheck = Evaluator.Atoms(trigger).Any(a => a is ModelValidPred),
            });
            return;
        }

        var (st, source, note) = ExceptionStatus(exit.ExceptionType, at, method);
        Rules.Add(new CodeRule
        {
            Id = $"R{Rules.Count + 1}",
            Site = site,
            Trigger = trigger,
            TriggerCode = code,
            Kind = "throw",
            Exception = exit.Exception,
            Message = exit.Message,
            MessageSyntax = exit.MessageSyntax,
            Status = st,
            StatusSource = source,
            StatusNote = note,
        });
    }

    private void AddThrow(AnalyzedMethod method, SyntaxNode at, SyntaxNode pathFrom, Pred trigger, string code, ExpressionSyntax thrown, string kind)
    {
        var type = method.ResolveType(method.Model.GetTypeInfo(thrown).Type);
        var (message, syntax) = MessageOf(thrown, method.Model);
        Add(method, at, pathFrom, trigger, code, new Exit(kind, at, type?.Name, type, message, syntax, []), method == _graph.EntryPoint);
    }

    private void AddGuard(AnalyzedMethod method, InvocationExpressionSyntax inv, Pred trigger, string code, string exception)
    {
        var (status, source, note) = ExceptionStatus(null, inv, method, exception);
        Rules.Add(new CodeRule
        {
            Id = $"R{Rules.Count + 1}",
            Site = SiteOf(inv, method),
            Trigger = trigger,
            TriggerCode = code,
            Kind = "guard",
            Exception = exception,
            Message = MessageOf(inv, method.Model).Message,
            Status = status,
            StatusSource = source,
            StatusNote = note,
        });
    }

    /// <summary>throw do bloco do if/switch já tratado como regra pela estrutura.</summary>
    private static bool IsBranchExit(ThrowStatementSyntax ts) => ts.Parent switch
    {
        SwitchSectionSyntax => true,
        IfStatementSyntax or ElseClauseSyntax => true,
        BlockSyntax { Parent: IfStatementSyntax or ElseClauseSyntax or SwitchSectionSyntax } => true,
        _ => false,
    };

    private static bool InsideLambda(SyntaxNode node, SyntaxNode declaration) =>
        node.Ancestors().TakeWhile(a => a != declaration).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    /// <summary>O que o bloco faz ao sair: lança exceção, notifica erro ou retorna (com o status HTTP no controller).</summary>
    private Exit? ExitOf(IEnumerable<StatementSyntax> statements, AnalyzedMethod method)
    {
        var list = statements.ToList();
        if (list.Count == 1 && list[0] is BlockSyntax inner) list = [.. inner.Statements];

        foreach (var statement in list)
        {
            switch (statement)
            {
                case ThrowStatementSyntax { Expression: { } thrown }:
                {
                    var type = method.ResolveType(method.Model.GetTypeInfo(thrown).Type);
                    var (message, syntax) = MessageOf(thrown, method.Model);
                    return new Exit("throw", statement, type?.Name, type, message, syntax, []);
                }
                case ExpressionStatementSyntax es when Unwrap(es.Expression) is InvocationExpressionSyntax inv && NotificationMessage(inv, method.Model) is { } notified:
                {
                    var ret = list.OfType<ReturnStatementSyntax>().FirstOrDefault();
                    var statuses = ret?.Expression is { } e && method == _graph.EntryPoint ? StatusOf(e, method.Model, 0) : [];
                    return new Exit("notify", statement, null, null, notified.Message, notified.Syntax, statuses);
                }
                case ReturnStatementSyntax ret:
                {
                    var statuses = ret.Expression is { } e && method == _graph.EntryPoint ? StatusOf(e, method.Model, 0) : [];
                    // return await Falha("chave", "mensagem"): o método chamado sempre notifica o erro.
                    if (method != _graph.EntryPoint && ret.Expression is { } failed
                        && failed.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                            .FirstOrDefault(i => NotifierOf(method.Model.GetSymbolInfo(i).Symbol as IMethodSymbol) is { Conditional: false }) is { } failure)
                        return new Exit("notify", statement, null, null, NotifierMessage(failure, method.Model, null), null, statuses);
                    var (message, syntax) = ret.Expression is { } value ? MessageOf(value, method.Model) : (null, null);
                    return new Exit("return", statement, null, null, message, syntax, statuses);
                }
            }
        }
        return null;
    }

    // ---- Notificações de erro ----

    /// <summary>Tipos que registram erro sem exceção: notificação de domínio, falha de validação da entidade.</summary>
    private static readonly string[] ErrorTypeNames = ["DomainNotification", "Notification", "Notificacao", "ValidationFailure", "Error", "Erro"];

    private sealed record Notifier(bool Conditional);

    private sealed record Recorder(string Callee, bool Delegated, string? Message, InvocationExpressionSyntax Call);

    private sealed record Notified(string? Message, InterpolatedStringExpressionSyntax? Syntax);

    private readonly HashSet<ISymbol> _analyzed = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<ISymbol, Notifier?> _notifiers = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<SyntaxTree, SemanticModel?> _models = [];

    private SemanticModel? ModelOf(SyntaxNode node)
    {
        if (_models.TryGetValue(node.SyntaxTree, out var cached)) return cached;
        var document = _graph.Solution.Solution.GetDocument(node.SyntaxTree);
        return _models[node.SyntaxTree] = document is null ? null
            : document.TryGetSemanticModel(out var model) ? model : document.GetSemanticModelAsync().GetAwaiter().GetResult();
    }

    private static bool IsErrorType(ITypeSymbol? type)
    {
        if (type is null) return false;
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == "Exception") return false;
        return ErrorTypeNames.Any(n => type.Name.Contains(n, StringComparison.Ordinal));
    }

    /// <summary>
    /// Método (com fonte) que registra erro: cria a notificação/falha de validação ou chama outro método que registra.
    /// Conditional: só registra dentro de um if/switch/catch (Existe notifica só quando não encontra).
    /// </summary>
    private Notifier? NotifierOf(IMethodSymbol? method, int depth = 0)
    {
        if (method is null || depth > 4) return null;
        var key = (method.ReducedFrom ?? method).OriginalDefinition;
        if (_notifiers.TryGetValue(key, out var cached)) return cached;
        _notifiers[key] = null;

        Notifier? result = null;
        foreach (var reference in key.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            if (ModelOf(declaration) is not { } model) continue;
            foreach (var node in declaration.DescendantNodes())
            {
                if (node is not (BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax) || InsideLambda(node, declaration)) continue;
                var records = node switch
                {
                    BaseObjectCreationExpressionSyntax creation => IsErrorType(model.GetTypeInfo(creation).Type),
                    InvocationExpressionSyntax invocation => NotifierOf(model.GetSymbolInfo(invocation).Symbol as IMethodSymbol, depth + 1) is not null,
                    _ => false,
                };
                if (!records) continue;
                var conditional = node.Ancestors().TakeWhile(a => a != declaration)
                    .Any(a => a is IfStatementSyntax or SwitchSectionSyntax or ConditionalExpressionSyntax or CatchClauseSyntax or SwitchExpressionArmSyntax);
                result = new Notifier(conditional);
                if (!conditional) break;
            }
            if (result is not null) break;
        }
        return _notifiers[key] = result;
    }

    /// <summary>Mensagem registrada pelo método chamado: último argumento da notificação, com os parâmetros ligados aos argumentos da chamada.</summary>
    private string? NotifierMessage(InvocationExpressionSyntax call, SemanticModel model, Func<IParameterSymbol, string?>? bound, int depth = 0)
    {
        if (depth > 4 || model.GetSymbolInfo(call).Symbol is not IMethodSymbol callee) return null;
        string? Bound(IParameterSymbol p) => SymbolicResolver.Argument(call, callee, p) is { } argument ? Text(argument, model, bound) : null;

        foreach (var reference in (callee.ReducedFrom ?? callee).OriginalDefinition.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            if (ModelOf(declaration) is not { } calleeModel) continue;
            var nodes = declaration.DescendantNodes().Where(n => !InsideLambda(n, declaration)).ToList();
            if (nodes.OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault(c => IsErrorType(calleeModel.GetTypeInfo(c).Type)) is { } creation)
                return creation.ArgumentList?.Arguments.LastOrDefault() is { } last ? Text(last.Expression, calleeModel, Bound) : null;
            if (nodes.OfType<InvocationExpressionSyntax>().FirstOrDefault(i => NotifierOf(calleeModel.GetSymbolInfo(i).Symbol as IMethodSymbol) is not null) is { } inner)
                return NotifierMessage(inner, calleeModel, Bound, depth + 1);
        }
        return null;
    }

    /// <summary>Texto de uma expressão: constante, interpolação com constantes ou parâmetros ligados à chamada.</summary>
    private static string? Text(ExpressionSyntax expression, SemanticModel model, Func<IParameterSymbol, string?>? bound)
    {
        if (model.GetConstantValue(expression) is { HasValue: true, Value: string constant }) return constant;
        var e = SymbolicResolver.Unwrap(expression);
        switch (e)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;
            case InterpolatedStringExpressionSyntax interpolated:
                return string.Concat(interpolated.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax t => t.TextToken.ValueText,
                    InterpolationSyntax hole => Text(hole.Expression, model, bound) ?? $"{{{hole.Expression.Compact()}}}",
                    _ => c.ToString(),
                }));
            case IdentifierNameSyntax when bound is not null && model.GetSymbolInfo(e).Symbol is IParameterSymbol parameter:
                return bound(parameter);
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) && Text(b.Left, model, bound) is { } left && Text(b.Right, model, bound) is { } right:
                return left + right;
        }
        return null;
    }

    /// <summary>
    /// Chamada que registra erro: NotifyError(...) pelo nome, Bus.PublicarEvento(new DomainNotification(...)) ou um
    /// método que notifica. Null quando a chamada não registra erro.
    /// </summary>
    private Notified? NotificationMessage(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (IsNotification(invocation))
        {
            var (message, syntax) = MessageOf(invocation.ArgumentList.Arguments.LastOrDefault()?.Expression ?? (SyntaxNode)invocation, model);
            return new Notified(message, syntax);
        }
        if (invocation.ArgumentList.Arguments.Select(a => SymbolicResolver.Unwrap(a.Expression)).OfType<BaseObjectCreationExpressionSyntax>()
                .FirstOrDefault(c => IsErrorType(model.GetTypeInfo(c).Type)) is { } created)
        {
            var (message, syntax) = created.ArgumentList?.Arguments.LastOrDefault() is { } last ? MessageOf(last.Expression, model) : (null, null);
            return new Notified(message, syntax);
        }
        // Só o método que sempre notifica: programacao.AdicionarOrigem(...) notifica dentro de um if próprio (regra dele).
        return NotifierOf(model.GetSymbolInfo(invocation).Symbol as IMethodSymbol) is { Conditional: false }
            ? new Notified(NotifierMessage(invocation, model, null), null)
            : null;
    }

    /// <summary>
    /// Método chamado na condição que registra o erro (if (!Existe(...)) return ...). Delegated: o método está no
    /// fluxo analisado e registra o erro dentro de um if, que já é uma regra.
    /// </summary>
    private Recorder? ConditionNotifier(ExpressionSyntax condition, AnalyzedMethod method)
    {
        foreach (var invocation in condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (method.Model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee || NotifierOf(callee) is not { } notifier) continue;
            var original = (callee.ReducedFrom ?? callee).OriginalDefinition;
            var name = $"{callee.ContainingType.Name}.{callee.Name}";
            return notifier.Conditional && _analyzed.Contains(original)
                ? new Recorder(name, true, null, invocation)
                : new Recorder(name, false, NotifierMessage(invocation, method.Model, null), invocation);
        }
        return null;
    }

    /// <summary>Regras que notificam: o status é o que o controller devolve com notificação (BadRequest do ApiResponse).</summary>
    private void ApplyNotificationStatus()
    {
        if (NotificationStatus is not { } status) return;
        foreach (var rule in Rules.Where(r => r.Kind == "notify" && r.StatusSource == "inferido"))
        {
            rule.Status = status;
            rule.StatusSource = "código";
            rule.StatusNote = null;
        }
    }

    private static bool IsNotification(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        return name is not null && (name.StartsWith("Notify", StringComparison.Ordinal) || name.StartsWith("Notific", StringComparison.Ordinal)
            || name is "AddError" or "AdicionarErro" or "AddNotification" or "AddModelError");
    }

    private sealed record GuardInfo(Pred Trigger, string Code, string Exception);

    /// <summary>ArgumentNullException.ThrowIfNull(x), ArgumentOutOfRangeException.ThrowIfNegative(x), Guard.Against.Null(x)...</summary>
    private GuardInfo? Guard(InvocationExpressionSyntax inv, Scope scope)
    {
        if (scope.Model.GetSymbolInfo(inv).Symbol is not IMethodSymbol method) return null;
        var args = inv.ArgumentList.Arguments;
        if (args.Count == 0) return null;
        var type = method.ContainingType.Name;
        var name = method.Name;
        var isThrowIf = name.StartsWith("ThrowIf", StringComparison.Ordinal) && type.EndsWith("Exception", StringComparison.Ordinal);
        var isGuard = type is "Guard" or "GuardClauseExtensions" or "Ensure" or "Check";
        if (!isThrowIf && !isGuard) return null;

        var subject = _resolver.Term(args[0].Expression, scope);
        var text = args[0].Expression.Compact();
        Term? Arg(int i) => i < args.Count ? _resolver.Term(args[i].Expression, scope) : null;
        var kind = isThrowIf ? name["ThrowIf".Length..] : name;
        var v = subject as VarTerm;

        Pred? trigger = kind switch
        {
            "Null" when v is not null => new NullPred(v.Var, $"{text} == null"),
            "NullOrEmpty" when v is not null => new BlankPred(v.Var, false, $"string.IsNullOrEmpty({text})"),
            "NullOrWhiteSpace" when v is not null => new BlankPred(v.Var, true, $"string.IsNullOrWhiteSpace({text})"),
            "Negative" when subject is not null => new CmpPred(subject, "<", ConstTerm.Of(0), $"{text} < 0"),
            "Zero" when subject is not null => new CmpPred(subject, "==", ConstTerm.Of(0), $"{text} == 0"),
            "NegativeOrZero" when subject is not null => new CmpPred(subject, "<=", ConstTerm.Of(0), $"{text} <= 0"),
            "GreaterThan" when subject is not null && Arg(1) is { } max => new CmpPred(subject, ">", max, $"{text} > {args[1].Expression.Compact()}"),
            "GreaterThanOrEqual" when subject is not null && Arg(1) is { } max2 => new CmpPred(subject, ">=", max2, $"{text} >= {args[1].Expression.Compact()}"),
            "LessThan" when subject is not null && Arg(1) is { } min => new CmpPred(subject, "<", min, $"{text} < {args[1].Expression.Compact()}"),
            "LessThanOrEqual" when subject is not null && Arg(1) is { } min2 => new CmpPred(subject, "<=", min2, $"{text} <= {args[1].Expression.Compact()}"),
            "Equal" when subject is not null && Arg(1) is { } eq => new CmpPred(subject, "==", eq, $"{text} == {args[1].Expression.Compact()}"),
            "NotEqual" when subject is not null && Arg(1) is { } ne => new CmpPred(subject, "!=", ne, $"{text} != {args[1].Expression.Compact()}"),
            "OutOfRange" when subject is not null && Arg(2) is { } lo && Arg(3) is { } hi =>
                Pred.Or(new CmpPred(subject, "<", lo, $"{text} < {args[2].Expression.Compact()}"), new CmpPred(subject, ">", hi, $"{text} > {args[3].Expression.Compact()}")),
            _ => null,
        };
        trigger ??= _resolver.Opaque(inv, scope);
        var exception = kind switch
        {
            "Null" => "ArgumentNullException",
            "Negative" or "Zero" or "NegativeOrZero" or "GreaterThan" or "GreaterThanOrEqual" or "LessThan" or "LessThanOrEqual" or "Equal" or "NotEqual" or "OutOfRange" => "ArgumentOutOfRangeException",
            _ => "ArgumentException",
        };
        return new GuardInfo(trigger, trigger.Text, isThrowIf ? type : exception);
    }

    // ---- Status HTTP ----

    private static readonly Dictionary<string, int> ResultStatus = new()
    {
        ["Ok"] = 200, ["OkObjectResult"] = 200, ["OkResult"] = 200, ["Json"] = 200, ["Content"] = 200, ["File"] = 200,
        ["Created"] = 201, ["CreatedAtAction"] = 201, ["CreatedAtRoute"] = 201, ["CreatedResult"] = 201, ["CreatedAtActionResult"] = 201, ["CreatedAtRouteResult"] = 201,
        ["Accepted"] = 202, ["AcceptedAtAction"] = 202, ["AcceptedAtRoute"] = 202, ["AcceptedResult"] = 202,
        ["NoContent"] = 204, ["NoContentResult"] = 204,
        ["BadRequest"] = 400, ["BadRequestObjectResult"] = 400, ["BadRequestResult"] = 400, ["ValidationProblem"] = 400,
        ["Unauthorized"] = 401, ["UnauthorizedResult"] = 401, ["UnauthorizedObjectResult"] = 401,
        ["Forbid"] = 403, ["ForbidResult"] = 403,
        ["NotFound"] = 404, ["NotFoundResult"] = 404, ["NotFoundObjectResult"] = 404,
        ["Conflict"] = 409, ["ConflictResult"] = 409, ["ConflictObjectResult"] = 409,
        ["UnprocessableEntity"] = 422, ["UnprocessableEntityResult"] = 422, ["UnprocessableEntityObjectResult"] = 422,
    };

    /// <summary>Status possíveis de um retorno do controller: Ok(...) → 200; ObterErrosModel() → segue o método (BadRequest → 400).</summary>
    private HashSet<int> StatusOf(ExpressionSyntax expression, SemanticModel model, int depth)
    {
        var e = SymbolicResolver.Unwrap(expression);
        switch (e)
        {
            case InvocationExpressionSyntax inv:
            {
                var name = inv.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    GenericNameSyntax g => g.Identifier.Text,
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                    _ => null,
                };
                if (name == "StatusCode" && inv.ArgumentList.Arguments.FirstOrDefault() is { } code
                    && model.GetConstantValue(code.Expression) is { HasValue: true, Value: int status })
                    return [status];
                if (name == "Problem")
                    return [inv.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "statusCode") is { } sc
                        && model.GetConstantValue(sc.Expression) is { HasValue: true, Value: int ps } ? ps : 500];
                if (name is not null && ResultStatus.TryGetValue(name, out var known)) return [known];

                // Método do próprio controller (ou da base): os status dos returns dele.
                if (depth < 3 && model.GetSymbolInfo(inv).Symbol is IMethodSymbol target)
                    foreach (var reference in target.DeclaringSyntaxReferences)
                    {
                        var declaration = reference.GetSyntax();
                        if (!model.Compilation.ContainsSyntaxTree(declaration.SyntaxTree)) continue;
                        var targetModel = model.Compilation.GetSemanticModel(declaration.SyntaxTree);
                        var returns = declaration is BaseMethodDeclarationSyntax { ExpressionBody: { } arrow }
                            ? [arrow.Expression]
                            : declaration.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.Expression).OfType<ExpressionSyntax>();
                        var statuses = returns.SelectMany(r => StatusOf(r, targetModel, depth + 1)).ToHashSet();
                        if (statuses.Count > 0) return statuses;
                    }
                return [];
            }
            case BaseObjectCreationExpressionSyntax creation:
            {
                var type = model.GetTypeInfo(creation).Type?.Name;
                if (creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                        .FirstOrDefault(a => a.Left.ToString() == "StatusCode") is { } assignment
                    && model.GetConstantValue(assignment.Right) is { HasValue: true, Value: int set })
                    return [set];
                return type is not null && ResultStatus.TryGetValue(type, out var known) ? [known] : [];
            }
        }
        return [];
    }

    private void CollectSuccessStatus()
    {
        var entry = _graph.EntryPoint;
        var returns = entry.Declaration.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(r => entry.IsReachable(r) && !InsideLambda(r, entry.Declaration) && !_exitStatements.Contains(r)
                && !r.Ancestors().TakeWhile(a => a != entry.Declaration).Any(a => a is CatchClauseSyntax))
            .ToList();

        var statuses = returns.Where(r => r.Expression is not null).SelectMany(r => StatusOf(r.Expression!, entry.Model, 0)).ToList();
        if (entry.Declaration is BaseMethodDeclarationSyntax { ExpressionBody: { } arrow })
            statuses.AddRange(StatusOf(arrow.Expression, entry.Model, 0));

        var success = statuses.LastOrDefault(s => s is >= 200 and < 300);
        // return ApiResponse(...) que devolve Ok ou BadRequest: o BadRequest é a resposta das notificações.
        if (success > 0 && returns.Any(r => r.Expression is { } e && StatusOf(e, entry.Model, 0) is var both && both.Any(s => s < 400) && both.Any(s => s >= 400)))
            NotificationStatus ??= statuses.First(s => s >= 400);
        if (success > 0)
        {
            SuccessStatus = success;
            SuccessStatusSource = "código";
            return;
        }

        var returnType = SymbolicResolver.UnwrapTask(entry.Symbol.ReturnType);
        SuccessStatus = 200;
        SuccessStatusSource = returnType.SpecialType == SpecialType.System_Void || returnType.Name == "Task" ? "inferido" : "inferido";
    }

    /// <summary>Status de uma exceção: try/catch na cadeia de chamadas, mapeamento do middleware ou o nome da exceção.</summary>
    private (int? Status, string Source, string? Note) ExceptionStatus(ITypeSymbol? type, SyntaxNode at, AnalyzedMethod method, string? fallbackName = null)
    {
        var node = at;
        var current = method;
        for (var depth = 0; depth < 32; depth++)
        {
            foreach (var tryStatement in node.Ancestors().TakeWhile(a => a != current.Declaration).OfType<TryStatementSyntax>())
            {
                if (!tryStatement.Block.Span.Contains(node.Span)) continue;
                var handler = tryStatement.Catches.FirstOrDefault(c => c.Declaration is null || Derives(type, current.Model.GetTypeInfo(c.Declaration.Type).Type));
                if (handler is null) continue;

                var exit = ExitOf(handler.Block.Statements, current);
                switch (exit?.Kind)
                {
                    case "return" when current == _graph.EntryPoint:
                    case "notify" when current == _graph.EntryPoint:
                        var status = exit.Statuses.FirstOrDefault(s => s >= 400);
                        return status > 0
                            ? (status, "código", $"capturada em {current.DisplayName} (catch {handler.Declaration?.Type.Compact() ?? ""})")
                            : (400, "inferido", $"capturada em {current.DisplayName} e devolvida como notificação");
                    case "throw":
                        type = exit.ExceptionType;
                        break;
                    case null when handler.Block.Statements.OfType<ThrowStatementSyntax>().Any(t => t.Expression is null):
                        break; // throw; relança a mesma exceção
                    default:
                        return (null, "inferido", $"capturada em {current.DisplayName}: o fluxo continua (exceção tratada)");
                }
            }

            if (_resolver.CallSiteOf(current) is not { } site) break;
            node = site.Syntax;
            current = site.Caller;
        }

        if (type is not null && _exceptions.Find(type) is { } mapped)
            return (mapped.Status, "middleware", $"mapeado em {mapped.Source}");

        var name = type?.Name ?? fallbackName ?? "";
        var names = new List<string> { name };
        for (var t = (type as INamedTypeSymbol)?.BaseType; t is not null && t.Name != "Exception"; t = t.BaseType) names.Add(t.Name);
        bool Has(params string[] parts) => names.Any(n => parts.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)));

        if (Has("NotFound", "NaoEncontrad", "NãoEncontrad", "Inexistente")) return (404, "inferido", "pelo nome da exceção; confira o tratamento global de exceções");
        if (Has("Unauthorized", "NaoAutorizad", "NãoAutorizad", "Unauthenticated")) return (401, "inferido", "pelo nome da exceção; confira o tratamento global de exceções");
        if (Has("Forbidden", "Proibid", "AcessoNegado", "Permissao", "Permission")) return (403, "inferido", "pelo nome da exceção; confira o tratamento global de exceções");
        if (Has("Conflict", "Conflito", "Duplicad", "JaExiste")) return (409, "inferido", "pelo nome da exceção; confira o tratamento global de exceções");
        if (Has("Validation", "Validacao", "Validação", "Domain", "Dominio", "Domínio", "Negocio", "Negócio", "Business", "Regra", "Rule"))
            return (400, "inferido", "pelo nome da exceção; confira o tratamento global de exceções");
        return (500, "inferido", "exceção sem tratamento encontrado no código: o ASP.NET Core devolve 500");
    }

    private static bool Derives(ITypeSymbol? type, ITypeSymbol? baseType)
    {
        if (baseType is null) return false;
        if (baseType.Name == "Exception" && baseType.ContainingNamespace?.ToDisplayString() == "System") return true;
        for (var t = type; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, baseType.OriginalDefinition)) return true;
        return false;
    }

    private static bool HasApiController(INamedTypeSymbol controller)
    {
        for (var t = controller; t is not null; t = t.BaseType)
            if (t.GetAttributes().Any(a => a.AttributeClass?.Name is "ApiControllerAttribute")) return true;
        return controller.ContainingAssembly.GetAttributes().Any(a => a.AttributeClass?.Name is "ApiControllerAttribute");
    }

    // ---- Efeitos ----

    private void CollectEffects(EndpointAnalysisContext context)
    {
        foreach (var change in context.EntityChanges.Where(c => c.EffectClass != EffectClasses.Infrastructure))
        {
            if (Locate(change.Source) is { } site)
                Effects.Add(new EffectSite { Site = site, Kind = change.Operation, Target = change.Entity, Description = $"{change.Entity} {change.Operation}", Change = change });
            foreach (var property in change.PropertyChanges)
                if (Locate(property.Source) is { } propertySite)
                    Effects.Add(new EffectSite
                    {
                        Site = propertySite, Kind = change.Operation, Target = change.Entity,
                        Description = $"{change.Entity}.{property.Property} = {property.Value}", Change = change, Property = property,
                    });
        }

        foreach (var effect in context.Effects.Where(e => e.Kind is SinkKinds.SaveChanges or SinkKinds.Publish or SinkKinds.Send or SinkKinds.Http or SinkKinds.FileWrite
                     && e.Class != EffectClasses.Infrastructure && e.EventKind != EventKinds.DomainError))
            if (Locate(effect.Source) is { } site)
                Effects.Add(new EffectSite { Site = site, Kind = effect.Kind, Target = effect.Target, Description = effect.Description });
    }

    /// <summary>Encontra no grafo o trecho de uma referência (arquivo, método, linha).</summary>
    private Site? Locate(SourceReference? source)
    {
        if (source is null) return null;
        var method = _graph.Methods.FirstOrDefault(m => m.DisplayName == source.Method && m.SourceFile == source.File)
            ?? _graph.Methods.FirstOrDefault(m => m.SourceFile == source.File && m.Declaration.StartLine() <= source.Line
                && m.Declaration.GetLocation().GetLineSpan().EndLinePosition.Line + 1 >= source.Line);
        if (method is null) return null;

        var nodes = method.Declaration.DescendantNodes().Where(n => n.StartLine() == source.Line).ToList();
        var node = nodes.OfType<StatementSyntax>().FirstOrDefault(s => s is not BlockSyntax) ?? nodes.OfType<ExpressionSyntax>().FirstOrDefault() ?? nodes.FirstOrDefault();
        return node is null ? null : SiteOf(node, method);
    }

    // ---- Mensagens ----

    private static (string? Message, InterpolatedStringExpressionSyntax? Syntax) MessageOf(SyntaxNode expression, SemanticModel model)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return (literal.Token.ValueText, null);
                // $"{nameof(Programacao)} não existe" é constante.
                case InterpolatedStringExpressionSyntax interpolated when model.SyntaxTree == interpolated.SyntaxTree
                    && model.GetConstantValue(interpolated).Value is string constantText:
                    return (constantText, null);
                case InterpolatedStringExpressionSyntax interpolated:
                    return (string.Concat(interpolated.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : c.ToString())), interpolated);
                case MemberAccessExpressionSyntax member when model.GetConstantValue(member).Value is string constant:
                    return (constant, null);
            }
        }
        return (null, null);
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e) => e is AwaitExpressionSyntax a ? a.Expression : e;
}
