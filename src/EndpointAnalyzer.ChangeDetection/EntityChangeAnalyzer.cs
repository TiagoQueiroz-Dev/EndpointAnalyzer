using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.ChangeDetection;

/// <summary>Observação de alteração de entidade em um método específico do grafo.</summary>
public sealed record RawEntityChange(
    AnalyzedMethod Method,
    string Entity,
    string Operation,
    string? Property,
    string? Value,
    string? Condition,
    SourceReference Source,
    bool IsOperation,
    bool Direct = false);

public sealed class EntityChangeAnalysis
{
    public List<EntityChange> Changes { get; init; } = [];

    /// <summary>Onde SaveChanges/SaveChangesAsync é chamado.</summary>
    public List<SourceReference> PersistencePoints { get; init; } = [];

    /// <summary>Observações por método (para classificar efeitos e montar o grafo de negócio).</summary>
    public List<RawEntityChange> Raw { get; init; } = [];

    /// <summary>Métodos que chamam SaveChanges.</summary>
    public HashSet<AnalyzedMethod> SavingMethods { get; init; } = [];

    public IReadOnlyCollection<string> EntityTypes { get; init; } = [];

    public EntityCatalog? Catalog { get; init; }

    /// <summary>Refaz o agrupamento considerando só as observações de <paramref name="keep"/>.</summary>
    public EntityChangeAnalysis Filter(Func<RawEntityChange, bool> keep, Func<RawEntityChange, string>? effectClass = null)
    {
        var raw = Raw.Where(keep).ToList();
        return new EntityChangeAnalysis
        {
            Raw = raw,
            Changes = EntityChangeAnalyzer.Merge(raw, effectClass),
            PersistencePoints = PersistencePoints,
            SavingMethods = SavingMethods,
            EntityTypes = EntityTypes,
            Catalog = Catalog,
        };
    }
}

public interface IEntityChangeAnalyzer
{
    Task<EntityChangeAnalysis> AnalyzeAsync(CallGraph graph, CancellationToken cancellationToken = default);
}

/// <summary>
/// Detecta INSERT/UPDATE/DELETE de entidades: chamadas EF Core (Add, Update, Remove, ExecuteUpdate),
/// métodos de repositório (Adicionar, Atualizar, Excluir...), new Entidade { ... } e atribuições de propriedades.
/// Os tipos são resolvidos no contexto de execução (TEntity → Veiculo) e só valem trechos alcançáveis.
/// </summary>
public class EntityChangeAnalyzer : IEntityChangeAnalyzer
{
    private static readonly Dictionary<string, string> EfOperations = new()
    {
        ["Add"] = EntityOperations.Insert,
        ["AddAsync"] = EntityOperations.Insert,
        ["AddRange"] = EntityOperations.Insert,
        ["AddRangeAsync"] = EntityOperations.Insert,
        ["Update"] = EntityOperations.Update,
        ["UpdateRange"] = EntityOperations.Update,
        ["Remove"] = EntityOperations.Delete,
        ["RemoveRange"] = EntityOperations.Delete,
    };

    private static readonly (string Prefix, string Operation)[] RepositoryVerbs =
    [
        ("Adicionar", EntityOperations.Insert), ("Inserir", EntityOperations.Insert), ("Incluir", EntityOperations.Insert),
        ("Cadastrar", EntityOperations.Insert), ("Criar", EntityOperations.Insert), ("Armazenar", EntityOperations.Insert),
        ("Add", EntityOperations.Insert), ("Insert", EntityOperations.Insert), ("Create", EntityOperations.Insert),
        ("Atualizar", EntityOperations.Update), ("Alterar", EntityOperations.Update), ("Editar", EntityOperations.Update),
        ("Modificar", EntityOperations.Update), ("Update", EntityOperations.Update),
        ("Excluir", EntityOperations.Delete), ("Remover", EntityOperations.Delete), ("Deletar", EntityOperations.Delete),
        ("Delete", EntityOperations.Delete), ("Remove", EntityOperations.Delete),
    ];

    public async Task<EntityChangeAnalysis> AnalyzeAsync(CallGraph graph, CancellationToken cancellationToken = default)
    {
        var catalog = await EntityCatalog.BuildAsync(graph.Solution.Solution, cancellationToken);
        var raw = new List<RawEntityChange>();
        var persistence = new List<SourceReference>();
        var saving = new HashSet<AnalyzedMethod>();

        foreach (var method in graph.Methods)
            AnalyzeMethod(method, catalog, raw, persistence, saving);

        return new EntityChangeAnalysis
        {
            Raw = raw,
            Changes = Merge(raw),
            PersistencePoints = persistence,
            SavingMethods = saving,
            EntityTypes = catalog.Entities,
            Catalog = catalog,
        };
    }

    private static void AnalyzeMethod(AnalyzedMethod method, EntityCatalog catalog, List<RawEntityChange> raw, List<SourceReference> persistence, HashSet<AnalyzedMethod> saving)
    {
        var model = method.Model;
        var createdLocals = CreatedLocals(method, catalog);
        var isConstructor = method.Symbol.MethodKind == MethodKind.Constructor;

        foreach (var node in method.Declaration.DescendantNodes())
        {
            if (!method.IsReachable(node)) continue;

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    AnalyzeInvocation(method, invocation, catalog, raw, persistence, saving);
                    break;

                case BaseObjectCreationExpressionSyntax creation when TypeOf(method, creation) is { } createdType && catalog.IsEntity(createdType):
                    var entity = createdType.Name;
                    var condition = ConditionOf(method, creation);
                    raw.Add(new RawEntityChange(method, entity, EntityOperations.Insert, null, null, condition, Reference(method, creation), IsOperation: false));

                    // new Programacao { Status = ..., DataInicio = ... }
                    foreach (var assignment in creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
                        raw.Add(new RawEntityChange(method, entity, EntityOperations.Insert, assignment.Left.Compact(), assignment.Right.Compact(), condition, Reference(method, assignment), IsOperation: false));
                    break;

                case AssignmentExpressionSyntax assignment when assignment.Parent is not InitializerExpressionSyntax:
                    AnalyzeAssignment(method, assignment, catalog, createdLocals, isConstructor, raw);
                    break;
            }
        }
    }

    private static void AnalyzeInvocation(AnalyzedMethod method, InvocationExpressionSyntax invocation, EntityCatalog catalog,
        List<RawEntityChange> raw, List<SourceReference> persistence, HashSet<AnalyzedMethod> saving)
    {
        var model = method.Model;
        if (invocation.Expression is not MemberAccessExpressionSyntax access) return;

        var name = access.Name.Identifier.Text;
        var receiverType = TypeOf(method, access.Expression);

        if (name is "SaveChanges" or "SaveChangesAsync")
        {
            persistence.Add(Reference(method, invocation));
            saving.Add(method);
            return;
        }

        var condition = ConditionOf(method, invocation);

        // DbSet.Add(p) / _context.Add(p) / _context.Update(p) / _context.Remove(p)
        if (EfOperations.TryGetValue(name, out var efOperation) && (EntityCatalog.IsDbSet(receiverType) || EntityCatalog.IsDbContext(receiverType)))
        {
            var entity = EntityCatalog.IsDbSet(receiverType)
                ? EntityCatalog.ElementType(receiverType)
                : ArgumentEntity(method, invocation, catalog) ?? GenericArgument(method, access.Name);
            if (IsConcreteEntity(entity))
                raw.Add(new RawEntityChange(method, entity!.Name, efOperation, null, null, condition, Reference(method, invocation), IsOperation: true, Direct: true));
            return;
        }

        // DbSet.Where(...).ExecuteUpdate(s => s.SetProperty(x => x.Status, valor))
        if (name is "ExecuteUpdate" or "ExecuteUpdateAsync" or "ExecuteDelete" or "ExecuteDeleteAsync")
        {
            if (EntityCatalog.ElementType(receiverType) is not { } entity || !IsConcreteEntity(entity)) return;
            var operation = name.StartsWith("ExecuteUpdate") ? EntityOperations.Update : EntityOperations.Delete;
            raw.Add(new RawEntityChange(method, entity.Name, operation, null, null, condition, Reference(method, invocation), IsOperation: true, Direct: true));

            foreach (var set in invocation.ArgumentList.DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "SetProperty" }))
            {
                var args = set.ArgumentList.Arguments;
                if (args.Count < 2) continue;
                var property = args[0].Expression is SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax prop }
                    ? prop.Name.Identifier.Text
                    : args[0].Expression.Compact();
                raw.Add(new RawEntityChange(method, entity.Name, operation, property, args[1].Expression.Compact(), condition, Reference(method, set), IsOperation: false));
            }
            return;
        }

        // _repository.Adicionar(programacao) ou programacao.Itens.Add(item)
        var verb = RepositoryVerbs.FirstOrDefault(v => name.StartsWith(v.Prefix, StringComparison.Ordinal));
        if (verb.Prefix is null) return;

        var isRepository = receiverType is not null && receiverType.HasSource() && !catalog.IsEntity(receiverType);
        var isNavigation = access.Expression is MemberAccessExpressionSyntax nav
            && model.GetSymbolInfo(nav).Symbol is IPropertySymbol navProperty
            && catalog.IsEntity(method.ResolveType(navProperty.ContainingType));
        if (!isRepository && !isNavigation) return;

        if (ArgumentEntity(method, invocation, catalog) is { } argumentEntity && IsConcreteEntity(argumentEntity))
            raw.Add(new RawEntityChange(method, argumentEntity.Name, verb.Operation, null, null, condition, Reference(method, invocation), IsOperation: true));
    }

    private static void AnalyzeAssignment(AnalyzedMethod method, AssignmentExpressionSyntax assignment, EntityCatalog catalog,
        HashSet<string> createdLocals, bool isConstructor, List<RawEntityChange> raw)
    {
        var model = method.Model;
        var target = model.GetSymbolInfo(assignment.Left).Symbol;
        if (target is not (IPropertySymbol or IFieldSymbol { IsConst: false })) return;

        ITypeSymbol? ownerType;
        string? receiverName = null;
        var implicitThis = false;

        switch (assignment.Left)
        {
            case MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax:
                ownerType = method.Context.ThisType ?? method.Symbol.ContainingType;
                implicitThis = true;
                break;
            case MemberAccessExpressionSyntax access:
                ownerType = TypeOf(method, access.Expression);
                receiverName = access.Expression is IdentifierNameSyntax id ? id.Identifier.Text : null;
                break;
            case IdentifierNameSyntax when !target.IsStatic && IsMemberOfThis(target, method.Symbol.ContainingType):
                ownerType = method.Context.ThisType ?? method.Symbol.ContainingType;
                implicitThis = true;
                break;
            default:
                return;
        }

        if (!catalog.IsEntity(ownerType)) return;

        // Instância criada no próprio fluxo (new) ou atribuição no construtor da entidade → faz parte do INSERT.
        var operation = (implicitThis && isConstructor) || (receiverName is not null && createdLocals.Contains(receiverName))
            ? EntityOperations.Insert
            : EntityOperations.Update;

        var value = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            ? assignment.Right.Compact()
            : $"{assignment.Left.Compact()} {assignment.OperatorToken.Text} {assignment.Right.Compact()}";

        raw.Add(new RawEntityChange(method, ownerType!.Name, operation, PropertyName(target), value, ConditionOf(method, assignment), Reference(method, assignment), IsOperation: false));
    }

    /// <summary>Tipo da expressão no contexto de execução (genéricos resolvidos).</summary>
    private static ITypeSymbol? TypeOf(AnalyzedMethod method, ExpressionSyntax expression) =>
        method.ResolveType(method.Model.GetTypeInfo(expression).Type);

    /// <summary>§23: só vale entidade com tipo concreto (TEntity sem resolver não é efeito do endpoint).</summary>
    private static bool IsConcreteEntity(ITypeSymbol? type) =>
        type is INamedTypeSymbol { TypeKind: not TypeKind.TypeParameter } && type is not ITypeParameterSymbol;

    /// <summary>Variáveis locais que recebem new Entidade(...) dentro do método.</summary>
    private static HashSet<string> CreatedLocals(AnalyzedMethod method, EntityCatalog catalog)
    {
        var locals = new HashSet<string>();
        foreach (var declarator in method.Declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (declarator.Initializer?.Value is BaseObjectCreationExpressionSyntax creation && catalog.IsEntity(TypeOf(method, creation)))
                locals.Add(declarator.Identifier.Text);
        return locals;
    }

    private static ITypeSymbol? ArgumentEntity(AnalyzedMethod method, InvocationExpressionSyntax invocation, EntityCatalog catalog)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            // Tipo propagado (argumento conhecido) tem prioridade sobre o tipo estático.
            var type = method.Context.KnownTypeOf(argument.Expression, method.Model, method.Declaration)?.Type ?? TypeOf(method, argument.Expression);
            if (catalog.IsEntity(type)) return type;
            if (catalog.IsEntity(EntityCatalog.ElementType(type))) return EntityCatalog.ElementType(type);
        }
        return null;
    }

    private static ITypeSymbol? GenericArgument(AnalyzedMethod method, SimpleNameSyntax name) =>
        name is GenericNameSyntax { TypeArgumentList.Arguments: [var arg] } ? TypeOf(method, arg) : null;

    /// <summary>O membro pertence ao próprio tipo ou a uma classe base dele.</summary>
    private static bool IsMemberOfThis(ISymbol member, INamedTypeSymbol type)
    {
        if (member.ContainingType is null) return false;
        for (INamedTypeSymbol? t = type; t is not null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, member.ContainingType.OriginalDefinition)) return true;
        return false;
    }

    /// <summary>Campos de apoio (_status) aparecem como a propriedade correspondente (Status).</summary>
    private static string PropertyName(ISymbol symbol)
    {
        if (symbol is IFieldSymbol { AssociatedSymbol: IPropertySymbol property }) return property.Name;
        var name = symbol.Name.TrimStart('_');
        return name.Length > 0 && symbol is IFieldSymbol ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    private static string? ConditionOf(AnalyzedMethod method, SyntaxNode node) =>
        SyntaxConditions.Combine(method.PathCondition, SyntaxConditions.GetEnclosingCondition(node, method.Declaration));

    private static SourceReference Reference(AnalyzedMethod method, SyntaxNode node) => new()
    {
        File = method.SourceFile,
        Method = method.DisplayName,
        Line = node.StartLine(),
        Code = (node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() ?? node).Snippet(4),
    };

    /// <summary>Agrupa as observações por entidade + operação.</summary>
    internal static List<EntityChange> Merge(List<RawEntityChange> raw, Func<RawEntityChange, string>? effectClass = null)
    {
        var order = new[] { EntityOperations.Insert, EntityOperations.Update, EntityOperations.Delete };

        return raw
            .GroupBy(r => (r.Entity, r.Operation))
            .OrderBy(g => Array.IndexOf(order, g.Key.Operation))
            .Select(g =>
            {
                var operations = g.Where(r => r.IsOperation).ToList();
                var properties = g.Where(r => r.Property is not null)
                    .GroupBy(r => (r.Property, r.Value, r.Condition))
                    .Select(p => p.First())
                    .ToList();
                var main = operations.FirstOrDefault() ?? g.First();

                return new EntityChange
                {
                    Entity = g.Key.Entity,
                    Operation = g.Key.Operation,
                    EffectClass = effectClass?.Invoke(main) ?? EffectClasses.Primary,
                    Properties = properties.Select(p => p.Property!).Distinct().ToList(),
                    PropertyChanges = properties.Select(p => new PropertyChange
                    {
                        Property = p.Property!,
                        Value = p.Value ?? "",
                        Condition = p.Condition,
                        Source = p.Source,
                    }).ToList(),
                    // Com chamada explícita (Add/Update/Remove) vale a condição dela; senão, o que é comum a todas as alterações.
                    Condition = operations.Count > 0
                        ? SyntaxConditions.Common(operations.Select(o => o.Condition))
                        : SyntaxConditions.Common(g.Select(r => r.Condition)),
                    Source = main.Source,
                    CreationSource = g.Key.Operation == EntityOperations.Insert
                        ? g.FirstOrDefault(r => !r.IsOperation && r.Property is null)?.Source
                        : null,
                };
            })
            .ToList();
    }
}
