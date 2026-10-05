using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Runtime;

public enum MatchLevel
{
    /// <summary>O resultado observado é outro (status diferente, outra regra).</summary>
    Mismatch,

    /// <summary>Compatível com o esperado, mas sem evidência suficiente para confirmar (status compartilhado, ramo sem estado comprovado).</summary>
    Partial,

    /// <summary>Comportamento reproduzido: status e mensagem (ou campo da validação) conferem.</summary>
    Confirmed,
}

/// <param name="Retryable">Outra tentativa (com outros dados) pode chegar ao esperado.</param>
/// <param name="Observed">Status e mensagem observados: "422 · Veículo indisponível.".</param>
public sealed record MatchResult(MatchLevel Level, List<string> Evidence, List<string> Reasons, bool Retryable, string Observed);

/// <summary>
/// Compara, de forma determinística, o resultado esperado de um cenário com a execução real. Só confirma quando o
/// status confere e há algo que identifica a regra: a mensagem esperada na resposta, o campo nos erros de validação
/// ou um status que nenhuma outra regra do endpoint produz. A IA nunca decide sozinha que um cenário foi confirmado.
/// </summary>
public static class OutcomeMatcher
{
    public static MatchResult Match(Scenario scenario, ScenarioExpectation expected, ScenarioMaterialization? materialization,
        RuntimeExecution execution, ScenarioMatrix matrix)
    {
        var evidence = new List<string>();
        var reasons = new List<string>();
        var observed = Observed(execution);

        if (execution.Blocked)
            return new MatchResult(MatchLevel.Mismatch, evidence, [$"Requisição não executada: {execution.Reason}"], false, observed);
        if (execution.Status is not { } status)
            return new MatchResult(MatchLevel.Mismatch, evidence, [$"Sem resposta da API: {execution.Exception}"], true, observed);

        var success = status is >= 200 and < 300;
        var texts = Evidence.Texts(execution.ResponseBody);

        if (expected.Outcome == "sucesso")
        {
            if (expected.HttpStatus is { } wanted ? status != wanted : !success)
                return new MatchResult(MatchLevel.Mismatch, evidence,
                    [$"Esperado sucesso (HTTP {expected.HttpStatus?.ToString() ?? "2xx"}), observado {observed}."], true, observed);

            evidence.Add(expected.HttpStatus is null ? $"HTTP {status} (sucesso; status esperado não determinado)" : $"HTTP {status} igual ao esperado");
            AddState(materialization, evidence);
            if (materialization is { UnverifiedFocus.Count: > 0 } m)
            {
                reasons.Add($"O status confere, mas o que diferencia este cenário depende de estado sem valor comprovado: {string.Join("; ", m.UnverifiedFocus)}.");
                return new MatchResult(MatchLevel.Partial, evidence, reasons, true, observed);
            }
            return new MatchResult(MatchLevel.Confirmed, evidence, reasons, false, observed);
        }

        if (expected.HttpStatus is { } expectedStatus ? status != expectedStatus : success)
        {
            var explained = matrix.Scenarios.Where(s => s.Id != scenario.Id && ExplainedBy(s.Expected, execution, texts)).Select(s => s.Id).ToList();
            reasons.Add($"Esperado HTTP {expected.HttpStatus?.ToString() ?? "de erro"}{(expected.Messages.Count > 0 ? $" (\"{expected.Messages[0]}\")" : "")}, observado {observed}"
                + (explained.Count > 0 ? $" — resultado previsto para {string.Join(", ", explained)}." : "."));
            return new MatchResult(MatchLevel.Mismatch, evidence, reasons, true, observed);
        }
        evidence.Add($"HTTP {status} igual ao esperado");
        AddState(materialization, evidence);

        var messages = expected.Messages.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (messages.Count > 0)
        {
            if (messages.FirstOrDefault(m => Evidence.Contains(texts, m)) is { } found)
            {
                evidence.Add($"mensagem \"{found}\" encontrada na resposta");
                return new MatchResult(MatchLevel.Confirmed, evidence, reasons, false, observed);
            }

            // Validação com a mensagem padrão do framework (pode estar traduzida): basta o erro no campo certo.
            var field = expected.Rule?.StartsWith("model binding", StringComparison.Ordinal) == true
                ? scenario.Focus?.Detail?.Split(" = ", 2)[0]
                : expected.Rule?.Split(": ", 2)[0];
            var defaultMessage = scenario.Notes?.Any(n => n.StartsWith("Mensagem padrão", StringComparison.Ordinal)) == true
                || scenario.Notes?.Any(n => n.StartsWith("Mensagem do System.Text.Json", StringComparison.Ordinal)) == true;
            if (scenario.Kind == ScenarioKinds.Validation && field is not null && defaultMessage && Evidence.HasFieldError(execution.ResponseBody, field))
            {
                evidence.Add($"erro de validação no campo {field} (mensagem padrão do framework diferente da prevista)");
                return new MatchResult(MatchLevel.Confirmed, evidence, reasons, false, observed);
            }

            reasons.Add($"O status confere, mas a mensagem esperada (\"{messages[0]}\") não aparece na resposta: {observed}.");
            return new MatchResult(MatchLevel.Partial, evidence, reasons, true, observed);
        }

        // Sem mensagem: só o status identifica a regra se nenhuma outra regra produz o mesmo status.
        var shared = matrix.Scenarios
            .Where(s => s.Id != scenario.Id && s.Expected.Outcome == "erro" && s.Expected.HttpStatus == status && s.Expected.Rule != expected.Rule)
            .Select(s => s.Id).ToList();
        if (shared.Count > 0)
        {
            reasons.Add($"HTTP {status} também é o resultado de {string.Join(", ", shared)} e não há mensagem para diferenciar a regra.");
            return new MatchResult(MatchLevel.Partial, evidence, reasons, false, observed);
        }
        evidence.Add($"nenhuma outra regra do endpoint responde HTTP {status}");
        return new MatchResult(MatchLevel.Confirmed, evidence, reasons, false, observed);
    }

    /// <summary>O resultado da execução é o previsto para o cenário (mesmo status e, quando houver, a mensagem).</summary>
    public static bool ExplainedBy(ScenarioExpectation expected, RuntimeExecution execution, IReadOnlyList<string>? texts = null)
    {
        if (execution.Status is not { } status) return false;
        var success = status is >= 200 and < 300;
        if (expected.HttpStatus is { } wanted ? wanted != status : (expected.Outcome == "sucesso") != success) return false;
        texts ??= Evidence.Texts(execution.ResponseBody);
        var messages = expected.Messages.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        return messages.Count == 0 || messages.Any(m => Evidence.Contains(texts, m));
    }

    public static string Observed(RuntimeExecution execution)
    {
        if (execution.Blocked) return "não executada";
        if (execution.Status is not { } status) return execution.Exception ?? "sem resposta";
        var message = Evidence.MainMessage(execution.ResponseBody) ?? execution.Exception;
        return message is null ? $"HTTP {status}" : $"HTTP {status} · {ScenarioContext.Truncate(message, 160)}";
    }

    private static void AddState(ScenarioMaterialization? materialization, List<string> evidence)
    {
        if (materialization is { VerifiedState.Count: > 0 } m)
            evidence.Add($"estado real: {string.Join("; ", m.VerifiedState)}");
    }
}
