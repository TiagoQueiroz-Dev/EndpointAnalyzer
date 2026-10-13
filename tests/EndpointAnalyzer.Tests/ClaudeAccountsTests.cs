using System.Text.Json;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Tests;

/// <summary>Várias contas Claude: troca automática no limite de uso, ordem, logins separados e progresso preservado.</summary>
[Collection(SampleSolutionCollection.Name)]
public class ClaudeAccountsTests(SampleSolutionFixture fixture) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("claude-accounts-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static AiProviderUnavailableException Limit(string provider = AiProviders.Claude) =>
        new(provider, AiUnavailableReason.UsageLimit, "Claude AI usage limit reached");

    /// <summary>Provedor roteirizado: registra os pedidos e falha (ou responde) conforme a etapa.</summary>
    private sealed class ScriptedAi(string model, Func<string, Exception?>? fail = null, int delayMs = 0) : IAiProvider
    {
        public List<string> Prompts { get; } = [];

        public string Provider => AiProviders.Claude;

        public string Model => model;

        public async Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default)
        {
            await Record("documentação", PromptBuilder.BuildUserPrompt(context), cancellationToken);
            return new EndpointAnalysisResult { Summary = $"Documentado por {model}" };
        }

        public async Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default)
        {
            await Record(request.Purpose, request.UserPrompt, cancellationToken);
            return JsonSerializer.SerializeToElement(new { por = model });
        }

        private async Task Record(string step, string prompt, CancellationToken cancellationToken)
        {
            lock (Prompts) Prompts.Add(prompt);
            if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
            if (fail?.Invoke(step) is { } error) throw error;
        }
    }

    private static AiCandidate Candidate(string id, IAiProvider ai) => new(ClaudeAccounts.KeyOf(id), AiProviders.Claude, id, $"{id}@empresa.com", () => ai);

    private static AiJsonRequest Ask(string step, string prompt = "contexto do endpoint + evidências + resultado parcial") =>
        new(step, "instruções", prompt, new Dictionary<string, JsonElement>());

    [Fact]
    public async Task Limite_de_uso_repete_so_a_chamada_que_falhou_na_proxima_conta_com_o_mesmo_pedido()
    {
        var availability = new AiAvailability();
        var a = new ScriptedAi("modelo", _ => Limit());
        var b = new ScriptedAi("modelo");
        using var ai = FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], availability);

        var first = await ai.CompleteJsonAsync(Ask("adquirir dados (rodada 1)"));
        var second = await ai.CompleteJsonAsync(Ask("explorar cenários", "outro pedido"));

        Assert.Equal("modelo", first.GetProperty("por").GetString());
        Assert.Equal(["contexto do endpoint + evidências + resultado parcial"], a.Prompts); // a conta sem uso não é tentada de novo
        Assert.Equal(["contexto do endpoint + evidências + resultado parcial", "outro pedido"], b.Prompts);
        var handoff = Assert.Single(ai.Run.Handoffs);
        Assert.Equal(("a@empresa.com", "b@empresa.com", AiHandoffReasons.UsageLimit, "adquirir dados (rodada 1)"),
            (handoff.FromAccount, handoff.ToAccount, handoff.Reason, handoff.Step));
        Assert.Equal([("a@empresa.com", 0), ("b@empresa.com", 2)], ai.Run.Accounts.Select(x => (x.Label, x.Calls)));
        Assert.Equal(AiUnavailableReason.UsageLimit, availability.Blocked(ClaudeAccounts.KeyOf("a"))!.Reason);
        Assert.Equal("b@empresa.com", ai.Current.Label);
        Assert.Equal(2, ai.SuccessfulCalls);
    }

    [Fact]
    public async Task Sessao_expirada_tambem_troca_mas_outras_falhas_nao()
    {
        var availability = new AiAvailability();
        var expired = new ScriptedAi("m", _ => new AiProviderUnavailableException(AiProviders.Claude, AiUnavailableReason.Authentication, "Not logged in"));
        var b = new ScriptedAi("m");
        using (var ai = FailoverAiProvider.Start([Candidate("a", expired), Candidate("b", b)], availability))
        {
            await ai.CompleteJsonAsync(Ask("planejar"));
            Assert.Equal(AiHandoffReasons.Authentication, Assert.Single(ai.Run.Handoffs).Reason);
            Assert.Equal(AiUnavailableReason.Authentication, availability.Blocked(ClaudeAccounts.KeyOf("a"))!.Reason);
        }

        var broken = new ScriptedAi("m", _ => new AiProviderException("resposta inválida"));
        var untouched = new ScriptedAi("m");
        using var other = FailoverAiProvider.Start([Candidate("c", broken), Candidate("d", untouched)], availability);
        await Assert.ThrowsAsync<AiProviderException>(() => other.CompleteJsonAsync(Ask("planejar")));
        Assert.Empty(untouched.Prompts);
        Assert.Empty(other.Run.Handoffs);
        Assert.True(availability.IsAvailable(ClaudeAccounts.KeyOf("c")));
    }

    [Fact]
    public async Task Sem_proxima_conta_a_falha_lista_as_contas_e_a_proxima_analise_nao_as_repete()
    {
        var clock = new ManualClock();
        var availability = new AiAvailability(clock);
        var a = new ScriptedAi("m", _ => Limit());
        var b = new ScriptedAi("m", _ => Limit());
        using (var ai = FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], availability))
        {
            var error = await Assert.ThrowsAsync<AiProviderUnavailableException>(() => ai.CompleteJsonAsync(Ask("planejar")));
            Assert.Equal(AiUnavailableReason.UsageLimit, error.Reason);
            Assert.Contains("a@empresa.com", error.Message);
            Assert.Contains("b@empresa.com", error.Message);
            Assert.Contains("preservado", error.Message);
            Assert.True(ai.Exhausted);
            Assert.Null(ai.Run.Handoffs[^1].ToAccount);
        }

        // Outra análise logo depois não gasta chamadas nas contas que já recusaram.
        Assert.Throws<AiProviderUnavailableException>(() => FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], availability));
        Assert.Single(a.Prompts);
        Assert.Single(b.Prompts);

        // Passado o bloqueio, a conta volta a ser tentada.
        clock.Advance(AiAvailability.DefaultBlock + TimeSpan.FromMinutes(1));
        using var later = FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], availability);
        Assert.Equal("a@empresa.com", later.Current.Label);
    }

    [Fact]
    public async Task Conta_bloqueada_e_pulada_no_inicio_e_chamadas_paralelas_trocam_uma_vez()
    {
        var availability = new AiAvailability();
        availability.Block(ClaudeAccounts.KeyOf("a"), AiUnavailableReason.UsageLimit, "limite", DateTimeOffset.UtcNow.AddHours(2));
        var a = new ScriptedAi("m");
        var b = new ScriptedAi("m", _ => Limit(), delayMs: 50);
        var c = new ScriptedAi("m");
        using var ai = FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b), Candidate("c", c)], availability);
        Assert.Equal("b@empresa.com", ai.Current.Label);
        Assert.Contains(ai.Run.Notes, n => n.Contains("a@empresa.com") && n.Contains("pulada"));

        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => ai.CompleteJsonAsync(Ask($"etapa {i}"))));

        Assert.Empty(a.Prompts);
        Assert.Equal(3, c.Prompts.Count);
        Assert.Equal("c@empresa.com", Assert.Single(ai.Run.Handoffs).ToAccount);
    }

    [Fact]
    public async Task Cancelar_durante_a_troca_nao_chama_a_proxima_conta()
    {
        using var cancel = new CancellationTokenSource();
        var a = new ScriptedAi("m", _ =>
        {
            cancel.Cancel();
            return Limit();
        });
        var b = new ScriptedAi("m");
        using var ai = FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], new AiAvailability());

        await Assert.ThrowsAnyAsync<Exception>(() => ai.CompleteJsonAsync(Ask("planejar"), cancel.Token));
        Assert.Empty(b.Prompts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ai.CompleteJsonAsync(Ask("planejar"), cancel.Token));
        Assert.Empty(b.Prompts);
    }

    [Fact]
    public async Task Acompanhamento_mostra_a_conta_em_uso_e_as_trocas_enquanto_a_analise_roda()
    {
        var running = new RunningAnalyses();
        var solution = Path.Combine(_directory, "Api.sln");
        var availability = new AiAvailability();
        using (var analysis = running.Start(solution, "Api", "POST /api/x"))
        {
            using var ai = FailoverAiProvider.Start([Candidate("a", new ScriptedAi("m", _ => Limit())), Candidate("b", new ScriptedAi("m"))], availability);
            analysis.Progress.Track(ai);
            Assert.Equal("a@empresa.com", running.Find(solution, "Api", "POST /api/x")!.Snapshot().Account);
            Assert.True(availability.InUse(ClaudeAccounts.KeyOf("a")));

            await ai.CompleteJsonAsync(Ask("planejar"));
            var snapshot = running.Find(solution, "Api", "POST /api/x")!.Snapshot();
            Assert.Equal(("b@empresa.com", AiProviders.Claude), (snapshot.Account, snapshot.Provider));
            Assert.Equal("a@empresa.com", Assert.Single(snapshot.Handoffs).FromAccount);
            Assert.False(availability.InUse(ClaudeAccounts.KeyOf("a")));
            Assert.True(availability.InUse(ClaudeAccounts.KeyOf("b")));
        }
        Assert.Null(running.Find(solution, "Api", "POST /api/x"));
        Assert.False(availability.InUse(ClaudeAccounts.KeyOf("b")));
    }

    private ClaudeCodeOptions Options(string executable = "claude-inexistente") => new()
    {
        Executable = executable == "claude-inexistente" ? executable + Guid.NewGuid().ToString("N") : executable,
        ConfigDirectory = Path.Combine(_directory, "principal"),
        AccountsDirectory = Path.Combine(_directory, "accounts"),
    };

    [Fact]
    public async Task Contas_tem_logins_separados_e_a_ordem_persiste()
    {
        var options = Options();
        var accounts = new ClaudeAccounts(options, new AiAvailability());
        var primary = Assert.Single(accounts.All);
        Assert.Equal((ClaudeAccounts.PrimaryId, options.ConfigDirectory), (primary.Account.Id, primary.Account.Directory));

        var work = accounts.Add("  Trabalho  ");
        var spare = accounts.Add(null);
        Assert.Equal("Trabalho", work.Account.Label);
        Assert.Equal(3, accounts.All.Select(a => a.Account.Directory).Distinct().Count());
        Assert.StartsWith(options.AccountsDirectory, work.Account.Directory);
        Assert.NotSame(primary.Auth, work.Auth);

        accounts.Reorder([spare.Account.Id, ClaudeAccounts.PrimaryId, work.Account.Id]);
        Assert.Throws<ArgumentException>(() => accounts.Reorder([spare.Account.Id, work.Account.Id]));
        Assert.Throws<KeyNotFoundException>(() => accounts.Get("nao-existe"));

        var reloaded = new ClaudeAccounts(options, new AiAvailability());
        Assert.Equal([spare.Account.Id, ClaudeAccounts.PrimaryId, work.Account.Id], reloaded.All.Select(a => a.Account.Id));
        Assert.Equal("Trabalho", reloaded.Get(work.Account.Id).Account.Label);
        Assert.Equal(spare.Account.Id, reloaded.Get(null).Account.Id); // sem id: a primeira da ordem

        var states = await reloaded.DescribeAsync(refresh: false);
        Assert.All(states, s => Assert.Equal(AiAccountStates.LoggedOut, s.State));
        Assert.Equal(["Conta 1", "Conta principal", "Trabalho"], states.Select(s => s.Display));
    }

    [Fact]
    public async Task Remover_apaga_so_o_login_criado_para_a_conta_e_nao_remove_conta_em_uso()
    {
        var options = Options();
        var availability = new AiAvailability();
        var accounts = new ClaudeAccounts(options, availability);
        var extra = accounts.Add("Extra");
        Directory.CreateDirectory(extra.Account.Directory);
        File.WriteAllText(Path.Combine(extra.Account.Directory, ".credentials.json"), "{}");
        Directory.CreateDirectory(options.ConfigDirectory);

        using (FailoverAiProvider.Start([new AiCandidate(extra.Key, AiProviders.Claude, extra.Account.Id, "Extra", () => new ScriptedAi("m"))], availability))
            await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.RemoveAsync(extra.Account.Id));

        await accounts.RemoveAsync(extra.Account.Id);
        await accounts.RemoveAsync(ClaudeAccounts.PrimaryId);

        Assert.Empty(accounts.All);
        Assert.False(Directory.Exists(extra.Account.Directory));
        Assert.True(Directory.Exists(options.ConfigDirectory)); // login padrão (pode ser uma pasta configurada pelo usuário) fica no disco
        Assert.Empty(new ClaudeAccounts(options, availability).All);
    }

    /// <summary>Serviço com a IA roteirizada (sem Claude Code), banco e cache próprios.</summary>
    private ServiceProvider Services(IAiProviderSelector selector, string name) => new ServiceCollection()
        .AddEndpointAnalyzer(configureRuntime: o => { o.Enabled = false; o.AnalysisDatabasePath = Path.Combine(_directory, name, "analyses.db"); })
        .AddSingleton(new AnalysisCache(Path.Combine(_directory, name, "cache")))
        .AddSingleton(selector)
        .BuildServiceProvider();

    private sealed class Selector(Func<FailoverAiProvider> create) : IAiProviderSelector
    {
        public Task<IAiProvider?> SelectAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAiProvider?>(create());
    }

    [Fact]
    public async Task Sem_conta_disponivel_no_meio_a_analise_salva_o_progresso_e_lista_as_pendencias()
    {
        var availability = new AiAvailability();
        var a = new ScriptedAi("modelo-a", step => step == BusinessFlowAiAnalyzer.Purpose ? Limit() : null);
        var b = new ScriptedAi("modelo-a", _ => Limit());
        using var services = Services(new Selector(() => FailoverAiProvider.Start([Candidate("a", a), Candidate("b", b)], availability)), "parcial");
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var endpoint = await service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");
        var tabs = AnalysisSections.Summary | AnalysisSections.Business;

        var report = await service.AnalyzeAsync(fixture.SolutionPath, endpoint, sections: tabs, aiSections: tabs);

        Assert.Equal("Documentado por modelo-a", report.Ai!.Summary); // a etapa concluída fica no relatório
        Assert.NotNull(report.BusinessFlow!.Error);
        var interruption = report.AiRun!.Interruption!;
        Assert.Equal(AiHandoffReasons.UsageLimit, interruption.Reason);
        Assert.Equal(["Negócio: fluxo de negócio pela IA"], interruption.Pending);
        Assert.NotNull(report.AnalysisId);
        Assert.NotNull(services.GetRequiredService<SqliteAnalysisStore>().LoadStored(report.AnalysisId!));
        Assert.Equal(2, report.AiRun.Handoffs.Count);
        Assert.Contains("**Análise parcial:**", ReportRenderer.Markdown(report));
    }

    [Fact]
    public async Task Sem_nenhuma_resposta_da_ia_a_falha_sobe_e_nada_e_salvo()
    {
        var availability = new AiAvailability();
        var a = new ScriptedAi("modelo-x", _ => Limit());
        using var services = Services(new Selector(() => FailoverAiProvider.Start([Candidate("a", a)], availability)), "vazio");
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var endpoint = await service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");

        var error = await Assert.ThrowsAsync<AiProviderUnavailableException>(() =>
            service.AnalyzeAsync(fixture.SolutionPath, endpoint, sections: AnalysisSections.Summary, aiSections: AnalysisSections.Summary));

        Assert.Equal(AiUnavailableReason.UsageLimit, error.Reason);
        Assert.Null(services.GetRequiredService<SqliteAnalysisStore>().LatestId(fixture.SolutionPath, "POST", "/api/programacoes", endpoint.Project));
        // Contas sem uso no início: 429 direto, sem chamar a IA.
        await Assert.ThrowsAsync<AiProviderUnavailableException>(() =>
            service.AnalyzeAsync(fixture.SolutionPath, endpoint, sections: AnalysisSections.Summary, aiSections: AnalysisSections.Summary));
        Assert.Single(a.Prompts);
    }

    /// <summary>
    /// Claude Code falso (atrás de um shim do npm) que responde conforme o CLAUDE_CONFIG_DIR: cada conta tem e-mail
    /// (email.txt) e modo ("limit" ou resposta) próprios, e cada pedido recebido fica em prompts.log.
    /// </summary>
    private const string FakeClaudeScript = """
        const fs = require("fs"), path = require("path");
        const dir = process.env.CLAUDE_CONFIG_DIR, args = process.argv.slice(2);
        const read = (name) => { const f = path.join(dir, name); return fs.existsSync(f) ? fs.readFileSync(f, "utf8").trim() : ""; };
        if (args[0] === "auth" && args[1] === "status") {
          const email = read("email.txt");
          process.stdout.write(JSON.stringify(email ? { loggedIn: true, authMethod: "claude.ai", email, subscriptionType: "max" } : { loggedIn: false }));
        } else if (args[0] === "auth") {
          try { fs.unlinkSync(path.join(dir, "email.txt")); } catch { }
        } else {
          let input = "";
          process.stdin.on("data", d => input += d).on("end", () => {
            fs.appendFileSync(path.join(dir, "prompts.log"), JSON.stringify(input) + "\n");
            if (read("mode.txt") === "limit")
              process.stdout.write(JSON.stringify({ type: "result", is_error: true, result: "Claude AI usage limit reached|1760400000" }));
            else
              process.stdout.write(JSON.stringify({ type: "result", is_error: false, structured_output: {
                summary: "Documentado por " + read("email.txt"), businessRules: [], validations: [], entityChanges: [], uncertainties: [] } }));
          });
        }
        """;

    private static bool NodeAvailable => OperatingSystem.IsWindows()
        && (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d.Trim('"'), "node.exe")));

    [Fact]
    public async Task Analise_continua_na_proxima_conta_claude_com_o_mesmo_contexto()
    {
        if (!NodeAvailable) return;
        var bin = Directory.CreateDirectory(Path.Combine(_directory, "bin")).FullName;
        File.WriteAllText(Path.Combine(bin, "fake-claude.js"), FakeClaudeScript);
        var shim = Path.Combine(bin, "claude.cmd");
        File.WriteAllText(shim, "@ECHO off\r\n\"%_prog%\"  \"%dp0%\\fake-claude.js\" %*\r\n");
        using var services = new ServiceCollection()
            .AddEndpointAnalyzer(
                configureClaudeCode: o =>
                {
                    o.Executable = shim;
                    o.ConfigDirectory = Path.Combine(_directory, "principal");
                    o.AccountsDirectory = Path.Combine(_directory, "accounts");
                },
                configureRuntime: o => { o.Enabled = false; o.AnalysisDatabasePath = Path.Combine(_directory, "e2e", "analyses.db"); },
                configureCodex: o =>
                {
                    o.Executable = "codex-inexistente-" + Guid.NewGuid().ToString("N");
                    o.HomeDirectory = Path.Combine(_directory, "codex");
                })
            .AddSingleton(new AnalysisCache(Path.Combine(_directory, "e2e", "cache")))
            .BuildServiceProvider();
        var accounts = services.GetRequiredService<ClaudeAccounts>();
        var second = accounts.Add(null);
        foreach (var (directory, email, mode) in new[]
        {
            (Path.Combine(_directory, "principal"), "primeira@empresa.com", "limit"),
            (second.Account.Directory, "segunda@empresa.com", "ok"),
        })
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "email.txt"), email);
            File.WriteAllText(Path.Combine(directory, "mode.txt"), mode);
        }
        services.GetRequiredService<AiProviderRouter>().Mode = AiMode.Subscription;
        var service = services.GetRequiredService<EndpointAnalysisService>();
        var endpoint = await service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");

        var report = await service.AnalyzeAsync(fixture.SolutionPath, endpoint, sections: AnalysisSections.Summary,
            aiSections: AnalysisSections.Summary, aiProvider: AiProviders.Claude);

        Assert.Equal("Documentado por segunda@empresa.com", report.Ai!.Summary);
        var handoff = Assert.Single(report.AiRun!.Handoffs);
        Assert.Equal(("primeira@empresa.com", "segunda@empresa.com", AiHandoffReasons.UsageLimit), (handoff.FromAccount, handoff.ToAccount, handoff.Reason));
        Assert.Null(report.AiRun.Interruption);
        // A segunda conta recebeu exatamente o mesmo pedido (instruções, contexto do código e evidências).
        var first = File.ReadAllLines(Path.Combine(_directory, "principal", "prompts.log"));
        Assert.Equal(first, File.ReadAllLines(Path.Combine(second.Account.Directory, "prompts.log")));
        Assert.Contains("CriarProgramacaoRequest", JsonSerializer.Deserialize<string>(first[0]));

        var states = await accounts.DescribeAsync(refresh: false);
        Assert.Equal([AiAccountStates.NoUsage, AiAccountStates.Available], states.Select(s => s.State));
        // A próxima análise começa direto na conta com uso disponível.
        using var next = (FailoverAiProvider)(await services.GetRequiredService<AiProviderRouter>().SelectAsync(AiProviders.Claude))!;
        Assert.Equal("segunda@empresa.com", next.Current.Label);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
