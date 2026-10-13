using System.Text.Json;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Tests;

/// <summary>Várias contas do Codex e a continuação entre provedores: contas do Claude → contas do Codex (e o inverso).</summary>
[Collection(SampleSolutionCollection.Name)]
public class CodexAccountsTests(SampleSolutionFixture fixture) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codex-accounts-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Contas_do_codex_tem_logins_separados_ordem_persistida_e_remocao_segura()
    {
        var options = new CodexOptions
        {
            Executable = "codex-inexistente-" + Guid.NewGuid().ToString("N"),
            HomeDirectory = Path.Combine(_directory, "principal"),
            AccountsDirectory = Path.Combine(_directory, "accounts"),
        };
        var availability = new AiAvailability();
        var accounts = new CodexAccounts(options, availability);
        var primary = Assert.Single(accounts.All);
        Assert.Equal((CodexAccounts.PrimaryId, options.HomeDirectory, "codex:principal"), (primary.Account.Id, primary.Account.Directory, primary.Key));

        var second = accounts.Add("Trabalho");
        Assert.StartsWith(options.AccountsDirectory, second.Account.Directory);
        Assert.NotSame(primary.Auth, second.Auth);
        accounts.Reorder([second.Account.Id, CodexAccounts.PrimaryId]);
        var reloaded = new CodexAccounts(options, availability);
        Assert.Equal([second.Account.Id, CodexAccounts.PrimaryId], reloaded.All.Select(a => a.Account.Id));
        Assert.Equal(1, reloaded.OrderOf(reloaded.Get(second.Account.Id)));

        var states = await reloaded.DescribeAsync(refresh: false);
        Assert.All(states, s => Assert.Equal(AiAccountStates.LoggedOut, s.State));
        Assert.Equal(["Trabalho", "Conta principal"], states.Select(s => s.Display));

        Directory.CreateDirectory(second.Account.Directory);
        Directory.CreateDirectory(options.HomeDirectory);
        await reloaded.RemoveAsync(second.Account.Id);
        await reloaded.RemoveAsync(CodexAccounts.PrimaryId);
        Assert.False(Directory.Exists(second.Account.Directory));
        Assert.True(Directory.Exists(options.HomeDirectory)); // o login padrão (Codex:HomeDirectory) fica no disco
    }

    [Fact]
    public void Opcao_de_continuar_no_outro_provedor_vale_para_os_dois_e_persiste()
    {
        var file = Path.Combine(_directory, "ai-settings.json");
        var settings = new AiProviderSettings(file);
        Assert.True(settings.ContinueWithOtherProvider); // padrão
        settings.ContinueWithOtherProvider = false;
        Assert.False(new AiProviderSettings(file).ContinueWithOtherProvider);
    }

    private const string FakeClaudeScript = """
        const fs = require("fs"), path = require("path");
        const dir = process.env.CLAUDE_CONFIG_DIR, args = process.argv.slice(2);
        const read = (name) => { const f = path.join(dir, name); return fs.existsSync(f) ? fs.readFileSync(f, "utf8").trim() : ""; };
        if (args[0] === "auth" && args[1] === "status") {
          const email = read("email.txt");
          process.stdout.write(JSON.stringify(email ? { loggedIn: true, authMethod: "claude.ai", email, subscriptionType: "max" } : { loggedIn: false }));
        } else if (args[0] !== "auth") {
          let input = "";
          process.stdin.on("data", d => input += d).on("end", () => {
            fs.appendFileSync(path.join(dir, "prompts.log"), JSON.stringify(input) + "\n");
            process.stdout.write(JSON.stringify({ type: "result", is_error: true, result: "Claude AI usage limit reached|1760400000" }));
          });
        }
        """;

    /// <summary>
    /// Codex falso conforme o CODEX_HOME: app-server (conta, uso, modelos) e exec (limite ou resposta), registrando cada
    /// pedido em prompts.log.
    /// </summary>
    private const string FakeCodexScript = """
        const fs = require("fs"), path = require("path"), readline = require("readline");
        const home = process.env.CODEX_HOME, args = process.argv.slice(2);
        const read = (name) => { const f = path.join(home, name); return fs.existsSync(f) ? fs.readFileSync(f, "utf8").trim() : ""; };
        const line = (o) => process.stdout.write(JSON.stringify(o) + "\n");
        if (args[0] === "app-server") {
          readline.createInterface({ input: process.stdin }).on("line", (text) => {
            const m = JSON.parse(text);
            if (m.id === undefined) return;
            const email = read("email.txt"), reply = (result) => line({ id: m.id, result });
            if (m.method === "initialize") reply({});
            else if (m.method === "account/read") reply({ account: email ? { type: "chatgpt", email, planType: "plus" } : null });
            else if (m.method === "account/rateLimits/read") reply({ rateLimits: { primary: { usedPercent: 10, windowDurationMins: 300 } } });
            else if (m.method === "model/list") reply({ data: [{ model: "gpt-teste", displayName: "GPT teste", isDefault: true,
              supportedReasoningEfforts: [{ reasoningEffort: "high" }], defaultReasoningEffort: "high" }] });
            else line({ id: m.id, error: { message: "não suportado" } });
          });
        } else if (args[0] === "exec") {
          let input = "";
          process.stdin.setEncoding("utf8");
          process.stdin.on("data", d => input += d).on("end", () => {
            fs.appendFileSync(path.join(home, "prompts.log"), JSON.stringify(input) + "\n");
            if (read("mode.txt") === "limit") {
              line({ type: "turn.failed", error: { message: JSON.stringify({ type: "error", status: 429,
                error: { type: "usage_limit_reached", message: "You've hit your usage limit." } }) } });
              process.exit(1);
            }
            const answer = { summary: "Documentado por " + read("email.txt"), businessRules: [], validations: [], entityChanges: [], uncertainties: [] };
            line({ type: "thread.started", thread_id: "t" });
            line({ type: "item.completed", item: { id: "item_0", type: "agent_message", text: JSON.stringify(answer) } });
            line({ type: "turn.completed", usage: {} });
          });
        }
        """;

    private static bool NodeAvailable => OperatingSystem.IsWindows()
        && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d.Trim('"'), "node.exe")));

    private string Shim(string name, string script)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_directory, "bin-" + name)).FullName;
        File.WriteAllText(Path.Combine(bin, $"fake-{name}.js"), script);
        var shim = Path.Combine(bin, $"{name}.cmd");
        File.WriteAllText(shim, $"@ECHO off\r\n\"%_prog%\"  \"%dp0%\\fake-{name}.js\" %*\r\n");
        return shim;
    }

    private static void Account(string directory, string email, string mode)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "email.txt"), email);
        File.WriteAllText(Path.Combine(directory, "mode.txt"), mode);
    }

    private static string[] Prompts(string directory) =>
        File.Exists(Path.Combine(directory, "prompts.log")) ? File.ReadAllLines(Path.Combine(directory, "prompts.log")).Select(l => JsonSerializer.Deserialize<string>(l)!).ToArray() : [];

    [Fact]
    public async Task Contas_do_claude_esgotadas_continuam_nas_contas_do_codex_ate_uma_com_uso()
    {
        if (!NodeAvailable) return;
        var claudeShim = Shim("claude", FakeClaudeScript);
        var codexShim = Shim("codex", FakeCodexScript);
        using var services = new ServiceCollection()
            .AddEndpointAnalyzer(
                configureClaudeCode: o =>
                {
                    o.Executable = claudeShim;
                    o.ConfigDirectory = Path.Combine(_directory, "claude-principal");
                    o.AccountsDirectory = Path.Combine(_directory, "claude-accounts");
                },
                configureRuntime: o => { o.Enabled = false; o.AnalysisDatabasePath = Path.Combine(_directory, "db", "analyses.db"); },
                configureCodex: o =>
                {
                    o.Executable = codexShim;
                    o.HomeDirectory = Path.Combine(_directory, "codex-principal");
                    o.AccountsDirectory = Path.Combine(_directory, "codex-accounts");
                    o.TimeoutMinutes = 1;
                })
            .AddSingleton(new AnalysisCache(Path.Combine(_directory, "cache")))
            .BuildServiceProvider();
        var claude = services.GetRequiredService<ClaudeAccounts>();
        var codex = services.GetRequiredService<CodexAccounts>();
        var claude2 = claude.Add(null);
        var codex2 = codex.Add(null);
        Account(Path.Combine(_directory, "claude-principal"), "claude1@empresa.com", "limit");
        Account(claude2.Account.Directory, "claude2@empresa.com", "limit");
        Account(Path.Combine(_directory, "codex-principal"), "codex1@empresa.com", "limit");
        Account(codex2.Account.Directory, "codex2@empresa.com", "ok");
        var router = services.GetRequiredService<AiProviderRouter>();
        router.Mode = AiMode.Subscription;

        // Ordem: o provedor escolhido primeiro, cada um com as contas na ordem cadastrada.
        Assert.Equal(["claude:principal", claude2.Key, "codex:principal", codex2.Key], (await router.CandidatesAsync(AiProviders.Claude)).Select(c => c.Key));
        Assert.Equal(["codex:principal", codex2.Key, "claude:principal", claude2.Key], (await router.CandidatesAsync(AiProviders.Codex)).Select(c => c.Key));
        services.GetRequiredService<AiProviderSettings>().ContinueWithOtherProvider = false;
        Assert.Equal(["codex:principal", codex2.Key], (await router.CandidatesAsync(AiProviders.Codex)).Select(c => c.Key));
        services.GetRequiredService<AiProviderSettings>().ContinueWithOtherProvider = true;

        var service = services.GetRequiredService<EndpointAnalysisService>();
        var endpoint = await service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");
        var report = await service.AnalyzeAsync(fixture.SolutionPath, endpoint, sections: AnalysisSections.Summary,
            aiSections: AnalysisSections.Summary, aiProvider: AiProviders.Claude);

        Assert.Equal("Documentado por codex2@empresa.com", report.Ai!.Summary);
        Assert.Equal(AiProviders.Codex, report.Version.AiProvider);
        Assert.Equal(["claude1@empresa.com", "claude2@empresa.com", "codex1@empresa.com", "codex2@empresa.com"],
            report.AiRun!.Accounts.Select(a => a.Label));
        Assert.Equal(["claude1@empresa.com → claude2@empresa.com", "claude2@empresa.com → codex1@empresa.com", "codex1@empresa.com → codex2@empresa.com"],
            report.AiRun.Handoffs.Select(h => $"{h.FromAccount} → {h.ToAccount}"));
        Assert.All(report.AiRun.Handoffs, h => Assert.Equal(AiHandoffReasons.UsageLimit, h.Reason));
        Assert.Null(report.AiRun.Interruption);

        // Cada conta recebeu uma vez o mesmo pedido (no Codex, as instruções vão na própria mensagem, antes do contexto).
        var request = Assert.Single(Prompts(Path.Combine(_directory, "claude-principal")));
        Assert.Equal([request], Prompts(claude2.Account.Directory));
        Assert.Contains(request, Assert.Single(Prompts(Path.Combine(_directory, "codex-principal"))));
        Assert.Contains(request, Assert.Single(Prompts(codex2.Account.Directory)));

        // A próxima análise começa direto na conta com uso, sem gastar chamadas nas que recusaram.
        Assert.Equal([AiAccountStates.NoUsage, AiAccountStates.Available], (await codex.DescribeAsync(refresh: false)).Select(s => s.State));
        using var next = (FailoverAiProvider)(await router.SelectAsync(AiProviders.Claude))!;
        Assert.Equal("codex2@empresa.com", next.Current.Label);
        Assert.Equal(3, next.Run.Notes.Count);
    }
}
