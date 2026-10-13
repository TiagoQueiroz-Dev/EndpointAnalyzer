using System.Text.Json;

namespace EndpointAnalyzer.AI.Codex;

/// <param name="AuthMethod">"chatgpt" (conta do ChatGPT) ou "apiKey".</param>
/// <param name="LoginError">Motivo da última tentativa de login que falhou (para a interface).</param>
public sealed record CodexAuthStatus(
    bool CliInstalled,
    bool LoggedIn,
    string? AuthMethod = null,
    string? Email = null,
    string? PlanType = null,
    AiUsage? Usage = null,
    string? LoginError = null)
{
    /// <summary>Logado com a conta do ChatGPT (limite do plano), e não com uma chave da API.</summary>
    public bool UsesSubscription => LoggedIn && AuthMethod == "chatgpt";
}

/// <summary>
/// Login do Codex com a conta do ChatGPT pelo `codex app-server`: a interface abre a URL de autorização e o próprio Codex
/// recebe o retorno do navegador (servidor local em 127.0.0.1), então não há código para colar. Nenhum token sai do
/// CODEX_HOME do projeto: a API só devolve e-mail, plano e percentuais de uso.
/// </summary>
public class CodexAuth(CodexOptions options)
{
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private CodexAuthStatus? _status;
    private DateTime _statusAt;
    private LoginSession? _login;
    private string? _loginError;

    public bool LoginPending => _login is { Completed: false };

    public async Task<CodexAuthStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        // App-server do login encerrou sem avisar a conclusão (ex.: porta de retorno ocupada).
        if (_login is { Completed: true } ended && Interlocked.CompareExchange(ref _login, null, ended) == ended)
            _loginError ??= "O login do Codex foi interrompido. Clique em 'Entrar com Codex' novamente.";

        if (!refresh && _status is not null && DateTime.UtcNow - _statusAt < StatusCacheDuration)
            return _status with { LoginError = _loginError };

        CodexAuthStatus status;
        try
        {
            await using var server = await CodexAppServer.StartAsync(options, cancellationToken);
            status = await ReadStatusAsync(server, cancellationToken);
        }
        catch (CodexNotInstalledException)
        {
            status = new CodexAuthStatus(CliInstalled: false, LoggedIn: false);
        }
        catch (Exception e) when (e is CodexAppServerException or TimeoutException or IOException)
        {
            // Versões sem o app-server: só o tipo de login.
            status = await LoginStatusAsync(cancellationToken);
        }

        _status = status;
        _statusAt = DateTime.UtcNow;
        return status with { LoginError = _loginError };
    }

    private static async Task<CodexAuthStatus> ReadStatusAsync(CodexAppServer server, CancellationToken cancellationToken)
    {
        var read = await server.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        if (!read.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            return new CodexAuthStatus(CliInstalled: true, LoggedIn: false);

        var method = Text(account, "type");
        if (method != "chatgpt") return new CodexAuthStatus(true, true, method);

        AiUsage? usage = null;
        try
        {
            usage = ParseUsage(await server.RequestAsync("account/rateLimits/read", null, cancellationToken));
        }
        catch (Exception e) when (e is CodexAppServerException or TimeoutException)
        {
            // Sem os limites a conta continua utilizável; a interface só não mostra o uso.
        }
        return new CodexAuthStatus(true, true, method, Text(account, "email"), Text(account, "planType"), usage);
    }

    internal static AiUsage? ParseUsage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;
        // "codex" é o limite do plano; rateLimits é a visão de um balde só (versões anteriores).
        var snapshot = response.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object
            && byId.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object
                ? codex
                : response.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object ? single : default;
        if (snapshot.ValueKind != JsonValueKind.Object) return null;

        var reached = snapshot.TryGetProperty("rateLimitReachedType", out var type) && type.ValueKind == JsonValueKind.String
            || response.TryGetProperty("ordinaryUsageAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.False;
        return new AiUsage(Window(snapshot, "primary"), Window(snapshot, "secondary"), reached);
    }

    private static AiUsageWindow? Window(JsonElement snapshot, string name)
    {
        if (!snapshot.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var used) || !used.TryGetInt32(out var percent)) return null;
        long? minutes = window.TryGetProperty("windowDurationMins", out var m) && m.TryGetInt64(out var mins) ? mins : null;
        DateTimeOffset? resetsAt = window.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new AiUsageWindow(percent, minutes, resetsAt);
    }

    /// <summary>`codex login status`: "Logged in using ChatGPT", "Logged in using an API key - sk-***" ou "Not logged in".</summary>
    private async Task<CodexAuthStatus> LoginStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await CodexProcess.RunAsync(CodexProcess.StartInfo(options, ["login", "status"]),
                input: null, TimeSpan.FromSeconds(30), cancellationToken);
            return ParseLoginStatus(result.ExitCode, result.Output + "\n" + result.Error);
        }
        catch (CodexNotInstalledException)
        {
            return new CodexAuthStatus(CliInstalled: false, LoggedIn: false);
        }
    }

    internal static CodexAuthStatus ParseLoginStatus(int exitCode, string output)
    {
        if (exitCode != 0 || !output.Contains("Logged in", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
            return new CodexAuthStatus(CliInstalled: true, LoggedIn: false);
        var method = output.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "chatgpt" : "apiKey";
        return new CodexAuthStatus(true, true, method);
    }

    /// <summary>Inicia o login com a conta do ChatGPT e devolve a URL que o usuário deve abrir.</summary>
    public async Task<string> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await CancelLoginCoreAsync();
            _loginError = null;

            var server = await CodexAppServer.StartAsync(options, cancellationToken);
            var session = new LoginSession(server);
            server.Notification += (method, parameters) =>
            {
                if (method == "account/login/completed") _ = CompleteAsync(session, parameters);
            };
            try
            {
                var response = await server.RequestAsync("account/login/start", new { type = "chatgpt" }, cancellationToken);
                session.Id = Text(response, "loginId");
                var url = Text(response, "authUrl") ?? throw new InvalidOperationException("O Codex não gerou a URL de login. Atualize o Codex e tente novamente.");
                _login = session;
                session.Expire(LoginTimeout, () => FailAsync(session, "O login não foi concluído em 10 minutos. Clique em 'Entrar com Codex' novamente."));
                return url;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }
        catch (CodexAppServerException e)
        {
            throw new InvalidOperationException($"Não foi possível iniciar o login do Codex: {e.Message}");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CompleteAsync(LoginSession session, JsonElement parameters)
    {
        var id = Text(parameters, "loginId");
        if (id is not null && session.Id is not null && id != session.Id) return;
        var success = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        if (success) await FinishAsync(session, error: null);
        else await FailAsync(session, Text(parameters, "error") is { Length: > 0 } error ? $"Falha no login do Codex: {AiFailures.Redact(error)}" : "O login do Codex não foi concluído.");
    }

    private Task FailAsync(LoginSession session, string error) => FinishAsync(session, error);

    private async Task FinishAsync(LoginSession session, string? error)
    {
        await _lock.WaitAsync();
        try
        {
            if (_login != session) return;
            _login = null;
            _loginError = error;
            _status = null;
        }
        finally
        {
            _lock.Release();
        }
        await session.DisposeAsync();
    }

    public async Task CancelLoginAsync()
    {
        await _lock.WaitAsync();
        try { await CancelLoginCoreAsync(); }
        finally { _lock.Release(); }
    }

    private async Task CancelLoginCoreAsync()
    {
        if (_login is not { } session) return;
        _login = null;
        if (session.Id is { } id && !session.Server.HasExited)
        {
            try { await session.Server.RequestAsync("account/login/cancel", new { loginId = id }, CancellationToken.None); }
            catch (Exception e) when (e is CodexAppServerException or TimeoutException) { }
        }
        await session.DisposeAsync();
    }

    /// <summary>Desconecta a conta do projeto; o Codex do terminal e o app desktop têm login próprio e continuam logados.</summary>
    public async Task<CodexAuthStatus> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await CodexProcess.RunAsync(CodexProcess.StartInfo(options, ["logout"]), input: null, TimeSpan.FromSeconds(30), cancellationToken);
        _loginError = null;
        return await GetStatusAsync(refresh: true, cancellationToken);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>App-server que recebe o retorno do navegador; fica vivo até o login terminar, ser cancelado ou expirar.</summary>
    private sealed class LoginSession(CodexAppServer server) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _expiry = new();
        private int _disposed;

        public CodexAppServer Server { get; } = server;

        public string? Id { get; set; }

        public bool Completed => Volatile.Read(ref _disposed) != 0 || Server.HasExited;

        public void Expire(TimeSpan after, Func<Task> onExpired) =>
            _ = Task.Delay(after, _expiry.Token).ContinueWith(t => t.IsCanceled ? Task.CompletedTask : onExpired(), TaskScheduler.Default).Unwrap();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _expiry.CancelAsync();
            await Server.DisposeAsync();
            _expiry.Dispose();
        }
    }
}
