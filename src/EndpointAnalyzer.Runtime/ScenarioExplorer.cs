using System.Text;
using System.Text.Json;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

internal sealed record Attempt(int Number, ScenarioMaterialization Materialization, RuntimeExecution Execution, MatchResult Match);

/// <summary>Andamento da validação de um cenário.</summary>
internal sealed class ScenarioState(Scenario scenario)
{
    public Scenario Scenario { get; } = scenario;

    /// <summary>Valores reais escolhidos pela IA (plano inicial ou exploração).</summary>
    public List<ScenarioBinding> Bindings { get; set; } = [];

    public List<Attempt> Attempts { get; } = [];

    /// <summary>Última falha de materialização (valores que contradizem o cenário, estado real incompatível).</summary>
    public string? MaterializationError { get; set; }

    public bool NeedsData { get; set; }

    /// <summary>Motivo dado pela IA quando não encontrou dados para o cenário.</summary>
    public string? PlanReason { get; set; }

    /// <summary>Classificação da IA ao desistir: nao_materializado, inconclusivo ou inalcancavel.</summary>
    public string? Verdict { get; set; }

    public string? Justification { get; set; }

    public bool Done { get; set; }

    public List<string> Problems { get; } = [];

    public Attempt? Confirmed => Attempts.FirstOrDefault(a => a.Match.Level == MatchLevel.Confirmed);
}

/// <summary>
/// ScenarioExplorer (Fases 5 e 6): confirma primeiro o caminho feliz (baseline) com os dados reais e, a partir dele,
/// executa cada cenário alterando só o necessário. Quando o resultado não confere, a IA vê a resposta e propõe outros
/// dados (tentativa e erro com contexto real), dentro dos limites de tentativas, rodadas e requisições.
/// </summary>
internal sealed class ScenarioExplorer(
    RuntimePayloadMaterializer materializer,
    RuntimeAi ai,
    RuntimeOptions options,
    ScenarioExecutor executor,
    ScenarioContext context,
    EndpointCatalog catalog,
    RuntimeValidation report,
    Action<string> log)
{
    private const int ScenariosPerPrompt = 12;

    private static readonly Dictionary<string, JsonElement> ResponseSchema = Schema.Root(new()
    {
        ["scenarios"] = Schema.Arr(Schema.Obj(new()
        {
            ["id"] = Schema.Str(),
            ["action"] = Schema.Enum("retry", "acquire", "give_up"),
            ["bindings"] = Schema.Arr(Schema.Binding()),
            ["classification"] = Schema.Enum("", "nao_materializado", "inconclusivo", "inalcancavel"),
            ["justification"] = Schema.Str(),
        })),
        ["requests"] = Schema.Arr(Schema.Request()),
        ["context"] = Schema.Arr(Schema.ContextItem()),
    });

    private ScenarioModel Model => materializer.Model;

    public Dictionary<string, ScenarioState> States { get; } = [];

    public ScenarioMaterialization? Baseline { get; private set; }

    public string? BaselineId { get; private set; }

    public async Task RunAsync(EndpointAnalysisContext analysis, IReadOnlyList<Scenario> scenarios, Dictionary<string, BindingPlan> plans,
        List<DataRequirement> requirements, CancellationToken cancellationToken)
    {
        foreach (var scenario in scenarios)
        {
            var state = new ScenarioState(scenario);
            if (plans.TryGetValue(scenario.Id, out var plan))
            {
                state.Bindings = plan.Bindings;
                if (!plan.Materializable) state.PlanReason = plan.Reason ?? "sem dado real para o cenário";
            }
            States[scenario.Id] = state;
        }

        // Fase 5: baseline (caminho feliz confirmado com dados reais).
        var baseline = scenarios.FirstOrDefault(s => s.Kind == ScenarioKinds.Success && s.Focus is null);
        if (baseline is not null)
        {
            BaselineId = baseline.Id;
            await BaselineAsync(analysis, States[baseline.Id], requirements, cancellationToken);
        }
        else log("Sem caminho feliz na matriz: os cenários serão materializados sem baseline.");

        // Fase 6: cada cenário a partir do baseline. Os que escrevem (sucesso) por último: mudam o estado.
        var order = new[] { ScenarioKinds.Validation, ScenarioKinds.Rule, ScenarioKinds.Boundary, ScenarioKinds.Success };
        foreach (var state in States.Values.Where(s => s.Scenario.Id != BaselineId).OrderBy(s => Array.IndexOf(order, s.Scenario.Kind)))
        {
            if (executor.BudgetExhausted) break;
            await TryAsync(state, RuntimePhases.Scenario, cancellationToken);
        }

        for (var round = 1; round <= options.MaxExplorationRounds && !executor.BudgetExhausted; round++)
        {
            var pending = States.Values.Where(Pending).ToList();
            if (pending.Count == 0) break;
            log($"Exploração {round}: {pending.Count} cenário(s) sem confirmação.");
            foreach (var chunk in pending.Chunk(ScenariosPerPrompt))
                await ExploreAsync(analysis, chunk, requirements, $"explorar cenários (rodada {round})", cancellationToken);
        }
    }

    private bool Pending(ScenarioState s) =>
        !s.Done && s.Attempts.Count < options.MaxAttemptsPerScenario && (s.Attempts.Count > 0 || s.MaterializationError is not null);

    private async Task BaselineAsync(EndpointAnalysisContext analysis, ScenarioState state, List<DataRequirement> requirements, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= options.MaxAttemptsPerScenario && !executor.BudgetExhausted; attempt++)
        {
            var before = state.Attempts.Count;
            await TryAsync(state, RuntimePhases.Baseline, cancellationToken);
            if (state.Confirmed is { } confirmed)
            {
                Baseline = confirmed.Materialization;
                var body = confirmed.Execution.RequestBody?.ToJsonString() ?? "null";
                context.Add("baseline", $"Payload do caminho feliz ({state.Scenario.Id}) confirmado: {confirmed.Execution.Method} {confirmed.Execution.Url} → HTTP {confirmed.Execution.Status}",
                    $"{{\"url\":{JsonSerializer.Serialize(confirmed.Execution.Url)},\"body\":{body},\"response\":{JsonSerializer.Serialize(ScenarioContext.Truncate(confirmed.Execution.ResponseBody ?? "", 1500))}}}",
                    null, confirmed.Execution.Id, "execucao", trusted: true);
                log($"Baseline {state.Scenario.Id} confirmado ({confirmed.Execution.Id}).");
                return;
            }
            if (state.Done || attempt == options.MaxAttemptsPerScenario) break;

            var bindings = state.Bindings;
            var executed = report.Executions.Count;
            await ExploreAsync(analysis, [state], requirements, $"corrigir o baseline (tentativa {attempt})", cancellationToken, retry: false);
            if (state.Done) break;
            // Nada mudou (mesmos valores, nenhum dado novo): outra tentativa repetiria o mesmo payload.
            if (ReferenceEquals(bindings, state.Bindings) && report.Executions.Count == executed && state.Attempts.Count == before) break;
        }
        log($"Baseline {state.Scenario.Id} não confirmado: os demais cenários usam os valores do gerador.");
    }

    /// <summary>Materializa e executa o cenário uma vez.</summary>
    private async Task TryAsync(ScenarioState state, string phase, CancellationToken cancellationToken)
    {
        var id = state.Scenario.Id;
        var m = materializer.Materialize(state, id == BaselineId ? null : Baseline, context);
        if (!m.Success)
        {
            state.MaterializationError = m.Error;
            state.NeedsData = m.NeedsData;
            state.Problems.Add($"Materialização: {m.Error}");
            log($"{id}: não materializado — {m.Error}");
            return;
        }
        state.MaterializationError = null;
        state.NeedsData = false;

        var request = RuntimeRequest.From(m.Request!);
        var signature = Signature(request);
        if (state.Attempts.Any(a => Signature(RuntimeRequest.From(a.Materialization.Request!)) == signature))
        {
            state.Problems.Add("Os novos valores geraram o mesmo payload de uma tentativa anterior.");
            return;
        }

        var execution = await executor.ExecuteAsync(request, phase, id, state.Attempts.Count + 1, cancellationToken: cancellationToken);
        var match = OutcomeMatcher.Match(state.Scenario, m.Expected!, m, execution, Model.Matrix);
        state.Attempts.Add(new Attempt(state.Attempts.Count + 1, m, execution, match));
        log($"{id} ({execution.Id}): {match.Level} — {match.Observed}");

        var wrote = !request.IsRead && execution.Status is >= 200 and < 300;
        if (wrote) context.InvalidateFacts();

        if (match.Level == MatchLevel.Confirmed)
        {
            state.Done = true;
            // O estado lido pelo fluxo confirmado vira fato (até a próxima escrita, que pode mudá-lo).
            if (!wrote)
            {
                context.AddFacts(m.Facts);
                context.AddFacts(Model.InferFacts(m, execution.Id));
            }
        }
        else if (execution.Blocked || !match.Retryable) state.Done = true;
    }

    /// <summary>
    /// A IA diz que o cenário é inalcançável: repete a última execução (só se ela não alterou nada, ou seja, terminou em
    /// erro) para mostrar que o resultado é estável. A decisão de retirar o cenário continua com o MatrixReconciler.
    /// </summary>
    private async Task RepeatAsync(ScenarioState state, CancellationToken cancellationToken)
    {
        var executed = state.Attempts.Where(a => !a.Execution.Blocked && a.Execution.Status is not null).ToList();
        if (executed.Count != 1 || executed[0].Execution.Status is >= 200 and < 300 || executor.BudgetExhausted) return;

        var last = executed[0];
        var execution = await executor.ExecuteAsync(RuntimeRequest.From(last.Materialization.Request!), RuntimePhases.Exploration, state.Scenario.Id,
            state.Attempts.Count + 1, "repetição para confirmar que o resultado é estável", cancellationToken);
        var match = OutcomeMatcher.Match(state.Scenario, last.Materialization.Expected!, last.Materialization, execution, Model.Matrix);
        state.Attempts.Add(new Attempt(state.Attempts.Count + 1, last.Materialization, execution, match));
        log($"{state.Scenario.Id} ({execution.Id}): repetição — {match.Observed}");
    }

    private async Task ExploreAsync(EndpointAnalysisContext analysis, IReadOnlyList<ScenarioState> states, List<DataRequirement> requirements,
        string purpose, CancellationToken cancellationToken, bool retry = true)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Endpoint analisado: {analysis.Endpoint.HttpMethod} {analysis.Endpoint.Route}");
        prompt.AppendLine($"Requisições restantes: {executor.Remaining}. Escrita permitida: {(executor.WritesAllowed ? "sim" : "não (apenas GET)")}.");
        prompt.AppendLine(executor.Authenticated
            ? "Autenticação: todas as requisições já levam o token da API informado pelo usuário; não tente obter outro token."
            : "Autenticação: nenhum token informado; se a API responder 401, o cenário depende de autenticação (não tente adivinhar credenciais).");
        prompt.AppendLine(Baseline is { } b
            ? $"Baseline confirmado ({BaselineId}): {b.Request!.Method} {b.Request.Url} {b.Request.Body?.ToJsonString()}"
            : "Baseline: ainda não confirmado.");
        prompt.AppendLine();
        prompt.Append(RuntimeAi.Section("catalog", catalog.Describe()));
        prompt.Append(RuntimeAi.Section("context", context.Describe()));
        prompt.Append(RuntimeAi.Section("payload_base", context.DescribePayload()));
        prompt.Append(RuntimeAi.Section("acquisition_executions", DataAcquisitionPlanner.Executions(report, RuntimePhases.Acquisition, RuntimePhases.Exploration)));
        prompt.Append(RuntimeAi.Section("code", RuntimeAi.Code(analysis, 16000)));
        prompt.Append(RuntimeAi.Section("pending", Pending(states)));
        prompt.AppendLine("""
            Tarefa: os cenários em pending não reproduziram o resultado esperado (ou não foram materializados).
            Para cada um, analise o resultado observado, o código e os dados, e escolha:
            - "retry": novos bindings (mesmo formato: variable, valueJson, source) com dados reais que levem ao esperado.
              Os bindings substituem os anteriores do cenário; o resto do payload vem do baseline confirmado (ou, sem
              ele, do payload_base). Se a resposta recusar um campo do payload base (ex.: código ou e-mail já
              cadastrado), ligue esse campo a outro valor real ou derivado de um dado real.
            - "acquire": faltam dados; peça em requests (prefira GET; só rotas do catálogo). O cenário volta na próxima rodada.
            - "give_up": não há como reproduzir. classification: "nao_materializado" (não há como obter o estado
              necessário), "inconclusivo" (não dá para concluir) ou "inalcancavel" (o código e as execuções mostram que o
              cenário não pode ocorrer: justifique citando o trecho do código e as execuções EX-..).
            - Para outras escolhas, classification = "".
            - context: dados novos encontrados nas respostas (executionId = execução de onde vieram).
            Não repita valores já tentados. Não invente ids.
            """);

        var response = await ai.AskAsync(purpose, prompt.ToString(), ResponseSchema, cancellationToken);
        if (response is null) return;

        DataAcquisitionPlanner.ApplyContext(response.Value, requirements, context, RuntimePhases.Exploration);
        foreach (var r in Read.Arr(response, "requests").Take(options.MaxRequestsPerRound))
        {
            if (DataAcquisitionPlanner.ParseRequest(r, out var reason) is not { } request) continue;
            var execution = await executor.ExecuteAsync(request, RuntimePhases.Exploration, reason: reason, cancellationToken: cancellationToken);
            if (!request.IsRead && execution.Status is >= 200 and < 300) context.InvalidateFacts();
        }

        foreach (var item in Read.Arr(response, "scenarios"))
        {
            var state = states.FirstOrDefault(s => s.Scenario.Id == Read.Str(item, "id"));
            if (state is null || state.Done) continue;
            switch (Read.Str(item, "action"))
            {
                case "give_up":
                    state.Done = true;
                    state.Verdict = Read.NonEmpty(Read.Str(item, "classification")) ?? "inconclusivo";
                    state.Justification = Read.NonEmpty(Read.Str(item, "justification"));
                    if (state.Verdict == "inalcancavel" && retry) await RepeatAsync(state, cancellationToken);
                    break;
                case "retry":
                    state.Bindings = RuntimePayloadMaterializer.Bindings(item);
                    state.Justification = Read.NonEmpty(Read.Str(item, "justification"));
                    if (retry && !executor.BudgetExhausted) await TryAsync(state, RuntimePhases.Exploration, cancellationToken);
                    break;
                default:
                    state.Justification = Read.NonEmpty(Read.Str(item, "justification"));
                    break;
            }
        }
    }

    /// <summary>Cenários pendentes com as tentativas (payload, resultado observado e por que não conferiu).</summary>
    private string Pending(IEnumerable<ScenarioState> states)
    {
        var sb = new StringBuilder();
        foreach (var state in states)
        {
            sb.Append(RuntimePayloadMaterializer.Scenarios([state.Scenario], Model));
            if (state.Bindings.Count > 0)
                sb.AppendLine($"bindings atuais: {string.Join("; ", state.Bindings.Select(b => $"{b.Variable} = {b.Value}"))}");
            foreach (var a in state.Attempts)
            {
                var e = a.Execution;
                sb.AppendLine($"tentativa {a.Number} ({e.Id}): {e.Method} {e.Url} {ScenarioContext.Truncate(e.RequestBody?.ToJsonString() ?? "", 600)}");
                if (a.Materialization.Changes.Count > 0) sb.AppendLine($"  alterado do baseline: {string.Join("; ", a.Materialization.Changes)}");
                sb.AppendLine($"  observado: {e.Status?.ToString() ?? "sem resposta"} {ScenarioContext.Truncate(e.ResponseBody ?? e.Exception ?? "", 800)}");
                sb.AppendLine($"  problema: {string.Join(" ", a.Match.Reasons)}");
            }
            if (state.MaterializationError is not null) sb.AppendLine($"materialização recusada: {state.MaterializationError}");
            if (state.PlanReason is not null) sb.AppendLine($"plano: {state.PlanReason}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Signature(RuntimeRequest r) => $"{r.Method}|{r.Url}|{r.Body?.ToJsonString()}|{string.Join(";", r.Headers ?? [])}";
}
