using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAnalyzer.AI.ClaudeCode;

public sealed record ClaudeAuthStatus(
    bool CliInstalled,
    bool LoggedIn,
    string? AuthMethod = null,
    string? Email = null,
    string? SubscriptionType = null,
    string? OrgName = null)
{
    /// <summary>Logado com conta claude.ai (plano mensal), e não com o Console/API.</summary>
    public bool UsesSubscription => LoggedIn && AuthMethod == "claude.ai";
}

/// <summary>
/// Login do Claude Code com a conta do Claude (claude auth login --claudeai).
/// O fluxo sem terminal é: abrir a URL de login, entrar com a conta e colar o código exibido.
/// </summary>
public partial class ClaudeCodeAuth(ClaudeCodeOptions options)
{
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ClaudeAuthStatus? _status;
    private DateTime _statusAt;
    private LoginSession? _login;

    public bool LoginPending => _login is { Process.HasExited: false };

    public async Task<ClaudeAuthStatus> GetStatusAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _status is not null && DateTime.UtcNow - _statusAt < StatusCacheDuration)
            return _status;

        ClaudeAuthStatus status;
        try
        {
            var result = await ProcessRunner.RunAsync(
                ProcessRunner.StartInfo(options.Executable, ["auth", "status", "--json"]),
                input: null, TimeSpan.FromSeconds(30), cancellationToken);
            status = Parse(result.Output);
        }
        catch (ClaudeCodeNotInstalledException)
        {
            status = new ClaudeAuthStatus(CliInstalled: false, LoggedIn: false);
        }

        _status = status;
        _statusAt = DateTime.UtcNow;
        return status;
    }

    /// <summary>Inicia o login e devolve a URL que o usuário deve abrir.</summary>
    public async Task<string> StartLoginAsync(string? email, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            CancelLoginCore();

            var arguments = new List<string> { "auth", "login", "--claudeai" };
            if (!string.IsNullOrWhiteSpace(email)) arguments.AddRange(["--email", email.Trim()]);

            var info = ProcessRunner.StartInfo(options.Executable, arguments);
            // A própria interface abre a URL; evita o CLI abrir outra aba.
            info.Environment["BROWSER"] = "none";

            var session = new LoginSession(ProcessRunner.Start(info));
            _login = session;

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (session.LoginUrl is { } url) return url;
                if (session.Process.HasExited)
                    throw new InvalidOperationException($"O login terminou sem gerar a URL: {session.CleanOutput}");
                await Task.Delay(200, cancellationToken);
            }

            CancelLoginCore();
            throw new TimeoutException("O Claude Code não gerou a URL de login.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Envia o código exibido após o login no navegador.</summary>
    public async Task<ClaudeAuthStatus> SubmitCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Informe o código de autorização.");

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var session = _login is { Process.HasExited: false } active
                ? active
                : throw new InvalidOperationException("Nenhum login em andamento. Clique em 'Entrar com Claude' novamente.");

            await session.Process.StandardInput.WriteLineAsync(code.Trim());
            await session.Process.StandardInput.FlushAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Um código válido é trocado em poucos segundos; se o CLI continua esperando, o texto colado não é um código.
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await session.Process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                CancelLoginCore();
                throw new InvalidOperationException("Código não reconhecido. Copie o código completo exibido após autorizar e clique em 'Entrar com Claude' novamente.");
            }

            _login = null;
            var status = await GetStatusAsync(refresh: true, cancellationToken);
            if (session.Process.ExitCode != 0 || !status.LoggedIn)
            {
                var detail = LastLines(session.CleanOutput);
                throw new InvalidOperationException(detail.Contains("400")
                    ? "Código inválido ou expirado. Clique em 'Entrar com Claude' e tente novamente."
                    : $"Falha no login: {detail}");
            }
            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CancelLoginAsync()
    {
        await _lock.WaitAsync();
        try { CancelLoginCore(); }
        finally { _lock.Release(); }
    }

    /// <summary>Desconecta o Claude Code desta máquina (vale para todas as sessões do Claude Code).</summary>
    public async Task<ClaudeAuthStatus> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await ProcessRunner.RunAsync(
            ProcessRunner.StartInfo(options.Executable, ["auth", "logout"]),
            input: null, TimeSpan.FromSeconds(30), cancellationToken);
        return await GetStatusAsync(refresh: true, cancellationToken);
    }

    private void CancelLoginCore()
    {
        if (_login is null) return;
        ProcessRunner.TryKill(_login.Process);
        _login.Process.Dispose();
        _login = null;
    }

    private static ClaudeAuthStatus Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var loggedIn = root.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            return new ClaudeAuthStatus(true, loggedIn, Str("authMethod"), Str("email"), Str("subscriptionType"), Str("orgName"));
        }
        catch (JsonException)
        {
            return new ClaudeAuthStatus(CliInstalled: true, LoggedIn: false);
        }
    }

    private static string LastLines(string text) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));

    [GeneratedRegex(@"https://[^\s\x07\x1B]*oauth[^\s\x07\x1B]*")]
    private static partial Regex LoginUrlRegex();

    /// <summary>Processo de login em andamento; lê a saída continuamente (o prompt do código não termina em nova linha).</summary>
    private sealed class LoginSession
    {
        private readonly StringBuilder _output = new();

        public LoginSession(Process process)
        {
            Process = process;
            _ = Pump(process.StandardOutput);
            _ = Pump(process.StandardError);
        }

        public Process Process { get; }

        public string CleanOutput
        {
            get { lock (_output) return ProcessRunner.StripAnsi(_output.ToString()); }
        }

        public string? LoginUrl
        {
            get
            {
                string raw;
                lock (_output) raw = _output.ToString();
                var match = LoginUrlRegex().Match(raw);
                return match.Success ? match.Value : null;
            }
        }

        private async Task Pump(StreamReader reader)
        {
            var buffer = new char[1024];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                    lock (_output) _output.Append(buffer, 0, read);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }
    }
}
