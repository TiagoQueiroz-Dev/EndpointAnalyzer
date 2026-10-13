using System.Text.Json;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;

namespace EndpointAnalyzer.Tests;

public class ClaudeCodeUsageTests
{
    // Formato do get_usage do Claude Code (resumido): limites do plano em rate_limits.
    private const string Response = """
        {"session":{"total_cost_usd":0,"model_usage":{}},"subscription_type":"team","rate_limits_available":true,
         "rate_limits":{
           "five_hour":{"utilization":30,"resets_at":"2026-10-13T13:50:00.342331+00:00","locked_reason":null},
           "seven_day":{"utilization":44.4,"resets_at":"2026-10-13T21:00:00.342352+00:00","locked_reason":null},
           "seven_day_opus":null,
           "limits":[{"kind":"session","group":"session","percent":30,"resets_at":"2026-10-13T13:50:00+00:00"},
                     {"kind":"weekly_all","group":"weekly","percent":44,"resets_at":"2026-10-13T21:00:00+00:00"}]},
         "behaviors":null}
        """;

    private static readonly ClaudeAuthStatus Subscription = new(true, true, "claude.ai", "a@b.c", "team", "Org");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Le_o_uso_da_sessao_e_da_semana()
    {
        var usage = ClaudeCodeUsage.Parse(Json(Response))!;

        Assert.Equal(30, usage.Primary!.UsedPercent);
        Assert.Equal(300, usage.Primary.WindowMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T13:50:00.342331+00:00"), usage.Primary.ResetsAt);
        Assert.Equal(44, usage.Secondary!.UsedPercent);
        Assert.Equal(10080, usage.Secondary.WindowMinutes);
        Assert.False(usage.LimitReached);
    }

    [Fact]
    public void Sem_limites_do_plano_nao_ha_uso()
    {
        Assert.Null(ClaudeCodeUsage.Parse(Json("""{"rate_limits_available":false,"rate_limits":null}""")));
        Assert.Null(ClaudeCodeUsage.Parse(Json("""{"session":{}}""")));
    }

    [Fact]
    public void Limite_esgotado_ou_bloqueado_aparece_no_uso()
    {
        var full = ClaudeCodeUsage.Parse(Json("""{"rate_limits":{"five_hour":{"utilization":100},"seven_day":{"utilization":60}}}"""))!;
        var locked = ClaudeCodeUsage.Parse(Json("""{"rate_limits":{"five_hour":{"utilization":20},"seven_day":{"utilization":60,"locked_reason":"weekly_limit"}}}"""))!;

        Assert.True(full.LimitReached);
        Assert.True(locked.LimitReached);
    }

    [Fact]
    public void Sem_os_campos_principais_usa_a_lista_agrupada()
    {
        var usage = ClaudeCodeUsage.Parse(Json("""
            {"rate_limits":{"limits":[{"kind":"session","percent":12,"resets_at":"2026-10-13T13:50:00+00:00"},{"kind":"weekly_all","percent":7}]}}
            """))!;

        Assert.Equal(12, usage.Primary!.UsedPercent);
        Assert.Equal(7, usage.Secondary!.UsedPercent);
        Assert.Null(usage.Secondary.ResetsAt);
    }

    [Fact]
    public async Task So_consulta_com_a_conta_do_claude_e_guarda_por_um_minuto()
    {
        var reads = 0;
        var clock = new ManualClock();
        var meter = new ClaudeCodeUsage(new ClaudeCodeOptions(), clock, _ =>
        {
            reads++;
            return Task.FromResult<AiUsage?>(new AiUsage(new AiUsageWindow(reads, 300, null), null, false));
        });

        Assert.Null(await meter.GetAsync(new ClaudeAuthStatus(true, true, "console")));
        Assert.Equal(0, reads);

        Assert.Equal(1, (await meter.GetAsync(Subscription))!.Primary!.UsedPercent);
        Assert.Equal(1, (await meter.GetAsync(Subscription))!.Primary!.UsedPercent);
        Assert.Equal(2, (await meter.GetAsync(Subscription, refresh: true))!.Primary!.UsedPercent);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(3, (await meter.GetAsync(Subscription))!.Primary!.UsedPercent);
    }

    [Fact]
    public async Task Consulta_o_uso_pelo_protocolo_de_controle_sem_prompt_ao_modelo()
    {
        if (!FakeClaude.NodeAvailable) return;
        using var fake = new FakeClaude(Response);

        var usage = await new ClaudeCodeUsage(fake.Options).GetAsync(Subscription, refresh: true);

        Assert.Equal(30, usage!.Primary!.UsedPercent);
        var requests = fake.Requests();
        Assert.Equal(["initialize", "get_usage"], requests.Select(r => r.GetProperty("subtype").GetString()));
        Assert.True(requests[1].GetProperty("skip_behaviors").GetBoolean());
        // Nenhuma mensagem de usuário (prompt) foi enviada.
        Assert.Equal(2, fake.Lines());
    }

    [Fact]
    public async Task Erro_do_get_usage_vira_excecao_com_a_mensagem()
    {
        if (!FakeClaude.NodeAvailable) return;
        using var fake = new FakeClaude(error: "get_usage is not supported in this context");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ClaudeCodeUsage(fake.Options).GetAsync(Subscription, refresh: true));

        Assert.Contains("get_usage is not supported", ex.Message);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>
    /// Claude Code falso atrás de um shim do npm: responde ao protocolo de controle (initialize, get_usage) e registra as
    /// linhas recebidas no stdin.
    /// </summary>
    private sealed class FakeClaude : IDisposable
    {
        private const string Script = """
            const fs = require("fs"), path = require("path"), readline = require("readline");
            const usage = fs.readFileSync(path.join(__dirname, "usage.json"), "utf8"), error = fs.readFileSync(path.join(__dirname, "error.txt"), "utf8");
            const log = path.join(__dirname, "stdin.jsonl");
            readline.createInterface({ input: process.stdin }).on("line", (line) => {
              fs.appendFileSync(log, line + "\n");
              const m = JSON.parse(line);
              if (m.type !== "control_request") return;
              const reply = (response) => process.stdout.write(JSON.stringify({ type: "control_response", response }) + "\n");
              const id = m.request_id;
              if (m.request.subtype === "initialize") reply({ subtype: "success", request_id: id, response: { models: [] } });
              else if (error) reply({ subtype: "error", request_id: id, error });
              else reply({ subtype: "success", request_id: id, response: JSON.parse(usage) });
            });
            """;

        private readonly string _directory = Directory.CreateTempSubdirectory("fake-claude-").FullName;

        public FakeClaude(string usage = "{}", string error = "")
        {
            File.WriteAllText(Path.Combine(_directory, "fake-claude.js"), Script);
            File.WriteAllText(Path.Combine(_directory, "usage.json"), usage);
            File.WriteAllText(Path.Combine(_directory, "error.txt"), error);
            var shim = Path.Combine(_directory, "claude.cmd");
            File.WriteAllText(shim, "@ECHO off\r\n\"%_prog%\"  \"%dp0%\\fake-claude.js\" %*\r\n");
            Options = new ClaudeCodeOptions { Executable = shim, ConfigDirectory = Path.Combine(_directory, "config") };
        }

        public static bool NodeAvailable => OperatingSystem.IsWindows()
            && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d.Trim('"'), "node.exe")));

        public ClaudeCodeOptions Options { get; }

        private string[] StdinLines() => File.ReadAllLines(Path.Combine(_directory, "stdin.jsonl"));

        public int Lines() => StdinLines().Length;

        public List<JsonElement> Requests() => StdinLines()
            .Select(Json)
            .Where(m => m.GetProperty("type").GetString() == "control_request")
            .Select(m => m.GetProperty("request"))
            .ToList();

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
