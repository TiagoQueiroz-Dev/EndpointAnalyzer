using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Application;

namespace EndpointAnalyzer.Tests;

public class ClaudeCodeTests
{
    [Fact]
    public void Le_a_saida_estruturada_do_claude_p()
    {
        const string output = """
            {"type":"result","subtype":"success","is_error":false,"result":"...",
             "structured_output":{"summary":"Cria uma programação.","businessRules":[{"id":"REGRA-001","description":"d","confidence":0.95,"evidence":{"file":"a.cs","method":"m","line":10,"code":"c"}}],
             "validations":[],"entityChanges":[],"uncertainties":["x"]}}
            """;

        var result = ClaudeCodeProvider.ParseResult(output, "");

        Assert.Equal("Cria uma programação.", result.Summary);
        Assert.Equal(10, result.BusinessRules[0].Evidence!.Line);
        Assert.Equal(["x"], result.Uncertainties);
    }

    [Fact]
    public void Erro_do_claude_p_vira_excecao_com_a_mensagem()
    {
        const string output = """{"type":"result","subtype":"error","is_error":true,"result":"Not logged in · Please run /login"}""";

        var ex = Assert.Throws<AiProviderException>(() => ClaudeCodeProvider.ParseResult(output, ""));
        Assert.Contains("Not logged in", ex.Message);
    }

    [Fact]
    public void Saida_que_nao_e_json_vira_excecao() =>
        Assert.Throws<AiProviderException>(() => ClaudeCodeProvider.ParseResult("command not found", "erro"));

    [Fact]
    public async Task Sem_Claude_Code_instalado_status_indica_e_nao_ha_ia()
    {
        var options = new ClaudeCodeOptions { Executable = "claude-inexistente-" + Guid.NewGuid().ToString("N") };
        var auth = new ClaudeCodeAuth(options);
        var router = new AiProviderRouter(new ClaudeCodeProvider(options), auth) { Mode = AiMode.Subscription };

        var status = await auth.GetStatusAsync();

        Assert.False(status.CliInstalled);
        Assert.False(status.UsesSubscription);
        Assert.Null(await router.SelectAsync());
    }
}
