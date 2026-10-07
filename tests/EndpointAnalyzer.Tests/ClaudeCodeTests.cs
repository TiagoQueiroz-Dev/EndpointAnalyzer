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

    // Formato do cmd-shim gerado pelo npm (ex.: %APPDATA%\npm\claude.cmd).
    private const string NpmShim = """
        @ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\node.exe" (
          SET "_prog=%dp0%\node.exe"
        ) ELSE (
          SET "_prog=node"
          SET PATHEXT=%PATHEXT:;.JS;=;%
        )

        endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\node_modules\@anthropic-ai\claude-code\TARGET" %*
        """;

    [Fact]
    public void Shim_do_npm_com_script_executa_node_e_o_script_sem_cmd()
    {
        using var dir = new TempDir();
        var script = dir.File(@"node_modules\@anthropic-ai\claude-code\cli.js");
        var node = dir.File("node.exe");
        var shim = dir.File("claude.cmd", NpmShim.Replace("TARGET", "cli.js"));

        var command = ClaudeExecutable.Resolve(shim);

        Assert.Equal(node, command.FileName);
        Assert.Equal([script], command.PrefixArguments);
    }

    [Fact]
    public void Shim_do_npm_com_binario_nativo_executa_o_binario()
    {
        using var dir = new TempDir();
        var binary = dir.File(@"node_modules\@anthropic-ai\claude-code\bin\claude.exe");
        dir.File("node.exe");
        var shim = dir.File("claude.cmd", NpmShim.Replace("TARGET", @"bin\claude.exe"));

        var command = ClaudeExecutable.Resolve(shim);

        Assert.Equal(binary, command.FileName);
        Assert.Empty(command.PrefixArguments);
    }

    [Fact]
    public void Caminho_configurado_sem_extensao_encontra_o_exe()
    {
        using var dir = new TempDir();
        var exe = dir.File("claude.exe");

        var command = ClaudeExecutable.Resolve(Path.Combine(dir.Path, "claude"));

        Assert.Equal(exe, command.FileName);
    }

    [Fact]
    public void Processo_usa_o_login_do_projeto_e_nao_herda_credenciais_do_ambiente()
    {
        using var dir = new TempDir();
        var options = new ClaudeCodeOptions
        {
            Executable = dir.File("claude.exe"),
            ConfigDirectory = Path.Combine(dir.Path, "config"),
        };
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-teste");
        try
        {
            var info = ProcessRunner.StartInfo(options, ["auth", "status"]);

            Assert.Equal(options.ConfigDirectory, info.Environment["CLAUDE_CONFIG_DIR"]);
            Assert.False(info.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.True(Directory.Exists(options.ConfigDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public void Nao_encontrado_informa_onde_procurou()
    {
        var ex = Assert.Throws<ClaudeCodeNotInstalledException>(() => ClaudeExecutable.Resolve("claude-inexistente-" + Guid.NewGuid().ToString("N")));

        Assert.Contains("PATH", ex.Message);
        Assert.Contains(Path.Combine(".local", "bin"), ex.Message);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("claude-exe-").FullName;

        public string File(string relative, string content = "")
        {
            var file = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllText(file, content);
            return file;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
