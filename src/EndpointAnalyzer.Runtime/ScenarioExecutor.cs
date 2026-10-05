using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>Requisição a executar: payload materializado de um cenário ou chamada de aquisição de dados.</summary>
public sealed record RuntimeRequest(string Method, string Url, Dictionary<string, string?>? Headers = null, JsonNode? Body = null)
{
    public static RuntimeRequest From(ScenarioRequest request) =>
        new(request.Method, request.Url, request.Headers, request.Body?.DeepClone());

    public bool IsRead => Method.ToUpperInvariant() is "GET" or "HEAD" or "OPTIONS";
}

/// <summary>
/// ScenarioExecutor: executa as requisições na API e registra status, corpo da resposta, exceção, tempo e payload.
/// Recusa rotas fora do catálogo, escritas fora do ambiente de análise e requisições além do limite.
/// </summary>
/// <param name="authorization">Header Authorization enviado em todas as requisições (token informado pelo usuário).</param>
public sealed partial class ScenarioExecutor(HttpClient client, RuntimeOptions options, EndpointCatalog catalog, bool writesAllowed, RuntimeValidation report,
    string? authorization = null)
{
    private int _sent;

    public bool BudgetExhausted { get; private set; }

    public int Remaining => Math.Max(0, options.MaxRequests - _sent);

    public bool WritesAllowed => writesAllowed;

    /// <summary>As requisições levam o token da API informado pelo usuário.</summary>
    public bool Authenticated => authorization is not null;

    public async Task<RuntimeExecution> ExecuteAsync(RuntimeRequest request, string phase, string? scenarioId = null, int attempt = 0,
        string? reason = null, CancellationToken cancellationToken = default)
    {
        var url = request.Url.StartsWith('/') ? request.Url : "/" + request.Url;
        var execution = new RuntimeExecution
        {
            Id = $"EX-{report.Executions.Count + 1:00}",
            Phase = phase,
            ScenarioId = scenarioId,
            Attempt = attempt,
            Method = request.Method.ToUpperInvariant(),
            Url = url,
            Headers = request.Headers is { Count: > 0 } ? request.Headers : null,
            RequestBody = request.Body?.DeepClone(),
            Reason = reason,
        };
        report.Executions.Add(execution);

        string? blocked = null;
        if (_sent >= options.MaxRequests)
        {
            BudgetExhausted = true;
            blocked = $"limite de {options.MaxRequests} requisições por análise atingido (Runtime:MaxRequests)";
        }
        else if (catalog.Match(execution.Method, url) is null)
            blocked = "rota fora do catálogo de endpoints da API";
        else if (!request.IsRead && !writesAllowed)
            blocked = "escrita (POST/PUT/PATCH/DELETE) desligada (Runtime:AllowWrites = false)";
        if (blocked is not null)
        {
            execution.Blocked = true;
            execution.Reason = reason is null ? blocked : $"{reason} — bloqueada: {blocked}";
            return execution;
        }

        _sent++;
        report.Stats.Requests++;
        using var message = new HttpRequestMessage(new HttpMethod(execution.Method), url.TrimStart('/'));
        foreach (var (name, value) in options.Headers) message.Headers.TryAddWithoutValidation(name, value);
        // Fica fora da execução registrada: o token não vai para o relatório nem para a IA.
        if (authorization is not null)
        {
            message.Headers.Remove("Authorization");
            message.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        foreach (var (name, value) in request.Headers ?? [])
        {
            message.Headers.Remove(name);
            message.Headers.TryAddWithoutValidation(name, value ?? "");
        }
        if (request.Body is not null)
            message.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            execution.Status = (int)response.StatusCode;
            execution.ResponseBody = body.Length > options.MaxStoredBodyChars ? body[..options.MaxStoredBodyChars] + "…" : body;
            if (execution.Status >= 500) execution.Exception = ExceptionIn(body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            execution.Exception = e is TaskCanceledException
                ? $"sem resposta em {options.RequestTimeoutSeconds}s"
                : $"falha de comunicação: {e.Message}";
        }
        execution.ElapsedMs = watch.ElapsedMilliseconds;
        return execution;
    }

    /// <summary>Nome e mensagem da exceção na página de erro/ProblemDetails ("System.InvalidOperationException: ...").</summary>
    internal static string? ExceptionIn(string body)
    {
        var match = ExceptionRegex().Match(body);
        if (!match.Success) return null;
        var text = match.Value.Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    [GeneratedRegex(@"\b[A-Z][\w.]*Exception\b(:[^\r\n""<]{0,300})?")]
    private static partial Regex ExceptionRegex();
}
