using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Runtime;

/// <summary>
/// MatrixReconciler (Fase 7): compara a matriz estática com as execuções reais e classifica cada cenário. Regras:
/// <list type="bullet">
/// <item>CONFIRMADO só com uma execução que reproduziu o esperado (<see cref="OutcomeMatcher"/>).</item>
/// <item>Uma execução que falhou não remove o cenário: fica INCONCLUSIVO ou NÃO MATERIALIZADO.</item>
/// <item>INALCANÇÁVEL (fora da matriz final) exige, além da justificativa da IA, pelo menos duas execuções com payloads
/// aceitos pelo solver para o cenário e o mesmo resultado em todas, explicado por outro cenário da matriz.</item>
/// <item>Resultados que nenhum cenário prevê viram DESCOBERTO EM RUNTIME.</item>
/// </list>
/// </summary>
internal static class MatrixReconciler
{
    public static ValidatedMatrix Reconcile(ScenarioMatrix matrix, IReadOnlyDictionary<string, ScenarioState> states, IReadOnlyList<RuntimeExecution> executions,
        bool writesAllowed, RuntimeOptions options)
    {
        var result = new ValidatedMatrix();
        foreach (var scenario in matrix.Scenarios)
        {
            var validated = states.TryGetValue(scenario.Id, out var state)
                ? Classify(scenario, state, matrix, writesAllowed)
                : new ValidatedScenario
                {
                    Id = scenario.Id,
                    Status = ScenarioValidationStatuses.Inconclusive,
                    Kind = scenario.Kind,
                    Title = scenario.Title,
                    Expected = scenario.Expected,
                    Request = scenario.Request,
                    Preconditions = scenario.Preconditions,
                    Reasons = [$"Não validado: limite de {options.MaxScenarios} cenários por análise (Runtime:MaxScenarios)."],
                };
            if (validated.Status == ScenarioValidationStatuses.Unreachable) result.Removed.Add(validated);
            else result.Scenarios.Add(validated);
        }

        result.Scenarios.AddRange(Discovered(matrix, executions));
        foreach (var status in ScenarioValidationStatuses.All)
            result.Counts[status] = result.Scenarios.Count(s => s.Status == status) + result.Removed.Count(s => s.Status == status);
        return result;
    }

    private static ValidatedScenario Classify(Scenario scenario, ScenarioState state, ScenarioMatrix matrix, bool writesAllowed)
    {
        var validated = new ValidatedScenario
        {
            Id = scenario.Id,
            Kind = scenario.Kind,
            Title = scenario.Title,
            Expected = scenario.Expected,
            Request = scenario.Request,
            Preconditions = scenario.Preconditions,
            Attempts = state.Attempts.Count,
        };

        var deciding = state.Confirmed
            ?? state.Attempts.LastOrDefault(a => a.Match.Level == MatchLevel.Partial)
            ?? state.Attempts.LastOrDefault();
        if (deciding is not null) Fill(validated, deciding);

        if (state.Confirmed is not null)
        {
            validated.Status = ScenarioValidationStatuses.Confirmed;
            return validated;
        }

        if (state.Attempts.Count == 0)
        {
            validated.Status = ScenarioValidationStatuses.NotMaterialized;
            if (!writesAllowed && !string.Equals(scenario.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                validated.Reasons.Add("Escrita desligada (Runtime:AllowWrites = false): o endpoint não pôde ser executado.");
            validated.Reasons.AddRange(state.Problems.Distinct().TakeLast(3));
            if (state.PlanReason is not null) validated.Reasons.Add($"IA: {state.PlanReason}");
            if (state.Justification is not null) validated.Reasons.Add($"IA: {state.Justification}");
            if (validated.Reasons.Count == 0) validated.Reasons.Add("Não foi possível obter o estado necessário para o cenário.");
            return validated;
        }

        validated.Reasons.AddRange(state.Attempts.Last().Match.Reasons);
        if (state.Attempts.Any(a => a.Execution.Blocked)) validated.Reasons.Add($"Requisição bloqueada: {state.Attempts.First(a => a.Execution.Blocked).Execution.Reason}");
        if (state.MaterializationError is not null) validated.Reasons.Add($"Última materialização recusada: {state.MaterializationError}");
        if (state.Justification is not null) validated.Reasons.Add($"IA: {state.Justification}");

        if (state.Verdict == "inalcancavel" && UnreachableEvidence(scenario, state, matrix) is { } evidence)
        {
            validated.Status = ScenarioValidationStatuses.Unreachable;
            validated.Evidence.AddRange(evidence);
            return validated;
        }
        if (state.Verdict == "inalcancavel")
            validated.Reasons.Add("A IA indicou que o cenário é inalcançável, mas a evidência não basta para retirá-lo (são necessárias 2+ execuções com payload aceito pelo solver, estado comprovado e o mesmo resultado, previsto por outro cenário).");

        validated.Status = state.Verdict == "nao_materializado" || (state.NeedsData && state.Attempts.All(a => a.Match.Level == MatchLevel.Mismatch))
            ? ScenarioValidationStatuses.NotMaterialized
            : ScenarioValidationStatuses.Inconclusive;
        return validated;
    }

    private static void Fill(ValidatedScenario validated, Attempt attempt)
    {
        var m = attempt.Materialization;
        var e = attempt.Execution;
        validated.Expected = m.Expected ?? validated.Expected;
        validated.Request = m.Request ?? validated.Request;
        if (m.Preconditions.Count > 0 || m.Expected is not null) validated.Preconditions = m.Preconditions;
        validated.Bindings = m.Bindings;
        validated.ExecutionId = e.Id;
        validated.Observed = new ObservedResult { HttpStatus = e.Status, Body = e.ResponseBody, Exception = e.Exception, ElapsedMs = e.ElapsedMs };
        validated.Evidence.AddRange(attempt.Match.Evidence);
        if (m.Changes.Count > 0) validated.Evidence.Add($"alterado do baseline: {string.Join("; ", m.Changes)}");
        if (attempt.Match.Level == MatchLevel.Partial) validated.Reasons.AddRange(attempt.Match.Reasons);
        if (m.UnverifiedState.Count > 0 && attempt.Match.Level == MatchLevel.Confirmed)
            validated.Evidence.Add($"estado assumido (não consultado): {string.Join("; ", m.UnverifiedState)}");
    }

    /// <summary>Evidência para retirar o cenário, ou nulo quando ela não basta.</summary>
    private static List<string>? UnreachableEvidence(Scenario scenario, ScenarioState state, ScenarioMatrix matrix)
    {
        var executed = state.Attempts.Where(a => !a.Execution.Blocked && a.Execution.Status is not null).ToList();
        if (executed.Count < 2 || executed.Any(a => a.Match.Level != MatchLevel.Mismatch)) return null;
        // Estado suposto pelo solver (ex.: um id que a IA achou que existia) não serve de prova.
        if (executed.Any(a => a.Materialization.UnverifiedState.Count > 0 || a.Materialization.Assumptions.Count > 0)) return null;
        var signatures = executed.Select(a => a.Match.Observed).Distinct().ToList();
        if (signatures.Count != 1) return null;
        var explained = matrix.Scenarios.Where(s => s.Id != scenario.Id && OutcomeMatcher.ExplainedBy(s.Expected, executed[0].Execution)).Select(s => s.Id).ToList();
        if (explained.Count == 0) return null;

        return
        [
            $"{executed.Count} execuções ({string.Join(", ", executed.Select(a => a.Execution.Id))}) com payload aceito pelo solver para o cenário (e todo o estado lido comprovado) terminaram sempre em {signatures[0]}",
            $"resultado previsto para {string.Join(", ", explained)}",
            $"IA: {state.Justification ?? "o código não permite atingir o cenário"}",
        ];
    }

    /// <summary>Execuções do endpoint cujo resultado nenhum cenário da matriz prevê.</summary>
    private static IEnumerable<ValidatedScenario> Discovered(ScenarioMatrix matrix, IReadOnlyList<RuntimeExecution> executions)
    {
        var seen = new HashSet<string>();
        var index = 0;
        foreach (var e in executions)
        {
            if (e.Phase == RuntimePhases.Acquisition || e.ScenarioId is null || e.Blocked) continue;
            var texts = Evidence.Texts(e.ResponseBody);
            if (e.Status is not null && matrix.Scenarios.Any(s => OutcomeMatcher.ExplainedBy(s.Expected, e, texts))) continue;

            var message = Evidence.MainMessage(e.ResponseBody) ?? e.Exception;
            var signature = $"{e.Status}|{Evidence.Normalize(message ?? "")}";
            if (!seen.Add(signature)) continue;

            var success = e.Status is >= 200 and < 300;
            yield return new ValidatedScenario
            {
                Id = $"RT-{++index:00}",
                Status = ScenarioValidationStatuses.Discovered,
                Kind = success ? ScenarioKinds.Success : ScenarioKinds.Rule,
                Title = e.Status is { } status
                    ? $"HTTP {status}{(message is null ? "" : $": {ScenarioContext.Truncate(message, 120)}")} (ao executar {e.ScenarioId})"
                    : $"Sem resposta da API: {e.Exception} (ao executar {e.ScenarioId})",
                Expected = new ScenarioExpectation
                {
                    Outcome = success ? "sucesso" : "erro",
                    HttpStatus = e.Status,
                    StatusSource = "runtime",
                    Messages = message is null ? [] : [message],
                    Exception = e.Exception,
                },
                Request = new ScenarioRequest { Method = e.Method, Url = e.Url, Headers = e.Headers, Body = e.RequestBody?.DeepClone() },
                Observed = new ObservedResult { HttpStatus = e.Status, Body = e.ResponseBody, Exception = e.Exception, ElapsedMs = e.ElapsedMs },
                ExecutionId = e.Id,
                Attempts = 1,
                Evidence = [$"observado em {e.Id} ao tentar {e.ScenarioId}"],
                Reasons = ["A análise estática não previu este resultado: confira a regra no código (ou no tratamento de erros) que o produz."],
            };
        }
    }
}
