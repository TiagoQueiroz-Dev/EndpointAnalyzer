using System.Text.Json;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Geração de cenários pelos desfechos reais do endpoint (EndpointAnalyzer_Arvore_Geracao_Cenarios.md): pontos de
/// encerramento pelo código, retornos propagados entre métodos, um cenário por desfecho (ponto + efeitos), viabilidade
/// SAT/UNSAT/UNKNOWN e identidade estrutural estável.
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class ScenarioTreeTests(SampleSolutionFixture fixture)
{
    private const string Remessas = "POST /api/remessas";

    private async Task<ScenarioMatrix> MatrixAsync(string endpoint) => (await fixture.ContextAsync(endpoint)).Scenarios!;

    private async Task<ScenarioModel> GenerateAsync(string endpointId, ScenarioGenerationOptions? options = null)
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, endpointId);
        var context = await fixture.Service.BuildContextAsync(fixture.SolutionPath, endpoint);
        var solution = await fixture.Solutions.GetAsync(fixture.SolutionPath);
        var graph = await new CallGraphBuilder(new AnalyzerOptions(), new MethodResolver()).BuildAsync(solution, endpoint);
        return await new ScenarioGenerator(options).GenerateModelAsync(graph, context);
    }

    private static List<Scenario> WithMessage(ScenarioMatrix matrix, string message) =>
        matrix.Scenarios.Where(s => s.Expected.Messages.Contains(message)).ToList();

    [Fact]
    public async Task Mesma_validacao_por_dois_ramos_equivalentes_nao_duplica_o_cenario()
    {
        var matrix = await MatrixAsync(Remessas);

        // ValidarPlaca é chamada nos dois lados do if (request.Urgente): um ponto do código, uma folha, um cenário.
        var reservada = Assert.Single(WithMessage(matrix, "Placa reservada."));
        Assert.Equal("AAA0A00", (string)reservada.Request.Body!["placa"]!);
        Assert.Single(matrix.Flow!.Nodes, n => n.Kind == "terminal" && n.Key == reservada.Terminal);
        Assert.Single(matrix.Scenarios, s => s.Terminal == reservada.Terminal);

        // O caminho até a regra é qualquer uma das duas chamadas: com Urgente verdadeiro ou falso, a mesma folha.
        var model = await GenerateAsync(Remessas);
        var id = model.Matrix.Scenarios.Single(s => s.Expected.Messages.Contains("Placa reservada.")).Id;
        foreach (var urgente in new[] { "true", "false" })
        {
            var m = model.Materialize(id, [new ScenarioBinding("request.Urgente", urgente)]);
            Assert.True(m.Success, m.Error);
            Assert.Equal(["Placa reservada."], m.Expected!.Messages);
        }
    }

    [Fact]
    public async Task Regras_diferentes_com_a_mesma_resposta_continuam_distintas()
    {
        var matrix = await MatrixAsync(Remessas);

        var invalidas = WithMessage(matrix, "Remessa inválida.");
        Assert.Equal(2, invalidas.Count);
        Assert.All(invalidas, s => Assert.Equal(422, s.Expected.HttpStatus));
        Assert.Equal(2, invalidas.Select(s => s.Terminal).Distinct().Count());
        Assert.Equal(2, invalidas.Select(s => s.Key).Distinct().Count());
        Assert.Equal(["BLOQUEADA", "CANCELADA"], invalidas.Select(s => (string)s.Request.Body!["referencia"]!).Order());
    }

    [Fact]
    public async Task Guardas_anteriores_passam_e_o_payload_atinge_a_folha_alvo()
    {
        var matrix = await MatrixAsync(Remessas);
        var leaves = matrix.Flow!.Nodes.Where(n => n.Kind == "terminal").Select(n => n.Key).ToHashSet();

        // A segunda regra só é alcançada com a primeira passando.
        var bloqueada = Assert.Single(matrix.Scenarios, s => s.Request.Body?["referencia"]?.GetValue<string>() == "BLOQUEADA");
        Assert.Contains("BLOQUEADA", bloqueada.Terminal);

        foreach (var scenario in matrix.Scenarios)
        {
            Assert.NotNull(scenario.Key);
            Assert.Contains(scenario.Terminal!, leaves);
            Assert.NotNull(scenario.Feasibility);
            // Com payload, o caminho é viável; sem payload, a linha diz por quê.
            if (scenario.NoPayload is null) Assert.NotEqual(PathFeasibilities.Unsat, scenario.Feasibility);
            else Assert.Null(scenario.Request.Body);
        }
    }

    [Fact]
    public async Task Retorno_antecipado_sem_escrita_e_sucesso_com_escrita_ficam_separados()
    {
        var matrix = await MatrixAsync(Remessas);

        var rascunho = Assert.Single(matrix.Scenarios, s => s.Focus?.Expression == "request.Rascunho");
        var principal = matrix.Scenarios.First(s => s is { Kind: ScenarioKinds.Success, Focus: null });
        Assert.Equal(principal.Expected.HttpStatus, rascunho.Expected.HttpStatus);
        Assert.Equal("sucesso", rascunho.Expected.Outcome);
        Assert.False(rascunho.Expected.Persisted);
        Assert.Empty(rascunho.Expected.Effects);
        Assert.True(principal.Expected.Persisted);
        Assert.NotEqual(principal.Terminal, rascunho.Terminal);
    }

    [Fact]
    public async Task Regra_depois_da_persistencia_continua_sendo_percorrida()
    {
        var matrix = await MatrixAsync(Remessas);

        var urgente = Assert.Single(WithMessage(matrix, "Prioridade alta exige remessa urgente."));
        Assert.Equal("erro", urgente.Expected.Outcome);
        Assert.Equal(422, urgente.Expected.HttpStatus);
        // O SaveChanges executou antes da regra: a inclusão chega ao banco mesmo com o erro.
        Assert.True(urgente.Expected.Persisted);
        Assert.Contains(urgente.Expected.Effects, e => e is { Target: "Remessa", Kind: EntityOperations.Insert });
    }

    [Fact]
    public async Task Required_comprimento_e_faixa_geram_um_cenario_por_validacao()
    {
        var matrix = await MatrixAsync(Remessas);

        // Cada validação é um desfecho: null/vazio/espaços, 6/8 caracteres e 0/6 não multiplicam cenários.
        foreach (var message in new[] { "The Placa field is required.", "The field Placa must be a string with a minimum length of 7 and a maximum length of 7.", "The field Prioridade must be between 1 and 5." })
            Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Validation && s.Expected.Messages.Contains(message));
        Assert.DoesNotContain(matrix.Scenarios, s => s.Kind == ScenarioKinds.Boundary);
        Assert.Equal(matrix.Scenarios.Count, matrix.Scenarios.Select(s => s.Terminal).Distinct().Count());
    }

    [Fact]
    public async Task Laco_so_gera_cenario_quando_muda_os_efeitos()
    {
        var matrix = await MatrixAsync(Remessas);

        // Sem itens o laço não insere ItemRemessa: outro conjunto de efeitos, outro desfecho. Duas iterações não são outro desfecho.
        var semItens = Assert.Single(matrix.Scenarios, s => s.Request.Body?["itens"]?.AsArray().Count == 0 && s.Expected.Outcome == "sucesso");
        Assert.DoesNotContain(semItens.Expected.Effects, e => e.Target == "ItemRemessa");
        Assert.True(semItens.Expected.Persisted);
        Assert.Equal(("request.Itens.Count > 0", false), (semItens.Focus!.Expression, semItens.Focus.Value));
        Assert.DoesNotContain(matrix.Scenarios, s => s.Request.Body?["itens"]?.AsArray().Count == 2);
        Assert.DoesNotContain(matrix.Notes, n => n.StartsWith("Laços cobertos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retorno_bool_de_metodo_chamado_e_propagado_e_dois_returns_falsos_sao_um_desfecho()
    {
        var matrix = await MatrixAsync("PUT /api/remessas/{id}/confirmar");

        // VolumesValidos devolve false por dois caminhos (<= 0 e > 100): o controller responde o mesmo BadRequest — um cenário.
        var invalidos = Assert.Single(matrix.Scenarios, s => s.Expected.Messages.Contains("Quantidade de volumes inválida."));
        Assert.Equal(400, invalidos.Expected.HttpStatus);
        var volumes = (long)invalidos.Request.Body!["volumes"]!;
        Assert.True(volumes is <= 0 or > 100, $"volumes = {volumes}");
        // A condição é a do método chamado, não uma suposição sobre o retorno dele.
        Assert.DoesNotContain(invalidos.Preconditions, p => p.Description.Contains("VolumesValidos", StringComparison.Ordinal));
        Assert.True((long)matrix.Scenarios[0].Request.Body!["volumes"]! is > 0 and <= 100);
    }

    [Fact]
    public async Task Null_devolvido_pelo_service_decide_o_ternario_do_controller()
    {
        var matrix = await MatrixAsync("PUT /api/remessas/{id}/confirmar");

        // return remessa is null ? NotFound() : Ok(remessa.Id): o lado NotFound é um ponto de encerramento, e o null vem
        // do return do service quando a consulta não encontra a remessa.
        var naoEncontrada = Assert.Single(matrix.Scenarios, s => s.Expected.HttpStatus == 404);
        Assert.Equal(ScenarioKinds.Rule, naoEncontrada.Kind);
        Assert.Contains(naoEncontrada.Preconditions, p => p.Description.StartsWith("Não existe Remessa", StringComparison.Ordinal));
        Assert.False(naoEncontrada.Expected.Persisted);

        var principal = matrix.Scenarios[0];
        Assert.Equal(200, principal.Expected.HttpStatus);
        Assert.True(principal.Expected.Persisted);
        Assert.Contains(principal.Expected.Effects, e => e is { Target: "Remessa", Kind: EntityOperations.Update });

        // Um cenário por desfecho: sucesso, volumes inválidos, não encontrada e tipo inválido no JSON.
        Assert.Equal(4, matrix.Scenarios.Count);
        Assert.All(matrix.Scenarios, s => Assert.Null(s.NoPayload));
    }

    [Fact]
    public async Task Barramento_repassa_o_retorno_do_handler_e_o_lado_inalcancavel_do_ternario_fica_unsat()
    {
        var matrix = await MatrixAsync("PUT /api/pedidos/{id}/cancelar");

        // ok = bus.EnviarComando(...) → mediator.Send → Handle, que só devolve true (ou lança): BadRequest nunca acontece.
        var badRequest = Assert.Single(matrix.Scenarios, s => s.Terminal!.Contains("PedidoController.Cancelar|return|!ok", StringComparison.Ordinal));
        Assert.Equal(PathFeasibilities.Unsat, badRequest.Feasibility);
        Assert.False(string.IsNullOrWhiteSpace(badRequest.NoPayload));
        Assert.DoesNotContain(matrix.Scenarios.SelectMany(s => s.Preconditions), p => p.Description.Contains("mediator.Send", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Toda_folha_da_arvore_tem_linha_na_matriz_e_a_sem_payload_explica_o_motivo()
    {
        foreach (var endpoint in new[] { Remessas, "POST /api/programacoes-transporte/{id}/origens", "POST /api/programacoes", "PATCH /api/programacoes/{id}/cancelar" })
        {
            var matrix = await MatrixAsync(endpoint);
            var leaves = matrix.Flow!.Nodes.Where(n => n.Kind == "terminal").Select(n => n.Key).Order().ToList();
            Assert.Equal(leaves, matrix.Scenarios.Select(s => s.Terminal!).Distinct().Order().ToList());
            foreach (var scenario in matrix.Scenarios)
            {
                if (scenario.Feasibility == PathFeasibilities.Unsat) Assert.NotNull(scenario.NoPayload);
                if (scenario.NoPayload is not null) Assert.False(string.IsNullOrWhiteSpace(scenario.NoPayload), $"{endpoint}: {scenario.Terminal}");
            }
            Assert.DoesNotContain(matrix.Notes, n => n.StartsWith("Limite de", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Catch_notificacao_delegada_e_validacao_automatica_tem_desfechos_corretos()
    {
        var matrix = await MatrixAsync("POST /api/programacoes-transporte/{id}/origens");
        var leaves = matrix.Flow!.Nodes.Where(n => n.Kind == "terminal").ToList();
        Scenario Row(FlowNodeInfo leaf) => Assert.Single(matrix.Scenarios, s => s.Terminal == leaf.Key);

        // catch que notifica: a exceção não é traduzível, então a folha é UNKNOWN com um cenário sob suposição.
        var commit = Row(Assert.Single(leaves, l => l.Label.StartsWith("exceção capturada", StringComparison.Ordinal)));
        Assert.Equal(PathFeasibilities.Unknown, commit.Feasibility);
        Assert.Null(commit.NoPayload);

        // if (!Existe(...)) return: a regra é a do método chamado (notifica dentro do próprio if), não outra folha.
        Assert.Single(leaves, l => l.Label == "EnderecoId não existe");
        Assert.DoesNotContain(leaves, l => l.Key.Contains("Existe<int, Endereco>", StringComparison.Ordinal));

        // if (ModelState.IsValid) return ApiResponse(...): é o return que encerra a requisição no caminho principal.
        var modelState = Row(Assert.Single(leaves, l => l.Key.Contains("ModelState.IsValid", StringComparison.Ordinal)));
        var principal = matrix.Scenarios.First(s => s is { Kind: ScenarioKinds.Success, Focus: null });
        Assert.Equal(principal.Id, modelState.Id);
        Assert.Null(modelState.NoPayload);

        // O return depois dele nunca acontece: UNSAT, linha sem payload com o motivo.
        var final = Row(Assert.Single(leaves, l => l.Key == TerminalKinds.Success));
        Assert.Equal(PathFeasibilities.Unsat, final.Feasibility);
        Assert.False(string.IsNullOrWhiteSpace(final.NoPayload));
        Assert.Null(final.Request.Body);

        // Return interno (Commit com notificações pendentes) não é folha: o valor volta ao chamador.
        Assert.DoesNotContain(leaves, l => l.Key.Contains("PossuiNotificacoes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mesmo_codigo_gera_a_mesma_arvore_chaves_e_cobertura()
    {
        var first = (await GenerateAsync(Remessas)).Matrix;
        var second = (await GenerateAsync(Remessas)).Matrix;

        Assert.Equal(first.Scenarios.Select(s => (s.Id, s.Key, s.Terminal, s.Feasibility, s.NoPayload, s.EquivalentTo)),
            second.Scenarios.Select(s => (s.Id, s.Key, s.Terminal, s.Feasibility, s.NoPayload, s.EquivalentTo)));
        Assert.Equal(JsonSerializer.Serialize(first.Flow), JsonSerializer.Serialize(second.Flow));
        Assert.Equal(first.Scenarios.Count, first.Scenarios.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public async Task Arvore_tem_raiz_na_entrada_validacoes_antes_da_action_e_folhas_ligadas_ao_codigo()
    {
        var matrix = await MatrixAsync(Remessas);
        var flow = matrix.Flow!;

        var entry = Assert.Single(flow.Nodes, n => n.Kind == "entry");
        Assert.Equal(entry.Id, flow.Nodes[0].Id);
        var gate = Assert.Single(flow.Nodes, n => n.Kind == "validation");
        Assert.Contains(flow.Edges, e => e.From == entry.Id && e.To == gate.Id);
        // Toda folha está ligada à árvore e tem linha na matriz; as regras apontam para o código.
        foreach (var node in flow.Nodes.Where(n => n.Kind == "terminal"))
        {
            Assert.Contains(flow.Edges, e => e.To == node.Id);
            Assert.Contains(matrix.Scenarios, s => s.Terminal == node.Key);
            if (node.Key.StartsWith(TerminalKinds.Rule + "|", StringComparison.Ordinal)) Assert.NotNull(node.Source);
        }
        // Ramo que converge (o laço com efeito): os dois lados seguem para o mesmo nó, sem duplicar o restante do fluxo.
        var loop = Assert.Single(flow.Nodes, n => n.Key.StartsWith("ramo|request.Itens.Count", StringComparison.Ordinal));
        Assert.Contains(flow.Edges, e => e.From == loop.Id && e.Sense == "true");
        Assert.Single(flow.Nodes, n => n.Label == "persiste as alterações (SaveChanges)");
    }

    [Fact]
    public async Task Limite_de_caminhos_deixa_desfechos_nao_decididos_nunca_inalcancaveis()
    {
        var full = await MatrixAsync(Remessas);
        var matrix = (await GenerateAsync(Remessas, new ScenarioGenerationOptions { MaxPaths = 3 })).Matrix;

        Assert.Contains(matrix.Notes, n => n.StartsWith("Limite de 3 caminhos de execução atingido", StringComparison.Ordinal));
        // Todo ponto de encerramento continua com linha; o que não foi alcançado fica UNKNOWN, nunca UNSAT.
        var points = full.Scenarios.Select(s => s.Terminal!).Where(t => !t.Contains("|efeitos:", StringComparison.Ordinal)).Order();
        Assert.Equal(points, matrix.Scenarios.Select(s => s.Terminal!).Where(t => !t.Contains("|efeitos:", StringComparison.Ordinal)).Order());
        Assert.DoesNotContain(matrix.Scenarios, s => s.Feasibility == PathFeasibilities.Unsat
            && full.Scenarios.Single(f => f.Terminal == s.Terminal).Feasibility != PathFeasibilities.Unsat);
    }

    [Fact]
    public void Solver_distingue_unsat_de_unknown()
    {
        Var Number(string key) => new() { Key = key, Kind = VarKind.Int, Origin = VarOrigin.Input, Display = key };
        var x = Number("in:x");
        var solver = new ConstraintSolver();

        var sat = solver.Check([new CmpPred(new VarTerm(x), ">", ConstTerm.Of(5), "x > 5")]);
        Assert.Equal(Feasibility.Sat, sat.Status);
        Assert.NotNull(sat.Assignment);

        var unsat = solver.Check([new CmpPred(new VarTerm(x), ">", ConstTerm.Of(5), "x > 5"), new CmpPred(new VarTerm(x), "<", ConstTerm.Of(3), "x < 3")]);
        Assert.Equal(Feasibility.Unsat, unsat.Status);
        Assert.Null(unsat.Assignment);

        // Comparação que o domínio não modela (texto com comprimento contra bool): suposição, nunca prova de UNSAT.
        var text = new Var { Key = "in:t", Kind = VarKind.String, Origin = VarOrigin.Input, Display = "t" };
        var odd = solver.Check([new CmpPred(new VarTerm(text, Measure.Length), "==", ConstTerm.Of(true), "t.Length == true")]);
        Assert.NotEqual(Feasibility.Unsat, odd.Status);
    }
}
