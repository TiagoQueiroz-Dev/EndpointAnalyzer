using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>
/// Sessão do protocolo de controle usado pelo Agent SDK (stream-json), sem enviar prompts ao modelo: initialize (modelos)
/// e consultas como get_usage (limites do plano). Uma requisição por vez; o processo é encerrado no Dispose.
/// </summary>
internal sealed class ClaudeCodeControl : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly Task<string> _error;
    private int _nextId;

    private ClaudeCodeControl(Process process)
    {
        _process = process;
        _error = process.StandardError.ReadToEndAsync();
    }

    /// <exception cref="ClaudeCodeNotInstalledException">Executável não encontrado.</exception>
    public static ClaudeCodeControl Start(ClaudeCodeOptions options)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EndpointAnalyzer", "claude-code");
        Directory.CreateDirectory(workingDirectory);
        var info = ProcessRunner.StartInfo(options,
            ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
             "--tools", "", "--setting-sources", "", "--no-session-persistence"], workingDirectory);
        return new ClaudeCodeControl(ProcessRunner.Start(info));
    }

    /// <summary>Envia o pedido e devolve o "response" da resposta de sucesso.</summary>
    /// <param name="subtype">initialize, get_usage...</param>
    /// <param name="what">O que é consultado, nas mensagens de erro (ex.: "os modelos").</param>
    public async Task<JsonElement> RequestAsync(string subtype, JsonObject? fields, string what, CancellationToken cancellationToken)
    {
        var id = $"endpoint-analyzer-{subtype}-{++_nextId}";
        var request = fields?.DeepClone().AsObject() ?? new JsonObject();
        request["subtype"] = subtype;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var message = new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request };
            // O stdin permanece aberto até a resposta: EOF antecipado pode encerrar a sessão.
            await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), timeout.Token);
            await _process.StandardInput.FlushAsync(timeout.Token);
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (Text(root, "type") != "control_response" || !root.TryGetProperty("response", out var response)
                    || Text(response, "request_id") != id) continue;
                if (Text(response, "subtype") == "error")
                    throw new InvalidOperationException($"Não foi possível consultar {what} do Claude Code: {AiFailures.Redact(Text(response, "error") ?? "erro desconhecido")}");
                return response.TryGetProperty("response", out var payload) ? payload.Clone() : default;
            }
            throw new InvalidOperationException($"O Claude Code encerrou a consulta sem informar {what}. Atualize o Claude Code e tente novamente.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"O Claude Code não informou {what} em {RequestTimeout.TotalSeconds:0} segundos.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"O Claude Code retornou dados inválidos ao consultar {what}. Atualize o Claude Code e tente novamente.", e);
        }
        catch (IOException)
        {
            throw new InvalidOperationException($"O Claude Code encerrou a consulta sem informar {what}. Atualize o Claude Code e tente novamente.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        ProcessRunner.TryKill(_process);
        await _process.WaitForExitAsync(CancellationToken.None);
        await _error;
        _process.Dispose();
    }

    internal static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
