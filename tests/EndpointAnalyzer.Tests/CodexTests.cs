using System.Diagnostics;
using System.Text.Json;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;

namespace EndpointAnalyzer.Tests;

public class CodexTests
{
    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static string Failed(string message) => Line(new { type = "turn.failed", error = new { message } });

    [Fact]
    public void Le_a_ultima_mensagem_do_agente_do_codex_exec()
    {
        var output = string.Join("\n",
            Line(new { type = "thread.started", thread_id = "t1" }),
            Line(new { type = "turn.started" }),
            // Aviso do CLI (não é falha da resposta).
            Line(new { type = "item.completed", item = new { id = "item_0", type = "error", message = "Model metadata not found" } }),
            Line(new { type = "item.completed", item = new { id = "item_1", type = "agent_message", text = """{"summary":"Cria uma programação.","businessRules":[],"validations":[],"entityChanges":[],"uncertainties":["x"]}""" } }),
            Line(new { type = "turn.completed", usage = new { input_tokens = 10 } }));

        var result = CodexProvider.ParseOutput(output, "").Deserialize<EndpointAnalysisResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("Cria uma programação.", result.Summary);
        Assert.Equal(["x"], result.Uncertainties);
    }

    [Fact]
    public void Reconexao_seguida_de_sucesso_nao_e_falha()
    {
        var output = string.Join("\n",
            Line(new { type = "error", message = "Reconnecting... 1/5 (stream disconnected)" }),
            Line(new { type = "item.completed", item = new { id = "item_0", type = "agent_message", text = """{"ok":true}""" } }),
            Line(new { type = "turn.completed" }));

        Assert.True(CodexProvider.ParseOutput(output, "").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Sessao_expirada_vira_falha_de_autenticacao_do_codex()
    {
        var output = string.Join("\n",
            Line(new { type = "error", message = "Reconnecting... 5/5 (unexpected status 401 Unauthorized: Missing bearer or basic authentication in header)" }),
            Failed("unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses"));

        var ex = Assert.Throws<AiProviderUnavailableException>(() => CodexProvider.ParseOutput(output, ""));

        Assert.Equal(AiProviders.Codex, ex.Provider);
        Assert.Equal(AiUnavailableReason.Authentication, ex.Reason);
        Assert.Contains("entre novamente com Codex", ex.Message);
    }

    [Fact]
    public void Limite_do_plano_vira_falha_de_limite_de_uso_com_a_mensagem_do_codex()
    {
        var api = Line(new { type = "error", status = 429, error = new { type = "usage_limit_reached", message = "You've hit your usage limit. Try again at 3:45 PM." } });

        var ex = Assert.Throws<AiProviderUnavailableException>(() => CodexProvider.ParseOutput(Failed(api), ""));

        Assert.Equal(AiUnavailableReason.UsageLimit, ex.Reason);
        Assert.Contains("Limite de uso do Codex atingido", ex.Message);
        Assert.Contains("Try again at 3:45 PM", ex.Message);
    }

    [Fact]
    public void Erro_da_api_do_codex_mostra_a_mensagem_interna()
    {
        var api = Line(new { type = "error", status = 400, error = new { type = "invalid_request_error", message = "The 'x' model is not supported when using Codex with a ChatGPT account." } });

        var ex = Assert.Throws<AiProviderException>(() => CodexProvider.ParseOutput(Failed(api), ""));

        Assert.Equal("Codex: The 'x' model is not supported when using Codex with a ChatGPT account. (HTTP 400)", ex.Message);
    }

    [Fact]
    public void Resposta_que_nao_e_json_vira_excecao()
    {
        var output = Line(new { type = "item.completed", item = new { id = "item_0", type = "agent_message", text = "Não consigo responder." } });

        Assert.Throws<AiProviderException>(() => CodexProvider.ParseOutput(output, ""));
        Assert.Throws<AiProviderException>(() => CodexProvider.ParseOutput("", ""));
    }

    [Fact]
    public void Credenciais_nao_aparecem_nas_mensagens_de_erro()
    {
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJhLWZhbHNhLTEyMzQ1Ng";
        var message = $"unexpected status 401 Unauthorized: Bearer {jwt}, refresh_token=rt_abc123def456, key sk-proj-abcdefghijklmnopqrstuv";

        var ex = Assert.Throws<AiProviderUnavailableException>(() => CodexProvider.ParseOutput(Failed(message), ""));

        Assert.DoesNotContain(jwt, ex.Message);
        Assert.DoesNotContain("rt_abc123def456", ex.Message);
        Assert.DoesNotContain("sk-proj-abcdefghijklmnopqrstuv", ex.Message);
    }

    [Fact]
    public void Exec_usa_o_schema_sandbox_somente_leitura_e_nao_grava_a_sessao()
    {
        var provider = new CodexProvider(new CodexOptions { Model = "gpt-teste", Effort = "high" });

        var arguments = provider.Arguments("schema.json", "dir");

        Assert.Equal("exec", arguments[0]);
        Assert.Equal("-", arguments[^1]);
        Assert.Equal("schema.json", arguments[arguments.ToList().IndexOf("--output-schema") + 1]);
        Assert.Equal("read-only", arguments[arguments.ToList().IndexOf("--sandbox") + 1]);
        Assert.Equal("gpt-teste", arguments[arguments.ToList().IndexOf("--model") + 1]);
        Assert.Contains("model_reasoning_effort=\"high\"", arguments);
        Assert.Contains("--ephemeral", arguments);
        Assert.Contains("--json", arguments);
        Assert.Equal("gpt-teste (Codex, effort high)", provider.Model);
        Assert.Equal(AiProviders.Codex, provider.Provider);
    }

    [Fact]
    public void Sem_modelo_escolhido_o_codex_usa_o_padrao_da_conta()
    {
        var provider = new CodexProvider(new CodexOptions { Model = "", Effort = "" });

        var arguments = provider.Arguments("schema.json", "dir");

        Assert.DoesNotContain("--model", arguments);
        Assert.DoesNotContain(arguments, a => a.StartsWith("model_reasoning_effort"));
    }

    [Fact]
    public void Prompt_leva_as_instrucoes_e_o_contexto()
    {
        var prompt = CodexProvider.BuildPrompt("Você documenta endpoints.", "Endpoint: POST /api/x");

        Assert.StartsWith("<instrucoes>", prompt);
        Assert.Contains("Você documenta endpoints.", prompt);
        Assert.EndsWith("Endpoint: POST /api/x", prompt);
        Assert.True(prompt.IndexOf("</instrucoes>", StringComparison.Ordinal) < prompt.IndexOf("Endpoint:", StringComparison.Ordinal));
    }

    [Fact]
    public void Le_conta_e_uso_dos_limites_do_app_server()
    {
        using var document = JsonDocument.Parse("""
            {"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":300,"resetsAt":1}},
             "rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":23,"windowDurationMins":300,"resetsAt":1791901297},
               "secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1792488097},"rateLimitReachedType":null}}}
            """);

        var usage = CodexAuth.ParseUsage(document.RootElement)!;

        Assert.Equal(23, usage.Primary!.UsedPercent);
        Assert.Equal(300, usage.Primary.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791901297), usage.Primary.ResetsAt);
        Assert.Equal(40, usage.Secondary!.UsedPercent);
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void Limite_atingido_aparece_no_uso()
    {
        using var reached = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":100},"rateLimitReachedType":"rate_limit_reached"}}""");
        using var blocked = JsonDocument.Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"primary":{"usedPercent":80}}}""");

        Assert.True(CodexAuth.ParseUsage(reached.RootElement)!.LimitReached);
        Assert.True(CodexAuth.ParseUsage(blocked.RootElement)!.LimitReached);
    }

    [Theory]
    [InlineData(1, "Not logged in", false, null)]
    [InlineData(0, "Logged in using ChatGPT", true, "chatgpt")]
    [InlineData(0, "Logged in using an API key - sk-proj-***", true, "apiKey")]
    public void Le_o_login_status_do_cli(int exitCode, string output, bool loggedIn, string? method)
    {
        var status = CodexAuth.ParseLoginStatus(exitCode, output);

        Assert.True(status.CliInstalled);
        Assert.Equal(loggedIn, status.LoggedIn);
        Assert.Equal(method, status.AuthMethod);
    }

    [Fact]
    public void Catalogo_ignora_modelos_ocultos_e_le_os_efforts()
    {
        using var document = JsonDocument.Parse("""
            {"data":[
              {"id":"gpt-a","model":"gpt-a","displayName":"GPT-A","isDefault":true,"hidden":false,"defaultReasoningEffort":"low",
               "supportedReasoningEfforts":[{"reasoningEffort":"low","description":""},{"reasoningEffort":"high","description":""}]},
              {"id":"gpt-oculto","model":"gpt-oculto","displayName":"Oculto","isDefault":false,"hidden":true,"defaultReasoningEffort":"low","supportedReasoningEfforts":[]}
            ],"nextCursor":null}
            """);

        var model = Assert.Single(CodexModelCatalog.ParseModels(document.RootElement));

        Assert.Equal("gpt-a", model.Id);
        Assert.Equal("GPT-A", model.Name);
        Assert.Equal(["low", "high"], model.Efforts);
        Assert.Equal("low", model.DefaultEffort);
        Assert.True(model.IsDefault);
    }

    [Fact]
    public async Task Sem_modelo_escolhido_usa_o_padrao_da_conta_e_um_effort_aceito()
    {
        var options = new CodexOptions { Model = "", Effort = "xhigh" };
        IReadOnlyList<CodexModelInfo> models =
        [
            new("gpt-b", "GPT-B", true, null, ["low", "medium"], "medium", false),
            new("gpt-a", "GPT-A", true, null, ["low", "high"], "low", true),
        ];
        var catalog = new CodexModelCatalog(options, TimeProvider.System, _ => Task.FromResult(models));

        await catalog.ResolveSelectionAsync(new CodexAuthStatus(true, true, "chatgpt", "a@b.c", "plus"));

        Assert.Equal("gpt-a", options.Model);
        Assert.Equal("low", options.Effort);
    }

    [Fact]
    public async Task Sem_codex_instalado_status_indica_e_o_roteador_nao_escolhe_o_codex()
    {
        var options = new CodexOptions
        {
            Executable = "codex-inexistente-" + Guid.NewGuid().ToString("N"),
            HomeDirectory = Path.Combine(Path.GetTempPath(), "codex-home-" + Guid.NewGuid().ToString("N")),
        };
        var auth = new CodexAuth(options);
        var claude = new ClaudeCodeOptions { Executable = "claude-inexistente-" + Guid.NewGuid().ToString("N") };
        var router = new AiProviderRouter(new ClaudeCodeProvider(claude), new ClaudeCodeAuth(claude),
            codex: new CodexProvider(options), codexAuth: auth, codexModels: new CodexModelCatalog(options));

        var status = await auth.GetStatusAsync();

        Assert.False(status.CliInstalled);
        Assert.False(status.LoggedIn);
        Assert.Null(await router.SelectAsync(AiProviders.Codex));
        Assert.Null(await router.SelectAsync(null));
        await Assert.ThrowsAsync<ArgumentException>(() => router.SelectAsync("gemini"));
        var notFound = Assert.Throws<CodexNotInstalledException>(() => CodexExecutable.Resolve(options.Executable));
        Assert.Contains("PATH", notFound.Message);
    }

    [Fact]
    public async Task Falha_de_conta_interrompe_a_validacao_em_runtime_e_outras_falhas_nao()
    {
        var report = new RuntimeValidation();
        var unavailable = new RuntimeAi(new ThrowingAi(new AiProviderUnavailableException(AiProviders.Codex, AiUnavailableReason.UsageLimit, "limite")), report, _ => { });
        var failing = new RuntimeAi(new ThrowingAi(new AiProviderException("resposta inválida")), report, _ => { });
        var schema = new Dictionary<string, JsonElement>();

        await Assert.ThrowsAsync<AiProviderUnavailableException>(() => unavailable.AskAsync("planejamento", "p", schema, CancellationToken.None));
        Assert.Null(await failing.AskAsync("planejamento", "p", schema, CancellationToken.None));
        Assert.Contains(report.Notes, n => n.Contains("resposta inválida"));
    }

    [Fact]
    public async Task Executa_o_codex_com_o_login_do_projeto_e_o_prompt_pelo_stdin()
    {
        if (!FakeCodex.NodeAvailable) return;
        using var fake = new FakeCodex("ok");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-teste-nao-deve-vazar-1234567890");
        try
        {
            var result = await new CodexProvider(fake.Options).CompleteJsonAsync(new AiJsonRequest("teste", "Instruções do sistema.", "Pergunta do usuário.",
                new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") }));

            Assert.Equal("sim", result.GetProperty("ok").GetString());
            var capture = fake.Capture();
            Assert.Equal(fake.Options.HomeDirectory, capture.GetProperty("home").GetString());
            Assert.Equal(JsonValueKind.Null, capture.GetProperty("apiKey").ValueKind);
            Assert.Contains("Instruções do sistema.", capture.GetProperty("input").GetString());
            Assert.Contains("Pergunta do usuário.", capture.GetProperty("input").GetString());
            Assert.Equal("""{"type":"object"}""", capture.GetProperty("schema").GetString());
            // O schema temporário é apagado depois da chamada.
            Assert.False(File.Exists(capture.GetProperty("schemaFile").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public async Task Limite_de_uso_no_codex_exec_chega_como_falha_tipada()
    {
        if (!FakeCodex.NodeAvailable) return;
        using var fake = new FakeCodex("limit");

        var ex = await Assert.ThrowsAsync<AiProviderUnavailableException>(() => new CodexProvider(fake.Options)
            .CompleteJsonAsync(new AiJsonRequest("teste", "s", "u", new Dictionary<string, JsonElement>())));

        Assert.Equal(AiUnavailableReason.UsageLimit, ex.Reason);
    }

    [Fact]
    public async Task Cancelar_a_analise_encerra_o_processo_do_codex()
    {
        if (!FakeCodex.NodeAvailable) return;
        using var fake = new FakeCodex("hang");
        using var cancel = new CancellationTokenSource();

        var run = new CodexProvider(fake.Options).CompleteJsonAsync(new AiJsonRequest("teste", "s", "u", new Dictionary<string, JsonElement>()), cancel.Token);
        var pid = await fake.WaitForPidAsync();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(Exited(pid));
    }

    private static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class ThrowingAi(Exception exception) : IAiProvider
    {
        public string Provider => AiProviders.Codex;

        public string Model => "throwing";

        public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
            Task.FromException<EndpointAnalysisResult>(exception);

        public Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<JsonElement>(exception);
    }

    /// <summary>
    /// Codex falso: um script node atrás de um shim do npm (o mesmo caminho de uma instalação via npm). Registra o que
    /// recebeu (argumentos, stdin, CODEX_HOME, OPENAI_API_KEY e o schema) e responde como o `codex exec --json`.
    /// </summary>
    private sealed class FakeCodex : IDisposable
    {
        private const string Script = """
            const fs = require("fs"), path = require("path");
            const args = process.argv.slice(2), mode = fs.readFileSync(path.join(__dirname, "mode.txt"), "utf8").trim();
            fs.writeFileSync(path.join(__dirname, "pid.txt"), String(process.pid));
            let input = "";
            process.stdin.setEncoding("utf8");
            process.stdin.on("data", d => input += d);
            process.stdin.on("end", () => {
              const schemaFile = args[args.indexOf("--output-schema") + 1];
              fs.writeFileSync(path.join(__dirname, "capture.json"), JSON.stringify({
                args, input, schemaFile, home: process.env.CODEX_HOME, apiKey: process.env.OPENAI_API_KEY ?? null,
                schema: fs.readFileSync(schemaFile, "utf8") }));
              const line = (o) => process.stdout.write(JSON.stringify(o) + "\n");
              if (mode === "hang") { setInterval(() => {}, 1000); return; }
              if (mode === "limit") {
                line({ type: "turn.failed", error: { message: JSON.stringify({ type: "error", status: 429,
                  error: { type: "usage_limit_reached", message: "You've hit your usage limit." } }) } });
                process.exit(1);
              }
              line({ type: "thread.started", thread_id: "t" });
              line({ type: "item.completed", item: { id: "item_0", type: "agent_message", text: JSON.stringify({ ok: "sim" }) } });
              line({ type: "turn.completed", usage: {} });
            });
            """;

        private readonly string _directory = Directory.CreateTempSubdirectory("fake-codex-").FullName;

        public FakeCodex(string mode)
        {
            File.WriteAllText(Path.Combine(_directory, "fake-codex.js"), Script);
            File.WriteAllText(Path.Combine(_directory, "mode.txt"), mode);
            var shim = Path.Combine(_directory, "codex.cmd");
            File.WriteAllText(shim, "@ECHO off\r\n\"%_prog%\"  \"%dp0%\\fake-codex.js\" %*\r\n");
            Options = new CodexOptions { Executable = shim, HomeDirectory = Path.Combine(_directory, "home"), Model = "gpt-teste", Effort = "high", TimeoutMinutes = 1 };
        }

        public static bool NodeAvailable => OperatingSystem.IsWindows()
            && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d.Trim('"'), "node.exe")));

        public CodexOptions Options { get; }

        public JsonElement Capture()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "capture.json")));
            return document.RootElement.Clone();
        }

        public async Task<int> WaitForPidAsync()
        {
            var file = Path.Combine(_directory, "capture.json");
            for (var i = 0; i < 200 && !File.Exists(file); i++) await Task.Delay(50);
            return int.Parse(File.ReadAllText(Path.Combine(_directory, "pid.txt")));
        }

        public void Dispose()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(200);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
