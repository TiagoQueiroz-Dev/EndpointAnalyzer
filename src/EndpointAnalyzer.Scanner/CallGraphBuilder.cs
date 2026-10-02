using EndpointAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Scanner;

/// <summary>
/// Método alcançável a partir do endpoint, com tudo que os analisadores precisam: sintaxe, semântica,
/// contexto de execução (genéricos, this concreto, argumentos) e os trechos alcançáveis.
/// </summary>
public sealed class AnalyzedMethod
{
    public required IMethodSymbol Symbol { get; init; }

    public required SyntaxNode Declaration { get; init; }

    public required SemanticModel Model { get; init; }

    public required CallNode Node { get; init; }

    public required string SourceFile { get; init; }

    public required AnalysisExecutionContext Context { get; init; }

    public Reachability Reachability { get; init; } = Reachability.All;

    /// <summary>Condição acumulada no caminho desde o endpoint até este método.</summary>
    public string? PathCondition { get; init; }

    public int Depth { get; init; }

    public AnalyzedMethod? Caller { get; init; }

    /// <summary>Chamadas feitas por este método (nós filhos) com a sintaxe de origem.</summary>
    public List<CallSite> CallSites { get; } = [];

    public bool IsReachable(SyntaxNode node) => Reachability.IsReachable(node);

    /// <summary>Resolve tipos genéricos do contexto: TEntity → Veiculo.</summary>
    public ITypeSymbol? ResolveType(ITypeSymbol? type) => Context.Resolve(type);

    public string DisplayName => $"{Node.TypeName}.{Node.MethodName}";

    public static string DisplayNameOf(IMethodSymbol method) =>
        method.MethodKind == MethodKind.Constructor
            ? $"{method.ContainingType.Name}.ctor"
            : $"{method.ContainingType.Name}.{method.Name}";
}

public sealed record CallSite(SyntaxNode Syntax, CallNode Node, IReadOnlyList<string> Usage);

public sealed class CallGraph
{
    public required LoadedSolution Solution { get; init; }

    public required EndpointInfo Endpoint { get; init; }

    public required CallNode Root { get; init; }

    public required IReadOnlyList<AnalyzedMethod> Methods { get; init; }

    public AnalyzedMethod EntryPoint => Methods[0];

    public AnalyzedMethod? MethodOf(CallNode node) => Methods.FirstOrDefault(m => m.Node == node);
}

public interface ICallGraphBuilder
{
    Task<CallGraph> BuildAsync(LoadedSolution solution, EndpointInfo endpoint, CancellationToken cancellationToken = default);
}

/// <summary>
/// Call graph orientado ao endpoint: segue o receiver concreto, a implementação registrada na DI
/// e o fluxo executável no contexto (genéricos e tipos propagados), em vez de tudo que pode ser alcançado.
/// </summary>
public class CallGraphBuilder(AnalyzerOptions options, IMethodResolver methodResolver) : ICallGraphBuilder
{
    public async Task<CallGraph> BuildAsync(LoadedSolution loaded, EndpointInfo endpoint, CancellationToken cancellationToken = default)
    {
        var entry = await methodResolver.ResolveAsync(loaded, endpoint, cancellationToken)
            ?? throw new InvalidOperationException($"Método do endpoint não encontrado: {endpoint.Controller}.{endpoint.Action}");

        var host = loaded.Solution.Projects.FirstOrDefault(p => p.Name == endpoint.Project);
        var di = await DependencyInjectionMap.BuildAsync(loaded.Solution, host, cancellationToken);
        var walker = new Walker(loaded, options, new CallResolver(loaded.Solution, di), cancellationToken);

        var context = new AnalysisExecutionContext
        {
            Method = entry,
            ThisType = entry.ContainingType,
            Generics = GenericTypeMap.For(entry, entry.ContainingType),
        };
        var root = await walker.VisitAsync(entry, context, depth: 0, callCondition: null, pathCondition: null, caller: null, resolution: null);

        return new CallGraph
        {
            Solution = loaded,
            Endpoint = endpoint,
            Root = root,
            Methods = walker.Methods,
        };
    }

    private sealed class Walker(LoadedSolution loaded, AnalyzerOptions options, CallResolver resolver, CancellationToken ct)
    {
        // VisitedMethods: evita loops A → B → C → A. A chave inclui o contexto (genéricos e this concreto).
        private readonly HashSet<string> _visited = [];
        private int _nextId;

        public List<AnalyzedMethod> Methods { get; } = [];

        public async Task<CallNode> VisitAsync(
            IMethodSymbol method,
            AnalysisExecutionContext context,
            int depth,
            string? callCondition,
            string? pathCondition,
            AnalyzedMethod? caller,
            (CallTarget Target, CallResolution Resolution)? resolution)
        {
            var location = method.OriginalDefinition.SourceLocation();
            var receiverType = context.ThisType ?? method.ContainingType;
            var node = new CallNode
            {
                Id = ++_nextId,
                MethodName = method.MethodKind == MethodKind.Constructor ? "ctor" : method.Name,
                TypeName = receiverType.Name,
                DeclaringType = receiverType.OriginalDefinition.Key() == method.ContainingType.OriginalDefinition.Key()
                    ? null
                    : method.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                SourceFile = loaded.RelativePath(location?.SourceTree?.FilePath),
                SourceLine = location?.StartLine() ?? 0,
                Condition = callCondition,
            };

            if (resolution is { } r)
            {
                node.ResolvedFrom = r.Target.Via;
                node.Receiver = r.Resolution.Receiver;
                node.Resolution = r.Target.Strategy;
                if (r.Resolution.Ambiguous)
                {
                    node.Ambiguous = true;
                    node.Candidates = r.Resolution.Candidates?.ToList();
                    return node;
                }
            }

            var key = $"{method.ToDisplayString()}|{context.ThisType?.ToDisplayString()}";
            if (!_visited.Add(key))
            {
                node.AlreadyVisited = true;
                return node;
            }

            if (depth >= options.MaxCallDepth)
            {
                node.DepthLimitReached = true;
                return node;
            }

            var declaration = await GetDeclarationAsync(method);
            if (declaration is null) return node;
            node.Description = EndpointMetadata.MethodSummary(method.OriginalDefinition, declaration);

            var document = loaded.Solution.GetDocument(declaration.SyntaxTree);
            var model = document is null ? null : await document.GetSemanticModelAsync(ct);
            if (model is null) return node;

            var analyzed = new AnalyzedMethod
            {
                Symbol = method,
                Declaration = declaration,
                Model = model,
                Node = node,
                SourceFile = node.SourceFile,
                Context = context,
                Reachability = BranchFeasibilityAnalyzer.Analyze(declaration, model, context),
                PathCondition = pathCondition,
                Depth = depth,
                Caller = caller,
            };
            Methods.Add(analyzed);

            foreach (var (syntax, called) in FindCalls(analyzed))
            {
                var condition = SyntaxConditions.GetEnclosingCondition(syntax, declaration);
                var control = SyntaxConditions.GetControlPath(syntax, declaration);
                var childPath = SyntaxConditions.Combine(pathCondition, condition);
                var usage = ValueUsageAnalyzer.Analyze(syntax, model, declaration);
                var (callStart, callEnd) = CallSiteLines(syntax);
                var callFile = loaded.RelativePath(syntax.SyntaxTree.FilePath);

                var callResolution = await resolver.ResolveAsync(syntax, called, analyzed, ct);
                foreach (var target in callResolution.Targets)
                {
                    var childContext = ChildContext(analyzed, syntax, target);
                    var child = await VisitAsync(target.Method, childContext, depth + 1, condition, childPath, analyzed, (target, callResolution));
                    child.Usage = usage.ToList();
                    child.Control = control;
                    (child.CallFile, child.CallLine, child.CallEndLine) = (callFile, callStart, callEnd);
                    node.Children.Add(child);
                    analyzed.CallSites.Add(new CallSite(syntax, child, usage));
                }
            }

            return node;
        }

        /// <summary>
        /// Linhas do ponto de chamada: a instrução inteira quando ela é simples (var x = Chamar(...);, return, throw);
        /// dentro de if/while/foreach/switch, só a expressão da chamada (para não destacar o bloco todo).
        /// </summary>
        private static (int Start, int End) CallSiteLines(SyntaxNode call)
        {
            SyntaxNode span = call.FirstAncestorOrSelf<StatementSyntax>() is { } statement
                && statement is ExpressionStatementSyntax or LocalDeclarationStatementSyntax or ReturnStatementSyntax
                    or ThrowStatementSyntax or YieldStatementSyntax
                ? statement
                : call;
            var lines = span.GetLocation().GetLineSpan();
            return (lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1);
        }

        /// <summary>Contexto do método chamado: genéricos resolvidos e tipos/valores dos argumentos (§7).</summary>
        private static AnalysisExecutionContext ChildContext(AnalyzedMethod caller, SyntaxNode call, CallTarget target)
        {
            var method = target.Method;
            var arguments = new Dictionary<string, KnownType>();
            var values = new Dictionary<string, KnownValue>();

            var argumentList = call switch
            {
                InvocationExpressionSyntax invocation => invocation.ArgumentList,
                BaseObjectCreationExpressionSyntax creation => creation.ArgumentList,
                ConstructorInitializerSyntax initializer => initializer.ArgumentList,
                _ => null,
            };

            if (argumentList is not null)
            {
                for (var i = 0; i < argumentList.Arguments.Count; i++)
                {
                    var argument = argumentList.Arguments[i];
                    var parameter = argument.NameColon is { } named
                        ? method.Parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.Text)
                        : i < method.Parameters.Length ? method.Parameters[i] : method.Parameters.LastOrDefault(p => p.IsParams);
                    if (parameter is null) continue;

                    var key = AnalysisExecutionContext.ParameterKey(parameter.OriginalDefinition);
                    if (caller.Context.KnownTypeOf(argument.Expression, caller.Model, caller.Declaration) is { } type)
                        arguments[key] = type;
                    if (caller.Context.KnownValueOf(argument.Expression, caller.Model) is { } value)
                        values[key] = value;
                }
            }

            return new AnalysisExecutionContext
            {
                Method = method,
                ThisType = target.ThisType,
                Generics = GenericTypeMap.For(method, target.ThisType, caller.Context.Generics),
                Arguments = arguments,
                Values = values,
            };
        }

        private IEnumerable<(SyntaxNode Syntax, IMethodSymbol Method)> FindCalls(AnalyzedMethod method)
        {
            foreach (var syntax in method.Declaration.DescendantNodes())
            {
                if (syntax is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax or ConstructorInitializerSyntax))
                    continue;

                // Branch impossível no contexto conhecido: não segue (§8).
                if (!method.IsReachable(syntax)) continue;

                var info = method.Model.GetSymbolInfo(syntax, ct);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                if (symbol is not IMethodSymbol called) continue;

                called = called.ReducedFrom ?? called;

                if (called.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) continue;
                if (called.IsImplicitlyDeclared) continue;
                if (called.MethodKind == MethodKind.Constructor && IsException(called.ContainingType)) continue;
                if (options.IsIgnored(called)) continue;

                // Sem código-fonte: só segue se for interface/abstrato do projeto (pode ter implementação).
                if (!called.HasSource() && called.ContainingType.TypeKind != TypeKind.Interface) continue;
                if (!called.HasSource() && !called.ContainingType.HasSource()) continue;

                yield return (syntax, called);
            }
        }

        private static bool IsException(INamedTypeSymbol type)
        {
            for (var t = type; t is not null; t = t.BaseType)
                if (t.Name == "Exception" && t.ContainingNamespace?.ToDisplayString() == "System") return true;
            return false;
        }

        private async Task<SyntaxNode?> GetDeclarationAsync(IMethodSymbol method)
        {
            foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
            {
                var syntax = await reference.GetSyntaxAsync(ct);
                switch (syntax)
                {
                    case BaseMethodDeclarationSyntax m when m.Body is not null || m.ExpressionBody is not null:
                    case AccessorDeclarationSyntax a when a.Body is not null || a.ExpressionBody is not null:
                    case LocalFunctionStatementSyntax:
                        return syntax;
                }
            }

            return null;
        }
    }
}
