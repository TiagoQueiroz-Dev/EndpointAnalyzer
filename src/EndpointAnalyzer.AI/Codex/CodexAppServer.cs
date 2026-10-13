using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Core;

namespace EndpointAnalyzer.AI.Codex;

/// <summary>
/// Cliente do protocolo do `codex app-server` (JSON-RPC por stdio, uma mensagem por linha), o mesmo usado pelas extensões
/// de IDE: conta e plano, limites de uso, catálogo de modelos e login. Nenhum prompt é enviado ao modelo por aqui.
/// </summary>
internal sealed class CodexAppServer : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _reader;
    private readonly Task<string> _error;
    private long _nextId;

    private CodexAppServer(Process process)
    {
        _process = process;
        _reader = ReadAsync();
        _error = process.StandardError.ReadToEndAsync();
    }

    /// <summary>Notificações do servidor (método, parâmetros), ex.: account/login/completed.</summary>
    public event Action<string, JsonElement>? Notification;

    public bool HasExited => _process.HasExited;

    /// <exception cref="CodexNotInstalledException">Executável não encontrado.</exception>
    public static async Task<CodexAppServer> StartAsync(CodexOptions options, CancellationToken cancellationToken)
    {
        var server = new CodexAppServer(CodexProcess.Start(CodexProcess.StartInfo(options, ["app-server"])));
        try
        {
            await server.RequestAsync("initialize", new
            {
                clientInfo = new { name = "endpoint_analyzer", title = "Endpoint Analyzer", version = AnalyzerInfo.Version },
            }, cancellationToken);
            await server.SendAsync(new JsonObject { ["method"] = "initialized" }, cancellationToken);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <exception cref="CodexAppServerException">O servidor respondeu com erro.</exception>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = response;
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = JsonSerializer.SerializeToNode(parameters);
            await SendAsync(message, cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                return await response.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"O Codex não respondeu a '{method}' em {RequestTimeout.TotalSeconds:0} segundos.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            throw new CodexAppServerException(await ExitDetailAsync());
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }
                if (root.ValueKind != JsonValueKind.Object) continue;

                var hasMethod = root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
                if (root.TryGetProperty("id", out var idElement) && !hasMethod)
                {
                    if (idElement.TryGetInt64(out var id) && _pending.TryGetValue(id, out var pending)) Complete(pending, root);
                }
                else if (hasMethod && !root.TryGetProperty("id", out _))
                {
                    var parameters = root.TryGetProperty("params", out var p) ? p : default;
                    try { Notification?.Invoke(method.GetString()!, parameters); }
                    catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
                }
                // Pedidos do servidor (aprovações) não acontecem nestas chamadas: nenhuma sessão é iniciada.
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        // Processo encerrado: libera quem ainda espera resposta.
        var detail = await ExitDetailAsync();
        foreach (var pending in _pending.Values) pending.TrySetException(new CodexAppServerException(detail));
    }

    private static void Complete(TaskCompletionSource<JsonElement> pending, JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            pending.TrySetException(new CodexAppServerException(AiFailures.Redact(message ?? "erro desconhecido")));
            return;
        }
        pending.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
    }

    private async Task<string> ExitDetailAsync()
    {
        string error;
        try
        {
            error = _process.HasExited ? await _error.WaitAsync(TimeSpan.FromSeconds(2)) : "";
        }
        catch (Exception e) when (e is TimeoutException or IOException or ObjectDisposedException)
        {
            error = "";
        }
        var lastLines = string.Join(" ", ProcessRunner.StripAnsi(error)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
        return "o Codex encerrou o app-server" + (lastLines.Length > 0 ? $": {AiFailures.Redact(lastLines)}" : ". Atualize o Codex e tente novamente.");
    }

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { }
        ProcessRunner.TryKill(_process);
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await _reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
        _process.Dispose();
        _write.Dispose();
    }
}

/// <summary>Erro devolvido pelo `codex app-server` (ou o servidor encerrou).</summary>
public class CodexAppServerException(string message) : Exception(message);
