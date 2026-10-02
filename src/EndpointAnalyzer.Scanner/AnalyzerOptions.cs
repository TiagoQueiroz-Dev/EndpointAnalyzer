using Microsoft.CodeAnalysis;

namespace EndpointAnalyzer.Scanner;

public class AnalyzerOptions
{
    /// <summary>MAX_CALL_DEPTH: evita percorrer fluxos enormes indefinidamente.</summary>
    public int MaxCallDepth { get; set; } = 20;

    /// <summary>Verbos analisados. MVP: apenas POST, PUT e PATCH.</summary>
    public HashSet<string> HttpMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };

    /// <summary>Chamadas para esses namespaces não são seguidas.</summary>
    public List<string> IgnoredNamespaces { get; set; } = ["System", "Microsoft", "Newtonsoft", "AutoMapper", "Serilog"];

    /// <summary>Tamanho máximo do trecho de código de cada método enviado à IA.</summary>
    public int MaxSnippetChars { get; set; } = 6000;

    /// <summary>Padrões de nome usados para classificar a relevância dos nós.</summary>
    public RelevanceOptions Relevance { get; set; } = new();

    public bool IsIgnored(ISymbol symbol)
    {
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? "";
        return IgnoredNamespaces.Any(i => ns == i || ns.StartsWith(i + ".", StringComparison.Ordinal));
    }
}

/// <summary>
/// Padrões (por nome de tipo e método) para classificar nós em BUSINESS, VALIDATION, DATA_ACCESS,
/// PERSISTENCE, EXTERNAL_EFFECT, INFRASTRUCTURE e UTILITY. Sufixos de tipo comparam com o fim do nome;
/// prefixos de método com o começo; "contém" com qualquer parte. Todos ignoram maiúsculas.
/// </summary>
public class RelevanceOptions
{
    public List<string> InfrastructureTypeSuffixes { get; set; } =
    [
        "Bus", "Mediator", "MediatorHandler", "NotificationHandler", "Notifier", "Notificador", "Logger", "Audit", "Auditoria",
        "Cache", "EventStore", "Middleware", "Filter", "Interceptor", "Behavior", "Behaviour", "ApiController", "BaseController",
        "Telemetry", "Tracing", "HealthCheck", "Dispatcher",
    ];

    public List<string> InfrastructureMethodPrefixes { get; set; } =
    [
        "Notify", "Notific", "MostrarErros", "ObterErros", "ApiResponse", "ProblemaValidacao", "IsValidOperation",
        "ObterNotificacoes", "PossuiNotificacoes", "SalvarHistorico", "Log", "Registrar Log", "Auditar", "Trace",
    ];

    public List<string> UtilityTypeSuffixes { get; set; } =
    [
        "Extensions", "Extension", "Helper", "Helpers", "Util", "Utils", "Utilities", "Formatter", "Converter", "Mapper",
        "Profile", "Constants", "Constantes", "Enumeration", "ValueObject",
    ];

    public List<string> UtilityMethods { get; set; } = ["ToString", "Equals", "GetHashCode", "CompareTo", "Clone", "Dispose", "GetType", "Deconstruct"];

    public List<string> UtilityMethodPrefixes { get; set; } =
        ["Sanitiza", "Sanitize", "Normaliza", "Normalize", "Formata", "Format", "Converte", "Convert", "Parse", "Mapear", "Trim"];

    public List<string> PersistenceMethodPrefixes { get; set; } =
    [
        "SaveChanges", "SalvarAlteracoes", "Commit", "Persistir", "Persist", "Gravar", "Adicionar", "Inserir", "Incluir", "Atualizar",
        "Alterar", "Editar", "Remover", "Excluir", "Deletar", "Armazenar", "Add", "Insert", "Update", "Remove", "Delete", "Save",
    ];

    public List<string> DataAccessTypeSuffixes { get; set; } = ["Repository", "Repositorio", "Dao", "Query", "Queries", "ReadModel", "Context", "DbContext"];

    public List<string> DataAccessMethodPrefixes { get; set; } =
    [
        "Obter", "Buscar", "Listar", "Consultar", "Existe", "Possui", "Pesquisar", "Carregar", "Recuperar", "Selecionar", "Contar", "Ler",
        "Eh", "Get", "Find", "List", "Query", "Count", "Any", "Exists", "Has", "Load", "Retrieve", "Fetch", "Search", "Is",
    ];

    /// <summary>Consultas pelo fim do nome: PlacaAntigaExiste, ProdutoExistente.</summary>
    public List<string> DataAccessMethodSuffixes { get; set; } = ["Existe", "Exists", "Existente", "PorId", "ById"];

    public List<string> ValidationTypeSuffixes { get; set; } =
        ["Validator", "Validation", "Validacao", "Validador", "Specification", "Spec", "Rule", "Rules", "Regra", "Regras", "Policy"];

    public List<string> ValidationMethodPrefixes { get; set; } = ["Validar", "Validate", "Valida", "Verificar", "Verifica", "Check", "Ensure", "Garantir", "Assegurar"];

    public List<string> ValidationMethodContains { get; set; } = ["EhValid", "IsValid"];

    public List<string> ExternalEffectMethodPrefixes { get; set; } = ["Enviar", "Send", "Publicar", "Publish", "Disparar", "Integrar", "Notificar"];

    /// <summary>Tipos que o persistence/repository/service usam para persistir (UnitOfWork, Service...).</summary>
    public List<string> PersistenceTypeSuffixes { get; set; } = ["Repository", "Repositorio", "Service", "Servico", "UnitOfWork", "Uow", "Context", "DbContext"];

    public List<string> BusinessTypeSuffixes { get; set; } =
        ["Service", "Servico", "AppService", "ApplicationService", "Handler", "Manager", "Domain", "Aggregate", "UseCase", "Command", "Controller"];

    /// <summary>Eventos que representam erro de domínio (não expandem consumidores).</summary>
    public List<string> DomainErrorEventContains { get; set; } = ["DomainNotification", "Notification", "Notificacao", "Error", "Erro"];

    /// <summary>Eventos técnicos (histórico, log, auditoria).</summary>
    public List<string> InfraEventContains { get; set; } = ["Historico", "History", "Log", "Audit", "Auditoria", "Telemetry"];

    public List<string> BusinessEventSuffixes { get; set; } = ["Event", "Evento", "IntegrationEvent", "Command", "Comando", "Message", "Mensagem"];

    /// <summary>Métodos que publicam/enviam mensagens (inclusive em bibliotecas sem fonte, como IMediator.Publish).</summary>
    public List<string> PublishMethodPrefixes { get; set; } =
        ["Publish", "Publicar", "RaiseEvent", "Raise", "Send", "Enviar", "EnviarComando", "Dispatch", "Enqueue", "Produce", "Emit"];

    /// <summary>Exceções que representam regra de negócio.</summary>
    public List<string> BusinessExceptionContains { get; set; } = ["Domain", "Dominio", "RegraNegocio", "Business", "Negocio", "Validation", "Validacao", "NaoEncontrado", "NotFound", "Conflict"];

    /// <summary>Entidades técnicas: alterá-las é efeito de infraestrutura (ex.: histórico gravado pelo barramento).</summary>
    public List<string> InfrastructureEntityPrefixes { get; set; } = ["Historico", "History", "Log", "Audit", "Auditoria"];

    public static bool EndsWithAny(string value, IEnumerable<string> suffixes) =>
        suffixes.Any(s => value.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static bool StartsWithAny(string value, IEnumerable<string> prefixes) =>
        prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                          // "Is"/"Eh"/"Has" só como prefixo de palavra: IsValid, EhTransportadora (não "Issue", "Ehrlich")
                          && (p.Length > 3 || value.Length == p.Length || char.IsUpper(value[p.Length])));

    public static bool ContainsAny(string value, IEnumerable<string> parts) =>
        parts.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
}
