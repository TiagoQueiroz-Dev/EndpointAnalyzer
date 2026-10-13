using System.Text.Json;
using EndpointAnalyzer.AI.ClaudeCode;

namespace EndpointAnalyzer.AI.Codex;

/// <summary>
/// Configuração do uso da conta do Codex (plano do ChatGPT: Plus, Pro, Business...) através do Codex CLI instalado na máquina.
/// </summary>
public class CodexOptions
{
    /// <summary>Executável do Codex (PATH, npm ou o CLI do app desktop; ou informe o caminho completo).</summary>
    public string Executable { get; set; } = "codex";

    /// <summary>
    /// Configuração e login próprios do projeto (CODEX_HOME): entrar ou sair aqui não afeta o Codex do terminal nem o app
    /// desktop, e o consumo é sempre do limite da conta logada no projeto.
    /// </summary>
    public string HomeDirectory { get; set; } = Path.Combine(ClaudeModelCatalog.DataDirectory, "codex");

    /// <summary>Vazio = o modelo padrão da conta (informado pelo Codex).</summary>
    public string Model { get; set; } = "";

    /// <summary>Reasoning effort (low | medium | high | xhigh...). Vazio = o padrão do modelo.</summary>
    public string Effort { get; set; } = "high";

    public int TimeoutMinutes { get; set; } = 20;

    /// <summary>Contas do Codex adicionais: cada uma com o próprio CODEX_HOME (login separado) e a lista ordenada.</summary>
    public string AccountsDirectory { get; set; } = Path.Combine(ClaudeModelCatalog.DataDirectory, "codex-accounts");

    /// <summary>As mesmas opções (executável, modelo e effort) com o login de outra conta.</summary>
    public CodexOptions WithHomeDirectory(string homeDirectory) => new()
    {
        Executable = Executable,
        HomeDirectory = homeDirectory,
        Model = Model,
        Effort = Effort,
        TimeoutMinutes = TimeoutMinutes,
        AccountsDirectory = AccountsDirectory,
    };

    private static string SelectionFile => Path.Combine(ClaudeModelCatalog.DataDirectory, "codex-selection.json");

    /// <summary>Aplica o modelo/effort escolhidos na interface (salvos em disco), se houver.</summary>
    public void LoadSelection()
    {
        try
        {
            if (!File.Exists(SelectionFile)) return;
            var saved = JsonSerializer.Deserialize<Selection>(File.ReadAllText(SelectionFile));
            if (saved is null || string.IsNullOrWhiteSpace(saved.Model)) return;
            Model = saved.Model;
            Effort = saved.Effort;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
    }

    public void SaveSelection(string model, string effort)
    {
        Model = model;
        Effort = effort;
        Directory.CreateDirectory(ClaudeModelCatalog.DataDirectory);
        File.WriteAllText(SelectionFile, JsonSerializer.Serialize(new Selection(model, effort)));
    }

    private sealed record Selection(string Model, string Effort);
}
