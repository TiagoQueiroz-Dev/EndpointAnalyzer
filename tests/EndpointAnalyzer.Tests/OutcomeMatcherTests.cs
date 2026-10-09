using System.Text.Json.Nodes;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;

namespace EndpointAnalyzer.Tests;

public class OutcomeMatcherTests
{
    private static Scenario BindingScenario() => new()
    {
        Id = "CEN-02",
        Kind = ScenarioKinds.Validation,
        Focus = new ScenarioFocus { Detail = "veiculoTipoId = \"abc\"" },
        Expected = new ScenarioExpectation
        {
            Outcome = "erro", HttpStatus = 400,
            Messages = ["The JSON value could not be converted to int. Path: $.veiculoTipoId"],
            Rule = "model binding: tipo do JSON incompatível",
        },
        Notes = ["Mensagem do System.Text.Json (pode variar com a versão e a configuração do JSON)."],
    };

    private static MatchResult Match(Scenario scenario, string body, int status = 400) =>
        OutcomeMatcher.Match(scenario, scenario.Expected, null,
            new RuntimeExecution { Status = status, ResponseBody = body },
            new ScenarioMatrix { Scenarios = [scenario] });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Mensagem_esperada_pode_ocorrer_em_qualquer_indice(int index)
    {
        var scenario = new Scenario
        {
            Id = "CEN-03", Kind = ScenarioKinds.Rule,
            Expected = new ScenarioExpectation { Outcome = "erro", HttpStatus = 400, Messages = ["Tipo de veículo não encontrado"] },
        };
        var errors = new JsonArray();
        for (var i = 0; i < 3; i++)
            errors.Add(new JsonObject { ["key"] = "", ["value"] = i == index ? scenario.Expected.Messages[0] : "Outro erro" });
        var result = Match(scenario, new JsonObject { ["errors"] = errors }.ToJsonString());
        Assert.Equal(MatchLevel.Confirmed, result.Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Erro_de_conversao_com_tipo_clr_e_key_vazia_identifica_campo_em_qualquer_indice(int index)
    {
        const string conversion = "The JSON value could not be converted to System.Int32. Path: $.veiculoTipoId | LineNumber: 0 | BytePositionInLine: 22.";
        var errors = new JsonArray();
        for (var i = 0; i < 3; i++)
            errors.Add(new JsonObject
            {
                ["key"] = "", ["value"] = i == index ? conversion : "The pAdicionarVeiculoV1Request field is required.",
            });
        var body = new JsonObject { ["success"] = false, ["data"] = 0, ["errors"] = errors }.ToJsonString();
        var result = Match(BindingScenario(), body);
        Assert.Equal(MatchLevel.Confirmed, result.Level);
        Assert.Contains(result.Evidence, e => e.Contains("veiculoTipoId"));
        Assert.Contains(conversion, EndpointAnalyzer.Runtime.Evidence.MainMessage(body));
    }

    [Theory]
    [InlineData("{\"errors\":[{\"key\":\"request.VeiculoTipoId\",\"value\":\"Valor inválido.\"}]}")]
    [InlineData("{\"errors\":[\"Erro no JSON. Path: $.veiculoTipoId | LineNumber: 0\"]}")]
    [InlineData("{\"errors\":{\"$.VeiculoTipoId\":[\"Valor inválido.\"]}}")]
    public void Formatos_de_validacao_identificam_o_campo(string body)
    {
        Assert.Equal(MatchLevel.Confirmed, Match(BindingScenario(), body).Level);
    }

    [Theory]
    [InlineData("{\"errors\":[{\"key\":\"\",\"value\":\"The pAdicionarVeiculoV1Request field is required.\"}]}")]
    [InlineData("{\"errors\":[{\"key\":\"\",\"value\":\"Conversion failed. Path: $.unidadeId | LineNumber: 0\"}]}")]
    [InlineData("{\"errors\":[{\"key\":\"\",\"value\":\"Conversion failed. Path: $.veiculoTipoIdExtra | LineNumber: 0\"}]}")]
    [InlineData("{\"errors\":[{\"key\":\"unidadeId\",\"value\":\"Invalid veiculoTipoId mentioned without a JSON path.\"}]}")]
    [InlineData("{\"errors\":[{\"key\":\"veiculoTipoId\",\"value\":\"\"}]}")]
    [InlineData("{\"errors\":{\"veiculoTipoId\":[]}}")]
    [InlineData("{\"errors\":[]}")]
    [InlineData("invalid JSON")]
    public void Outros_campos_mensagens_genericas_e_erros_vazios_nao_confirmam(string body)
    {
        Assert.Equal(MatchLevel.Partial, Match(BindingScenario(), body).Level);
    }

    [Fact]
    public void Campo_correto_com_status_diferente_nao_confirma()
    {
        Assert.Equal(MatchLevel.Mismatch, Match(BindingScenario(),
            "{\"errors\":[{\"key\":\"veiculoTipoId\",\"value\":\"Valor inválido.\"}]}", 422).Level);
    }
}
