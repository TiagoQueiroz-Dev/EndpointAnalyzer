using System.Text.Json;

namespace EndpointAnalyzer.AI.ClaudeCode;

/// <summary>
/// Configuração do uso da assinatura do Claude (Pro/Max/Team) através do Claude Code instalado na máquina.
/// </summary>
public class ClaudeCodeOptions
{
    /// <summary>Executável do Claude Code (precisa estar no PATH, ou informe o caminho completo).</summary>
    public string Executable { get; set; } = "claude";

    /// <summary>
    /// Configuração e login próprios do projeto (CLAUDE_CONFIG_DIR): entrar ou sair aqui não afeta o Claude Code do terminal,
    /// e o consumo é sempre da conta logada no projeto.
    /// </summary>
    public string ConfigDirectory { get; set; } = Path.Combine(ClaudeModelCatalog.DataDirectory, "claude");

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | xhigh | max. Vazio para modelos sem effort (Haiku 4.5).</summary>
    public string Effort { get; set; } = "high";

    public int TimeoutMinutes { get; set; } = 20;

    private static string SelectionFile => Path.Combine(ClaudeModelCatalog.DataDirectory, "claude-code-selection.json");

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
