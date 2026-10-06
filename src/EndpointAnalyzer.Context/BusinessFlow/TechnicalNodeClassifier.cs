using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Context.BusinessFlow;

/// <summary>
/// Fase 3: classificação aproximada de cada nó técnico antes da IA, a partir da categoria de relevância da
/// análise estática e do nome do tipo/método. Ajuda a IA a separar regra real de infraestrutura.
/// </summary>
public static class TechnicalNodeClassifier
{
    // Chamadas normalmente colapsáveis no fluxo de negócio (continuam no fluxo técnico).
    private static readonly HashSet<string> InfrastructureMethods =
    [
        "ConfigureAwait", "GetQueryable", "Include", "ThenInclude", "AsNoTracking", "AsQueryable", "ToListAsync", "ToList",
        "ToArrayAsync", "FirstOrDefaultAsync", "SingleOrDefaultAsync", "Dispose", "DisposeAsync",
    ];

    public static string Classify(CallNode node, bool isRoot)
    {
        var type = StripGeneric(node.TypeName);
        var method = node.MethodName;

        if (isRoot || type.EndsWith("Controller", StringComparison.Ordinal)) return TechnicalNodeKinds.Controller;
        if (method is "Map" or "MapTo" or "ProjectTo" || type.Contains("Mapper", StringComparison.OrdinalIgnoreCase))
            return TechnicalNodeKinds.Mapping;
        if (InfrastructureMethods.Contains(method) || method.StartsWith("Log", StringComparison.Ordinal) && type.Contains("Logger", StringComparison.Ordinal))
            return TechnicalNodeKinds.Infrastructure;

        var repository = type.Contains("Repository", StringComparison.OrdinalIgnoreCase) || type.Contains("Repositorio", StringComparison.OrdinalIgnoreCase);
        return node.Category switch
        {
            NodeCategories.Validation => TechnicalNodeKinds.Validation,
            NodeCategories.Persistence => TechnicalNodeKinds.Persistence,
            NodeCategories.DataAccess => TechnicalNodeKinds.Query,
            NodeCategories.ExternalEffect => TechnicalNodeKinds.Integration,
            NodeCategories.Infrastructure => TechnicalNodeKinds.Infrastructure,
            NodeCategories.Utility => TechnicalNodeKinds.Helper,
            _ when repository => TechnicalNodeKinds.Repository,
            NodeCategories.Business when IsApplicationLayer(type) => TechnicalNodeKinds.Application,
            NodeCategories.Business => TechnicalNodeKinds.BusinessRule,
            _ => TechnicalNodeKinds.Unknown,
        };
    }

    private static bool IsApplicationLayer(string type) =>
        type.EndsWith("ApplicationService", StringComparison.Ordinal) || type.EndsWith("AppService", StringComparison.Ordinal)
        || type.EndsWith("Handler", StringComparison.Ordinal) || type.EndsWith("UseCase", StringComparison.Ordinal);

    private static string StripGeneric(string type)
    {
        var i = type.IndexOf('<');
        return i < 0 ? type : type[..i];
    }
}
