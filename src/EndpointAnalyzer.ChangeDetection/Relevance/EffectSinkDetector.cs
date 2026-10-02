using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.ChangeDetection.Relevance;

/// <summary>
/// Encontra os sinks (§19): operações observáveis do fluxo — THROW, RETURN do endpoint, INSERT/UPDATE/DELETE,
/// SaveChanges, Publish/Send, requisição HTTP e escrita de arquivo.
/// </summary>
public class EffectSinkDetector(RelevanceOptions options)
{
    private static readonly string[] HttpMethods =
        ["GetAsync", "PostAsync", "PutAsync", "PatchAsync", "DeleteAsync", "SendAsync", "GetStringAsync", "GetStreamAsync",
         "GetByteArrayAsync", "GetFromJsonAsync", "PostAsJsonAsync", "PutAsJsonAsync", "PatchAsJsonAsync"];

    private static readonly string[] FileMethods =
        ["WriteAllText", "WriteAllTextAsync", "WriteAllBytes", "WriteAllBytesAsync", "WriteAllLines", "WriteAllLinesAsync",
         "AppendAllText", "AppendAllTextAsync", "AppendAllLines", "Create", "Copy", "Move", "Delete"];

    public Dictionary<AnalyzedMethod, List<Effect>> Detect(CallGraph graph, EntityChangeAnalysis changes)
    {
        var result = graph.Methods.ToDictionary(m => m, _ => new List<Effect>());

        foreach (var method in graph.Methods)
        {
            var effects = result[method];
            var isEntry = method == graph.EntryPoint;

            foreach (var node in method.Declaration.DescendantNodes())
            {
                if (!method.IsReachable(node)) continue;

                switch (node)
                {
                    case ThrowStatementSyntax { Expression: { } thrown }:
                        effects.Add(ThrowEffect(method, thrown, node));
                        break;
                    case ThrowExpressionSyntax te:
                        effects.Add(ThrowEffect(method, te.Expression, node));
                        break;
                    case ReturnStatementSyntax { Expression: { } returned } when isEntry:
                        effects.Add(Create(method, SinkKinds.Return, EffectClasses.Primary, ReturnTarget(returned), $"retorna {ReturnTarget(returned)}", node));
                        break;
                    case InvocationExpressionSyntax invocation:
                        if (InvocationEffect(method, invocation) is { } effect) effects.Add(effect);
                        break;
                }
            }
        }

        foreach (var change in changes.Raw.Where(r => r.IsOperation))
            result[change.Method].Add(new Effect
            {
                Kind = change.Operation,
                Class = EffectClasses.Primary,
                Target = change.Entity,
                Description = $"{change.Entity} {change.Operation}",
                Condition = change.Condition,
                Source = change.Source,
            });

        return result;
    }

    private Effect? InvocationEffect(AnalyzedMethod method, InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return null;

        if (name is "SaveChanges" or "SaveChangesAsync")
            return Create(method, SinkKinds.SaveChanges, EffectClasses.Primary, "banco de dados", "persiste as alterações (SaveChanges)", invocation);

        var symbol = method.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var containing = symbol?.ContainingType;

        if (containing is not null && HttpMethods.Contains(name) && IsHttpClient(containing))
            return Create(method, SinkKinds.Http, EffectClasses.Secondary, FirstArgument(invocation) ?? "HTTP", $"requisição HTTP {name.Replace("Async", "").Replace("AsJson", "").ToUpperInvariant()}", invocation);

        if (containing is { Name: "File" or "Directory" } && containing.ContainingNamespace?.ToDisplayString() == "System.IO" && FileMethods.Contains(name))
            return Create(method, SinkKinds.FileWrite, EffectClasses.Secondary, FirstArgument(invocation) ?? "arquivo", $"grava arquivo ({name})", invocation);

        if (RelevanceOptions.StartsWithAny(name, options.PublishMethodPrefixes) && invocation.ArgumentList.Arguments.Count > 0)
        {
            var argument = invocation.ArgumentList.Arguments[0].Expression;
            var eventType = method.Context.KnownTypeOf(argument, method.Model, method.Declaration)?.Type;
            if (eventType is null || !LooksLikeMessage(eventType)) return null;

            var kind = ClassifyEvent(eventType);
            var sink = name.StartsWith("Send", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Enviar", StringComparison.OrdinalIgnoreCase)
                ? SinkKinds.Send
                : SinkKinds.Publish;
            var effect = Create(method, sink, kind switch
            {
                EventKinds.BusinessEvent => EffectClasses.Secondary,
                _ => EffectClasses.Infrastructure,
            }, eventType.Name, kind == EventKinds.DomainError ? "notifica erro de domínio" : $"publica {eventType.Name}", invocation);
            effect.EventKind = kind;
            return effect;
        }

        return null;
    }

    public string ClassifyEvent(ITypeSymbol type)
    {
        var names = new List<string>();
        for (var t = type; t is not null; t = t.BaseType) names.Add(t.Name);

        if (names.Any(n => RelevanceOptions.ContainsAny(n, options.DomainErrorEventContains))) return EventKinds.DomainError;
        if (names.Any(n => RelevanceOptions.ContainsAny(n, options.InfraEventContains))) return EventKinds.InfraEvent;
        return EventKinds.BusinessEvent;
    }

    /// <summary>O argumento publicado é um evento/comando/mensagem (e não, por exemplo, uma string).</summary>
    private bool LooksLikeMessage(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None) return false;
        for (var t = type; t is not null; t = t.BaseType)
            if (RelevanceOptions.EndsWithAny(t.Name, options.BusinessEventSuffixes) || RelevanceOptions.ContainsAny(t.Name, options.DomainErrorEventContains))
                return true;
        return type.AllInterfaces.Any(i => i.Name is "INotification" or "IRequest" or "IEvent" or "IIntegrationEvent" or "ICommand" or "IMessage");
    }

    private Effect ThrowEffect(AnalyzedMethod method, ExpressionSyntax thrown, SyntaxNode node)
    {
        var type = method.ResolveType(method.Model.GetTypeInfo(thrown).Type);
        var name = type?.Name ?? thrown.Compact();
        var business = RelevanceOptions.ContainsAny(name, options.BusinessExceptionContains);
        var effect = Create(method, SinkKinds.Throw, business ? EffectClasses.Primary : EffectClasses.Secondary, name, $"lança {name}", node);
        effect.Message = MessageOf(thrown, method.Model);
        return effect;
    }

    private static bool IsHttpClient(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name is "HttpClient" or "HttpMessageInvoker" or "RestClient") return true;
        return type.Name.Contains("HttpClient", StringComparison.Ordinal);
    }

    private static string ReturnTarget(ExpressionSyntax returned) => returned switch
    {
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax id } => id.Identifier.Text,
        InvocationExpressionSyntax { Expression: GenericNameSyntax g } => g.Identifier.Text,
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => m.Name.Identifier.Text,
        BaseObjectCreationExpressionSyntax c => c.Compact(),
        _ => returned.Compact(),
    };

    private static string? FirstArgument(InvocationExpressionSyntax invocation) =>
        invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.Compact();

    private static string? MessageOf(SyntaxNode expression, SemanticModel model)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression):
                    return literal.Token.ValueText;
                case InterpolatedStringExpressionSyntax interpolated:
                    return string.Concat(interpolated.Contents.Select(c => c is InterpolatedStringTextSyntax t ? t.TextToken.ValueText : c.ToString()));
                case MemberAccessExpressionSyntax member when model.GetConstantValue(member).Value is string constant:
                    return constant;
            }
        }
        return null;
    }

    private static Effect Create(AnalyzedMethod method, string kind, string effectClass, string target, string description, SyntaxNode node) => new()
    {
        Kind = kind,
        Class = effectClass,
        Target = target,
        Description = description,
        Condition = SyntaxConditions.Combine(method.PathCondition, SyntaxConditions.GetEnclosingCondition(node, method.Declaration)),
        Source = new SourceReference
        {
            File = method.SourceFile,
            Method = method.DisplayName,
            Line = node.StartLine(),
            Code = (node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() ?? node).Snippet(4),
        },
    };
}
