using System.Text.RegularExpressions;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Runtime;

/// <param name="Valid">O serviço aceitou o token.</param>
/// <param name="Message">O que foi testado e o resultado ("GET /veiculo/v1: 401 sem token, 200 com o token").</param>
public sealed record ApiTokenCheck(bool Valid, string Message);

/// <summary>
/// Confere o token da API informado pelo usuário: sobe o serviço e chama um GET que exige autenticação sem o token e
/// com o token. Válido quando o serviço deixa de responder 401/403 com o token.
/// </summary>
public sealed partial class ApiTokenChecker(IAppRunner runner)
{
    private const int MaxProbes = 5;

    /// <summary>Valor do header Authorization: "Bearer {token}", ou o texto como veio quando já traz o esquema ("Basic ...").</summary>
    public static string? Authorization(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var value = token.Trim();
        if (value.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) value = value["Authorization:".Length..].Trim();
        return SchemeRegex().IsMatch(value) ? value : $"Bearer {value}";
    }

    public async Task<ApiTokenCheck> CheckAsync(LoadedSolution solution, EndpointInfo endpoint, string token, CancellationToken cancellationToken = default)
    {
        var authorization = Authorization(token) ?? throw new ArgumentException("Informe o token da API.");
        var catalog = await EndpointCatalog.BuildAsync(solution, endpoint, cancellationToken);
        // GETs com menos parâmetros primeiro; os de rota recebem 1 (a autorização acontece antes de buscar o registro).
        var probes = catalog.Endpoints
            .Where(e => e.Method == "GET")
            .OrderBy(e => e.Parameters.Count)
            .ThenBy(e => e.Route.Count(c => c == '{'))
            .Take(MaxProbes)
            .Select(e => RouteValueRegex().Replace(e.Route, "1"))
            .ToList();
        if (probes.Count == 0) return new ApiTokenCheck(false, $"O projeto {endpoint.Project} não tem endpoint GET para testar o token.");

        await using var app = await runner.StartAsync(solution, endpoint, _ => { }, cancellationToken);
        foreach (var url in probes)
        {
            var anonymous = await StatusAsync(app, url, null, cancellationToken);
            if (anonymous is not (401 or 403)) continue;
            var authenticated = await StatusAsync(app, url, authorization, cancellationToken);
            return authenticated is 401 or 403 or null
                ? new ApiTokenCheck(false, $"Token recusado: GET {url} respondeu {authenticated?.ToString() ?? "sem resposta"} com o token.")
                : new ApiTokenCheck(true, $"Token aceito: GET {url} respondeu {anonymous} sem token e {authenticated} com o token.");
        }

        // Nenhum GET testado exige autenticação: confere só que o token não é recusado.
        var status = await StatusAsync(app, probes[0], authorization, cancellationToken);
        return status is 401 or 403 or null
            ? new ApiTokenCheck(false, $"Token recusado: GET {probes[0]} respondeu {status?.ToString() ?? "sem resposta"} com o token.")
            : new ApiTokenCheck(true, $"Token não recusado (GET {probes[0]} respondeu {status}), mas nenhum GET testado exige autenticação.");
    }

    private static async Task<int?> StatusAsync(RunningApp app, string url, string? authorization, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url.TrimStart('/'));
        if (authorization is not null) message.Headers.TryAddWithoutValidation("Authorization", authorization);
        try
        {
            using var response = await app.Client.SendAsync(message, cancellationToken);
            return (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z][\w-]*\s+\S")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteValueRegex();
}
