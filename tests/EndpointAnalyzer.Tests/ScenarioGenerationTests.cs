using EndpointAnalyzer.AI;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class ScenarioGenerationTests(SampleSolutionFixture fixture)
{
    private async Task<ScenarioMatrix> MatrixAsync(string endpoint)
    {
        var context = await fixture.ContextAsync(endpoint);
        Assert.NotNull(context.Scenarios);
        return context.Scenarios!;
    }

    private static Scenario Rule(ScenarioMatrix matrix, string message) =>
        Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Rule && s.Expected.Messages.Contains(message));

    [Fact]
    public async Task Caminho_feliz_cobre_o_ramo_opcional_com_o_estado_necessario()
    {
        var matrix = await MatrixAsync("POST /api/programacoes");

        var happy = matrix.Scenarios[0];
        Assert.Equal(ScenarioKinds.Success, happy.Kind);
        Assert.Equal("CEN-01", happy.Id);
        Assert.Equal(201, happy.Expected.HttpStatus);
        Assert.Equal("código", happy.Expected.StatusSource);
        Assert.True(happy.Expected.Persisted);

        // VeiculoId informado: exercita o vínculo do veículo (mais efeitos) e exige o veículo disponível.
        Assert.Equal(1, (long)happy.Request.Body!["veiculoId"]!);
        var veiculo = Assert.Single(happy.Preconditions);
        Assert.Equal("estado", veiculo.Kind);
        Assert.Contains("veiculos.ObterPorId(1)", veiculo.Description);
        Assert.Contains("Disponivel = true", veiculo.Description);

        var insert = Assert.Single(happy.Expected.Effects, e => e is { Target: "Programacao", Kind: EntityOperations.Insert });
        Assert.Contains(insert.Properties!, p => p.StartsWith("VeiculoId", StringComparison.Ordinal));
        Assert.Contains(happy.Expected.Effects, e => e is { Target: "Veiculo", Kind: EntityOperations.Update });

        // Sem veículo: variação da tabela de decisão, sem o UPDATE do veículo.
        var semVeiculo = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Success && s.Focus?.Expression == "request.VeiculoId == null");
        Assert.Null(semVeiculo.Request.Body!["veiculoId"]);
        Assert.DoesNotContain(semVeiculo.Expected.Effects, e => e.Target == "Veiculo");
        Assert.Empty(semVeiculo.Preconditions);
    }

    [Fact]
    public async Task Gera_um_cenario_por_regra_com_as_anteriores_passando()
    {
        var matrix = await MatrixAsync("POST /api/programacoes");
        var today = DateTime.Today;

        var passado = Rule(matrix, "Data inválida: a programação não pode estar no passado.");
        Assert.Equal(today.AddDays(-1), DateTime.Parse((string)passado.Request.Body!["data"]!));
        Assert.Equal("RegraNegocioException", passado.Expected.Exception);
        Assert.False(passado.Expected.Persisted);

        var naoEncontrado = Rule(matrix, "Veículo não encontrado.");
        Assert.Contains(naoEncontrado.Preconditions, p => p.Description.StartsWith("Não existe Veiculo", StringComparison.Ordinal));
        Assert.True(DateTime.Parse((string)naoEncontrado.Request.Body!["data"]!) >= today);

        var indisponivel = Rule(matrix, "Veículo indisponível.");
        Assert.Contains(indisponivel.Preconditions, p => p.Description.Contains("Disponivel = false"));

        // Limite da regra do código: hoje ainda é aceito.
        var hoje = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Boundary && s.Title.StartsWith("data = hoje", StringComparison.Ordinal));
        Assert.Equal(today, DateTime.Parse((string)hoje.Request.Body!["data"]!));
        Assert.Equal("sucesso", hoje.Expected.Outcome);
    }

    [Fact]
    public async Task Validacoes_geram_particoes_invalidas_e_valores_limite()
    {
        var matrix = await MatrixAsync("POST /api/programacoes");

        var acima = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Validation && s.Title.StartsWith("observacao com 501", StringComparison.Ordinal));
        Assert.Equal(501, ((string)acima.Request.Body!["observacao"]!).Length);
        Assert.Equal(400, acima.Expected.HttpStatus);
        Assert.Equal(ScenarioTechniques.BoundaryValue, acima.Technique);

        var limite = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Boundary && s.Title.StartsWith("observacao com 500", StringComparison.Ordinal));
        Assert.Equal("sucesso", limite.Expected.Outcome);

        // FluentValidation: GreaterThan(0).When(HasValue) → 0 é inválido, com a mensagem do WithMessage.
        var veiculoZero = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Validation && s.Expected.Messages.Contains("Veículo inválido."));
        Assert.Equal(0, (long)veiculoZero.Request.Body!["veiculoId"]!);

        // Tabela de decisão: o cenário de 501 caracteres só viola a validação de Observacao.
        var maxLength = Assert.Single(matrix.Conditions, c => c.Expression == "request.Observacao.Length > 500");
        Assert.True(acima.Decisions[maxLength.Id]);
        Assert.False(limite.Decisions[maxLength.Id]);
    }

    [Fact]
    public async Task Required_e_StringLength_geram_ausente_vazio_espacos_e_limites()
    {
        var matrix = await MatrixAsync("POST /api/veiculos");
        var validations = matrix.Scenarios.Where(s => s.Kind == ScenarioKinds.Validation).Select(s => s.Title).ToList();

        Assert.Contains("placa vazio (\"\")", validations);
        Assert.Contains("placa só com espaços", validations);
        Assert.Contains(validations, t => t.StartsWith("placa com 6", StringComparison.Ordinal));
        Assert.Contains(validations, t => t.StartsWith("placa com 8", StringComparison.Ordinal));

        var curta = Assert.Single(matrix.Scenarios, s => s.Title.StartsWith("placa com 6", StringComparison.Ordinal));
        Assert.Equal(["A placa deve ter 7 caracteres."], curta.Expected.Messages);

        // return Conflict(...) no controller: status do próprio código.
        var duplicada = Rule(matrix, "Já existe um veículo com esta placa.");
        Assert.Equal(409, duplicada.Expected.HttpStatus);
        Assert.Contains(duplicada.Preconditions, p => p.Description.Contains("AnyAsync") && p.Description.EndsWith("retorna true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Excecao_capturada_no_controller_usa_o_status_do_catch()
    {
        var matrix = await MatrixAsync("POST /veiculo/v1");

        var tipo = Rule(matrix, "Tipo de veículo não encontrado");
        Assert.Equal(400, tipo.Expected.HttpStatus);
        Assert.Equal("código", tipo.Expected.StatusSource);
        Assert.Contains(tipo.Preconditions, p => p.Description == "veiculoTipos.ExistePorId(1) retorna false");

        // A regra da placa depende do tipo de veículo exigir placa (estado consultado).
        var placa = Rule(matrix, "Placa obrigatória para esse tipo de veículo");
        Assert.Contains(placa.Preconditions, p => p.Description.Contains("veiculoTipos.ObterPorId(1)") && p.Description.Contains("PlacaObrigatoria = true"));

        // Tipo inválido no JSON: partição inválida de tipo, 400 automático do [ApiController].
        var tipoInvalido = Assert.Single(matrix.Scenarios, s => s.Title.StartsWith("veiculoTipoId com tipo inválido", StringComparison.Ordinal));
        Assert.Equal("abc", (string)tipoInvalido.Request.Body!["veiculoTipoId"]!);
        Assert.Equal(400, tipoInvalido.Expected.HttpStatus);
    }

    [Fact]
    public async Task Saida_antecipada_vira_cenario_de_sucesso_sem_alteracao()
    {
        var matrix = await MatrixAsync("PATCH /api/programacoes/{id}/cancelar");

        var jaCancelada = Assert.Single(matrix.Scenarios, s => s.Focus?.Expression == "programacao.Status == ProgramacaoStatus.Cancelada");
        Assert.Equal("sucesso", jaCancelada.Expected.Outcome);
        Assert.Equal(204, jaCancelada.Expected.HttpStatus);
        Assert.False(jaCancelada.Expected.Persisted);
        Assert.Contains(jaCancelada.Preconditions, p => p.Description.Contains("Status = ProgramacaoStatus.Cancelada"));

        var finalizada = Rule(matrix, "Programação finalizada não pode ser cancelada.");
        Assert.Contains(finalizada.Preconditions, p => p.Description.Contains("Status = ProgramacaoStatus.Finalizada"));

        // Mensagem interpolada com o valor do cenário.
        Assert.Contains(matrix.Scenarios, s => s.Expected.Messages.Contains("Programação 1 não encontrada."));
    }

    [Fact]
    public async Task Ramo_do_parametro_bool_muda_os_efeitos()
    {
        var matrix = await MatrixAsync("PUT /api/veiculos/{id}/disponibilidade");

        var disponivel = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Success && s.Request.Query!["disponivel"] == "true");
        var indisponivel = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Success && s.Request.Query!["disponivel"] == "false");
        Assert.Contains("UltimaProgramacaoId = null", disponivel.Expected.Effects.Single(e => e.Target == "Veiculo").Properties!);
        Assert.DoesNotContain("UltimaProgramacaoId = null", indisponivel.Expected.Effects.Single(e => e.Target == "Veiculo").Properties!);
        Assert.Equal("/api/veiculos/1/disponibilidade?disponivel=true", disponivel.Request.Url);

        var naoEncontrado = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Rule);
        Assert.Equal(404, naoEncontrado.Expected.HttpStatus);
        Assert.Empty(naoEncontrado.Expected.Messages);
    }

    [Fact]
    public async Task Restricoes_com_varias_variaveis_usam_o_Z3_e_o_middleware_define_o_status()
    {
        var matrix = await MatrixAsync("POST /api/cargas");

        var excede = Rule(matrix, "Carga excede a capacidade do veículo.");
        Assert.Equal("z3", excede.Solver);
        Assert.Equal(422, excede.Expected.HttpStatus);
        Assert.Equal("middleware", excede.Expected.StatusSource);

        // A regra da carga perigosa só é alcançada sem estourar a capacidade (a regra anterior passa).
        var perigosa = Rule(matrix, "Carga perigosa limitada à metade da capacidade.");
        var quantidade = (long)perigosa.Request.Body!["quantidade"]!;
        var peso = (decimal)perigosa.Request.Body!["pesoUnitarioKg"]!;
        var capacidade = decimal.Parse(perigosa.Preconditions.Single().Description.Split("CapacidadeKg = ")[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(peso * quantidade <= capacidade);
        Assert.True(quantidade > capacidade / 2);
        Assert.True(peso >= 0);

        var naoEncontrado = Rule(matrix, "Veículo não encontrado.");
        Assert.Equal(404, naoEncontrado.Expected.HttpStatus);
        Assert.Equal("middleware", naoEncontrado.Expected.StatusSource);

        Assert.Contains(matrix.Scenarios, s => s.Title.StartsWith("quantidade = 0", StringComparison.Ordinal) && s.Expected.HttpStatus == 400);
        Assert.Contains(matrix.Scenarios, s => s.Title.StartsWith("quantidade = 1000", StringComparison.Ordinal) && s.Expected.Outcome == "sucesso");
    }

    [Fact]
    public async Task Prompt_leva_os_cenarios_sem_datas_concretas()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var prompt = PromptBuilder.BuildUserPrompt(context);

        Assert.Contains("<scenario_matrix>", prompt);
        Assert.Contains("CEN-01", prompt);
        Assert.DoesNotContain(DateTime.Today.ToString("yyyy-MM-dd"), prompt);
        Assert.DoesNotContain(DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), prompt);
    }
}

public class ConstraintSolverTests
{
    private static Var Text(string key, bool nullable = true) => new()
    {
        Key = key, Kind = VarKind.String, Origin = VarOrigin.Input, Nullable = nullable, Display = key,
    };

    [Fact]
    public void Escolhe_o_valor_no_limite_mais_proximo_do_padrao()
    {
        var obs = Text("in:obs");
        var len = new VarTerm(obs, Measure.Length);
        var assignment = new ConstraintSolver().Solve([new CmpPred(len, ">", ConstTerm.Of(200), "obs.Length > 200")]);

        Assert.NotNull(assignment);
        Assert.Equal(201, assignment!.Get(obs).Length);
        Assert.False(assignment.UsedZ3);
    }

    [Fact]
    public void Prefere_valor_preenchido_ao_null_nas_disjuncoes()
    {
        var obs = Text("in:obs");
        // !(obs != null && obs.Length > 10) → null ou até 10 caracteres: escolhe o texto.
        var violation = Pred.And(Pred.Not(new NullPred(obs, "obs == null")), new CmpPred(new VarTerm(obs, Measure.Length), ">", ConstTerm.Of(10), "obs.Length > 10"));
        var assignment = new ConstraintSolver().Solve([Pred.Not(violation)]);

        Assert.False(assignment!.Get(obs).IsNull);
        Assert.True(assignment.Get(obs).Length <= 10);
    }

    [Fact]
    public void Restricao_aritmetica_vai_para_o_Z3()
    {
        Var Number(string key, VarKind kind) => new() { Key = key, Kind = kind, Origin = VarOrigin.Input, Display = key };
        var quantidade = Number("in:q", VarKind.Int);
        var capacidade = Number("st:cap", VarKind.Int);
        var goals = new Pred[]
        {
            new CmpPred(new VarTerm(quantidade), ">", new ArithTerm("/", new VarTerm(capacidade), ConstTerm.Of(2)), "q > cap / 2"),
            new CmpPred(new VarTerm(quantidade), "<=", ConstTerm.Of(10), "q <= 10"),
        };

        var assignment = new ConstraintSolver().Solve(goals);

        Assert.NotNull(assignment);
        Assert.True(assignment!.UsedZ3);
        Assert.True(assignment.Get(quantidade).Number > assignment.Get(capacidade).Number / 2);
        Assert.True(assignment.Get(quantidade).Number <= 10);
    }
}
