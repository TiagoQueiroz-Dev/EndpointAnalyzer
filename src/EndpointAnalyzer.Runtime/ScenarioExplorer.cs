using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>Classificação da IA ao desistir: inalcancavel (aceita só com prova) ou nao_materializado (nunca executado).</summary>
    public string? Verdict { get; set; }

    public string? Justification { get; set; }

    /// <summary>O que ainda falta para concluir (prova de inalcançável insuficiente, regra não isolada): vai para a IA.</summary>
    public string? Missing { get; set; }

    /// <summary>Limite que interrompeu a exploração antes de o cenário ser confirmado ou provado inalcançável.</summary>
    public string? StopReason { get; set; }

    /// <summary>Confirmado, inalcançável com prova, ou impossível de executar (escrita desligada, rota bloqueada).</summary>
    public bool Done { get; set; }

    public List<string> Problems { get; } = [];

    public Attempt? Confirmed => Attempts.FirstOrDefault(a => a.Match.Level == MatchLevel.Confirmed);
}

/// <summary>
/// ScenarioExplorer (Fases 5 e 6): confirma primeiro o caminho feliz (baseline) com os dados reais e, a partir dele,
/// executa cada cenário alterando só o necessário. Não há "inconclusivo": enquanto o cenário não for CONFIRMADO nem
/// provado INALCANÇÁVEL, a IA vê a resposta e propõe outro payload, mudando só as propriedades que o resultado observado
/// aponta (tentativa e erro com contexto real). Só os limites de segurança (requisições, rodadas, execuções por cenário)
/// ou uma rodada sem nada novo interrompem a exploração.
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
            ["unbind"] = Schema.Arr(Schema.Str()),
            ["classification"] = Schema.Enum("", "nao_materializado", "inalcancavel"),
            ["justification"] = Schema.Str(),
        })),
        ["requests"] = Schema.Arr(Schema.Request()),
        ["context"] = Schema.Arr(Schema.ContextItem()),
    });

    private Attempt? _baseline;

    private ScenarioModel Model => materializer.Model;

    public Dictionary<string, ScenarioState> States { get; } = [];

    public ScenarioMaterialization? Baseline => _baseline?.Materialization;

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

        // Até cada cenário ser confirmado ou provado inalcançável.
        string? stop = null;
        for (var round = 1; ; round++)
        {
            var pending = States.Values.Where(Pending).ToList();
            if (pending.Count == 0) break;
            if (executor.BudgetExhausted)
            {
                stop = $"limite de {options.MaxRequests} requisições por análise (Runtime:MaxRequests)";
                break;
            }
            if (round > options.MaxExplorationRounds)
            {
                stop = $"limite de {options.MaxExplorationRounds} rodadas de exploração (Runtime:MaxExplorationRounds)";
                break;
            }

            var before = Progress();
            log($"Exploração {round}: {pending.Count} cenário(s) sem confirmação nem prova de inalcançável.");
            foreach (var chunk in pending.Chunk(ScenariosPerPrompt))
                await ExploreAsync(analysis, chunk, requirements, $"explorar cenários (rodada {round})", cancellationToken);
            if (Progress() == before)
            {
                stop = "a IA não propôs payload, dado ou prova novos na última rodada de exploração";
                break;
            }
        }

        foreach (var state in States.Values.Where(s => !s.Done))
        {
            state.StopReason = state.Attempts.Count >= options.MaxAttemptsPerScenario
                ? $"limite de {options.MaxAttemptsPerScenario} execuções por cenário (Runtime:MaxAttemptsPerScenario)"
                : stop ?? "limite atingido";
            log($"{state.Scenario.Id}: não concluído — {state.StopReason}.");
        }
    }

    private bool Pending(ScenarioState s) => !s.Done && s.Attempts.Count < options.MaxAttemptsPerScenario;

    /// <summary>Muda a cada execução, dado novo, materialização recusada ou cenário concluído.</summary>
    private string Progress() =>
        $"{report.Executions.Count}|{report.Context.Count}|{States.Values.Sum(s => s.Problems.Count)}|{States.Values.Count(s => s.Done)}|" +
        string.Join(";", States.Values.Select(s => s.Missing));

    private async Task BaselineAsync(EndpointAnalysisContext analysis, ScenarioState state, List<DataRequirement> requirements, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= options.MaxAttemptsPerScenario && !executor.BudgetExhausted; attempt++)
        {
            var before = state.Attempts.Count;
            await TryAsync(state, RuntimePhases.Baseline, cancellationToken);
            if (_baseline is not null) return;
            if (state.Done || attempt == options.MaxAttemptsPerScenario) break;

            var bindings = state.Bindings;
            var executed = report.Executions.Count;
            await ExploreAsync(analysis, [state], requirements, $"corrigir o baseline (tentativa {attempt})", cancellationToken, retry: false);
            if (state.Done) break;
            // Nada mudou (mesmos valores, nenhum dado novo): outra tentativa repetiria o mesmo payload.
            if (bindings.SequenceEqual(state.Bindings) && report.Executions.Count == executed && state.Attempts.Count == before) break;
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
        if (state.Attempts.Any(a => !a.Execution.Blocked && Signature(RuntimeRequest.From(a.Materialization.Request!)) == signature))
        {
            state.Problems.Add("Os novos valores geraram o mesmo payload de uma tentativa anterior.");
            state.Missing = "os últimos valores geraram um payload já executado: proponha valores diferentes.";
            return;
        }

        var execution = await executor.ExecuteAsync(request, phase, id, state.Attempts.Count + 1, cancellationToken: cancellationToken);
        var match = Isolate(Model, state.Scenario, m, execution, OutcomeMatcher.Match(state.Scenario, m.Expected!, m, execution, Model.Matrix), _baseline, BaselineId);
        var attempt = new Attempt(state.Attempts.Count + 1, m, execution, match);
        state.Attempts.Add(attempt);
        log($"{id} ({execution.Id}): {match.Level} — {match.Observed}");

        var wrote = !request.IsRead && execution.Status is >= 200 and < 300;
        if (wrote) context.InvalidateFacts();

        if (match.Level == MatchLevel.Confirmed)
        {
            state.Done = true;
            state.Verdict = null;
            state.Missing = null;
            // O estado lido pelo fluxo confirmado vira fato (até a próxima escrita, que pode mudá-lo).
            if (!wrote)
            {
                context.AddFacts(m.Facts);
                context.AddFacts(Model.InferFacts(m, execution.Id));
            }
            if (id == BaselineId && _baseline is null) SetBaseline(state, attempt);
        }
        // Escrita desligada ou rota fora do catálogo: nenhum payload vai ser executado.
        else if (execution.Blocked && !executor.BudgetExhausted) state.Done = true;
        else state.Missing = null;
    }

    private void SetBaseline(ScenarioState state, Attempt confirmed)
    {
        _baseline = confirmed;
        var body = confirmed.Execution.RequestBody?.ToJsonString() ?? "null";
        context.Add("baseline", $"Payload do caminho feliz ({state.Scenario.Id}) confirmado: {confirmed.Execution.Method} {confirmed.Execution.Url} → HTTP {confirmed.Execution.Status}",
            $"{{\"url\":{JsonSerializer.Serialize(confirmed.Execution.Url)},\"body\":{body},\"response\":{JsonSerializer.Serialize(ScenarioContext.Truncate(confirmed.Execution.ResponseBody ?? "", 1500))}}}",
            null, confirmed.Execution.Id, "execucao", trusted: true);
        log($"Baseline {state.Scenario.Id} confirmado ({confirmed.Execution.Id}).");
    }

    /// <summary>
    /// Confirmação por isolamento, quando o status confere mas a resposta não identifica a regra (mensagem ausente ou
    /// status compartilhado com outra regra): o payload difere do baseline confirmado só nos campos da condição do
    /// cenário, todo o estado lido está comprovado e nenhuma outra regra com o mesmo status depende desses campos. A
    /// mudança de 2xx (baseline) para o status esperado só pode vir da regra do cenário.
    /// </summary>
    internal static MatchResult Isolate(ScenarioModel model, Scenario scenario, ScenarioMaterialization m, RuntimeExecution execution, MatchResult match,
        Attempt? baseline, string? baselineId)
    {
        if (match.Level != MatchLevel.Partial || baseline is null || scenario.Id == baselineId) return match;
        if (m.Expected?.Outcome != "erro" || execution.Status is not { } status || m.Changes.Count == 0) return match;
        if (m.UnverifiedFocus.Count > 0 || m.UnverifiedState.Count > 0 || m.Assumptions.Count > 0) return match;

        var changed = m.Changes.Select(c => c.Split(": ", 2)[0].Trim()).Distinct().ToList();
        var outside = changed.Where(f => !Touches(model.FocusVariables(scenario.Id, baseline.Materialization), f)).ToList();
        if (outside.Count > 0)
            return match with { Reasons = [.. match.Reasons, $"Isolamento: o payload também muda {string.Join(", ", outside)}, fora da condição do cenário; mude só os campos da condição."] };

        var rivals = model.Matrix.Scenarios.Where(s => s.Id != scenario.Id && s.Expected.Outcome == "erro" && s.Expected.HttpStatus == status).ToList();
        if (rivals.Any(r => r.Expected.Messages.Any(x => !string.IsNullOrWhiteSpace(x)) && OutcomeMatcher.ExplainedBy(r.Expected, execution))) return match;
        var shared = rivals.Where(r => changed.Any(f => Touches(model.FocusVariables(r.Id, baseline.Materialization), f))).Select(r => r.Id).ToList();
        if (shared.Count > 0)
            return match with { Reasons = [.. match.Reasons, $"Isolamento: {string.Join(", ", shared)} (mesmo HTTP {status}) também depende de {string.Join(", ", changed)}."] };

        var evidence = new List<string>(match.Evidence)
        {
            $"isolamento: o baseline {baselineId} ({baseline.Execution.Id}) respondeu HTTP {baseline.Execution.Status}; este payload muda só {string.Join("; ", m.Changes)} " +
            $"(condição do cenário) e a resposta passou a HTTP {status}" +
            (rivals.Count > 0 ? $"; {string.Join(", ", rivals.Select(r => r.Id))} (mesmo status) não depende desses campos" : ""),
        };
        if (m.Expected.Messages.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is { } message)
            evidence.Add($"mensagem observada diferente da prevista (\"{message}\"): {match.Observed}");
        return new MatchResult(MatchLevel.Confirmed, evidence, [], false, match.Observed);
    }

    /// <summary>A variável é o campo ou depende dele ("veiculos.ObterPorId(request.VeiculoId).Disponivel" ⊃ "request.VeiculoId").</summary>
    private static bool Touches(IReadOnlyList<string> variables, string field)
    {
        var pattern = $@"(?<![\w.]){Regex.Escape(field)}(?!\w)";
        return variables.Any(v => Regex.IsMatch(v, pattern));
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
            ? $"Baseline confirmado ({BaselineId}, {_baseline!.Execution.Id}): {b.Request!.Method} {b.Request.Url} {b.Request.Body?.ToJsonString()}"
            : "Baseline: ainda não confirmado.");
        prompt.AppendLine();
        prompt.Append(RuntimeAi.Section("catalog", catalog.Describe()));
        prompt.Append(RuntimeAi.Section("context", context.Describe()));
        prompt.Append(RuntimeAi.Section("payload_base", context.DescribePayload()));
        prompt.Append(RuntimeAi.Section("acquisition_executions", DataAcquisitionPlanner.Executions(report, RuntimePhases.Acquisition, RuntimePhases.Exploration)));
        prompt.Append(RuntimeAi.Section("code", RuntimeAi.Code(analysis, 16000)));
        prompt.Append(RuntimeAi.Section("pending", Pending(states)));
        prompt.AppendLine("""
            Tarefa: os cenários em pending ainda não foram CONFIRMADOS nem provados INALCANÇÁVEIS. Não existe
            "inconclusivo": cada cenário só termina confirmado (uma execução reproduz o resultado esperado) ou inalcançável
            (com prova). Para cada um, analise o resultado observado, o código e os dados, e escolha:
            - "retry": um payload diferente de todas as tentativas. Parta da última tentativa e altere somente as
              propriedades que o resultado observado aponta (o campo recusado na resposta, a regra que disparou antes da
              esperada, o estado lido pelo fluxo); todo o resto continua igual.
              bindings = só as variáveis que mudam (variable, valueJson, source), com dados reais; as demais ligações da
              tentativa anterior são mantidas. unbind = variáveis que voltam ao valor do baseline (ou do payload_base).
              Se a resposta recusar um campo do payload base (ex.: código ou e-mail já cadastrado), ligue esse campo a
              outro valor real ou derivado de um dado real. Se o status confere mas a regra não foi identificada
              (mensagem ausente ou status compartilhado), mude em relação ao baseline só os campos da condição do cenário.
            - "acquire": faltam dados reais; peça em requests (prefira GET; só rotas do catálogo). O cenário volta na
              próxima rodada.
            - "give_up" com classification "inalcancavel": o código e as execuções mostram que o cenário não pode ocorrer
              (justifique citando o trecho do código e as execuções EX-..). A prova exige 2+ execuções com payloads
              diferentes, aceitos pelo solver e com o estado lido comprovado, que terminam sempre no mesmo resultado,
              previsto para outro cenário. Enquanto faltar, envie em bindings uma variação do payload (outros valores
              reais que ainda satisfazem o cenário, mudando só o necessário) para ser executada.
            - "give_up" com classification "nao_materializado": só para cenário nunca executado cujo estado não existe e
              não pode ser criado pelas rotas do catálogo.
            - Para "retry" e "acquire", classification = "".
            - context: dados novos encontrados nas respostas (executionId = execução de onde vieram).
            Não repita payloads já tentados. Não invente ids.
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
            state.Justification = Read.NonEmpty(Read.Str(item, "justification")) ?? state.Justification;
            switch (Read.Str(item, "action"))
            {
                case "give_up":
                    await GiveUpAsync(state, item, retry, cancellationToken);
                    break;
                case "retry":
                    state.Verdict = null;
                    state.Bindings = Merge(state.Bindings, item);
                    if (retry && !executor.BudgetExhausted && state.Attempts.Count < options.MaxAttemptsPerScenario)
                        await TryAsync(state, RuntimePhases.Exploration, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>
    /// A IA desistiu. "inalcancavel" só encerra o cenário com prova (<see cref="MatrixReconciler.UnreachableEvidence"/>):
    /// enquanto ela faltar, a variação enviada é executada e o cenário continua pendente. "nao_materializado" só vale
    /// para cenário nunca executado.
    /// </summary>
    private async Task GiveUpAsync(ScenarioState state, JsonElement item, bool retry, CancellationToken cancellationToken)
    {
        switch (Read.Str(item, "classification"))
        {
            case "inalcancavel":
                state.Verdict = "inalcancavel";
                if (MatrixReconciler.UnreachableEvidence(state.Scenario, state, Model.Matrix, out _) is null
                    && Read.Arr(item, "bindings").Any() && retry && !executor.BudgetExhausted && state.Attempts.Count < options.MaxAttemptsPerScenario)
                {
                    state.Bindings = Merge(state.Bindings, item);
                    await TryAsync(state, RuntimePhases.Exploration, cancellationToken);
                    if (state.Done) return;
                    state.Verdict = "inalcancavel";
                }
                if (MatrixReconciler.UnreachableEvidence(state.Scenario, state, Model.Matrix, out var missing) is not null)
                {
                    state.Done = true;
                    state.Missing = null;
                    log($"{state.Scenario.Id}: inalcançável com prova.");
                }
                else state.Missing = $"prova de inalcançável insuficiente: {missing}";
                break;
            case "nao_materializado" when state.Attempts.All(a => a.Execution.Blocked):
                state.Verdict = "nao_materializado";
                state.Done = true;
                break;
            case "nao_materializado":
                state.Missing = $"o cenário já foi executado ({string.Join(", ", state.Attempts.Select(a => a.Execution.Id))}): não cabe \"nao_materializado\"; proponha outro payload (retry) ou prove que é inalcançável.";
                break;
            default:
                state.Missing = "give_up exige classification \"inalcancavel\" (com prova) ou \"nao_materializado\" (cenário nunca executado).";
                break;
        }
    }

    /// <summary>Ligações da tentativa anterior com as mudanças da IA: substitui as variáveis informadas e remove as de unbind.</summary>
    private static List<ScenarioBinding> Merge(List<ScenarioBinding> current, JsonElement item)
    {
        var changes = RuntimePayloadMaterializer.Bindings(item);
        var removed = Read.Strs(item, "unbind").Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current
            .Where(b => !removed.Contains(b.Variable) && changes.All(c => !string.Equals(c.Variable, b.Variable, StringComparison.OrdinalIgnoreCase)))
            .Concat(changes)
            .ToList();
    }

    /// <summary>Cenários pendentes com as tentativas (payload, resultado observado e por que não conferiu).</summary>
    private string Pending(IEnumerable<ScenarioState> states)
    {
        var sb = new StringBuilder();
        foreach (var state in states)
        {
            sb.Append(RuntimePayloadMaterializer.Scenarios([state.Scenario], Model));
            if (Model.FocusVariables(state.Scenario.Id, Baseline) is { Count: > 0 } focus)
                sb.AppendLine($"condição do cenário (variáveis): {string.Join(", ", focus)}");
            sb.AppendLine($"execuções: {state.Attempts.Count} de {options.MaxAttemptsPerScenario}");
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
            if (state.Verdict == "inalcancavel") sb.AppendLine("classificação proposta: inalcancavel (ainda sem prova suficiente)");
            if (state.Missing is not null) sb.AppendLine($"falta: {state.Missing}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Signature(RuntimeRequest r) => $"{r.Method}|{r.Url}|{r.Body?.ToJsonString()}|{string.Join(";", r.Headers ?? [])}";

    /// <summary>Assinatura do payload executado (método, URL, corpo e headers).</summary>
    internal static string Signature(RuntimeExecution e) => $"{e.Method}|{e.Url}|{e.RequestBody?.ToJsonString()}|{string.Join(";", e.Headers ?? [])}";
}
