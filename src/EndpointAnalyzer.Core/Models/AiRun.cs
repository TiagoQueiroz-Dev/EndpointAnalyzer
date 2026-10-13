using System.Text.Json.Serialization;

namespace EndpointAnalyzer.Core.Models;

/// <summary>Motivo da troca de conta (ou provedor) durante a análise.</summary>
public static class AiHandoffReasons
{
    public const string UsageLimit = "limite-de-uso";

    public const string Authentication = "autenticacao";
}

/// <summary>
/// Contas e provedores da IA usados numa análise: em que ordem, quando houve troca (limite de uso ou sessão expirada) e,
/// se nenhuma conta tinha uso disponível, o que ficou pendente. A troca repete só a chamada que falhou, com o mesmo pedido.
/// </summary>
public class AiRunInfo
{
    /// <summary>Contas na ordem em que foram usadas.</summary>
    public List<AiRunAccount> Accounts { get; set; } = [];

    public List<AiHandoff> Handoffs { get; set; } = [];

    /// <summary>Nenhuma conta ou provedor tinha uso disponível: o progresso foi salvo e as etapas abaixo ficaram pendentes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiInterruption? Interruption { get; set; }

    /// <summary>Contas puladas no início (sem uso disponível ou sessão expirada desde uma análise anterior).</summary>
    public List<string> Notes { get; set; } = [];
}

public class AiRunAccount
{
    /// <summary>claude ou codex.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Id da conta cadastrada (contas Claude); nulo para o Codex e a API.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountId { get; set; }

    /// <summary>Como a conta aparece na interface (e-mail ou apelido).</summary>
    public string Label { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>Chamadas à IA respondidas por esta conta.</summary>
    public int Calls { get; set; }
}

public class AiHandoff
{
    public DateTimeOffset At { get; set; }

    /// <summary>Etapa cuja chamada falhou e foi repetida na próxima conta (ex.: "documentação", "adquirir dados (rodada 2)").</summary>
    public string Step { get; set; } = "";

    public string FromProvider { get; set; } = "";

    public string FromAccount { get; set; } = "";

    /// <summary>Nulo quando não havia próxima conta.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToProvider { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToAccount { get; set; }

    /// <summary>Um de <see cref="AiHandoffReasons"/>.</summary>
    public string Reason { get; set; } = "";

    public string Message { get; set; } = "";
}

public class AiInterruption
{
    /// <summary>Um de <see cref="AiHandoffReasons"/>.</summary>
    public string Reason { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Etapas que não terminaram (ex.: "Resumo: documentação da IA").</summary>
    public List<string> Pending { get; set; } = [];
}
