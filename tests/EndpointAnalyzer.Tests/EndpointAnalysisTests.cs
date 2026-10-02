using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class EndpointAnalysisTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Post_monta_call_graph_resolvendo_interfaces()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");

        Assert.Equal("ProgramacaoController.Criar", context.CallGraph[0]);
        Assert.Contains("ProgramacaoService.Criar", context.CallGraph);
        Assert.Contains("ProgramacaoRepository.Adicionar", context.CallGraph);

        // Notificação por e-mail é infraestrutura: fica só no grafo técnico.
        Assert.DoesNotContain("EmailNotificacaoService.Notificar", context.CallGraph);
        Assert.Contains(context.CallTree!.Flatten(), n => n.FullName == "EmailNotificacaoService.Notificar");

        var service = Assert.Single(context.CallTree!.Children);
        Assert.Equal("IProgramacaoService", service.ResolvedFrom);
        Assert.Equal("DI", service.Resolution);
        // Construtores de exceção não fazem parte do fluxo.
        Assert.DoesNotContain(context.CallGraph, m => m.Contains("Exception"));
    }

    [Fact]
    public async Task Post_detecta_regras_e_validacoes()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");

        var data = Assert.Single(context.Conditions, c => c.Expression == "request.Data < DateTime.Today");
        Assert.Equal("throw RegraNegocioException", data.Action);
        Assert.Equal("Data inválida: a programação não pode estar no passado.", data.Message);
        Assert.Equal("SampleApi/Services/ProgramacaoService.cs", data.SourceFile);

        Assert.Contains(context.Conditions, c => c.Kind == ConditionKinds.Guard && c.Message == "Veículo não encontrado.");
        Assert.Contains(context.Conditions, c => c.Kind == ConditionKinds.Validation && c.Message == "A data é obrigatória.");
        Assert.Contains(context.Conditions, c => c.Kind == ConditionKinds.Validation && c.Message == "Informe a data da programação.");
    }

    [Fact]
    public async Task Post_detecta_insert_update_e_condicoes_dos_campos()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");

        var insert = Assert.Single(context.EntityChanges, c => c is { Entity: "Programacao", Operation: EntityOperations.Insert });
        Assert.Equal(["Data", "DataInicio", "Status", "Observacao", "VeiculoId"], insert.Properties);
        Assert.Equal("request.Data >= DateTime.Today", insert.Condition);
        Assert.Contains(insert.PropertyChanges, p => p is { Property: "Status", Value: "ProgramacaoStatus.Pendente" });

        var veiculoId = Assert.Single(insert.PropertyChanges, p => p.Property == "VeiculoId");
        Assert.Contains("request.VeiculoId != null", veiculoId.Condition);
        Assert.Contains("veiculo.Disponivel", veiculoId.Condition);

        var veiculo = Assert.Single(context.EntityChanges, c => c is { Entity: "Veiculo", Operation: EntityOperations.Update });
        Assert.Equal(["Disponivel"], veiculo.Properties);

        var save = Assert.Single(context.PersistencePoints);
        Assert.Equal("ProgramacaoRepository.Adicionar", save.Method);
    }

    [Fact]
    public async Task Put_detecta_alteracoes_condicionais()
    {
        var context = await fixture.ContextAsync("PUT /api/programacoes/{id}");

        var update = Assert.Single(context.EntityChanges);
        Assert.Equal(EntityOperations.Update, update.Operation);
        Assert.Equal("programacao.Status != ProgramacaoStatus.Cancelada", update.Condition);

        var veiculoId = Assert.Single(update.PropertyChanges, p => p.Property == "VeiculoId");
        Assert.Equal("programacao.Status != ProgramacaoStatus.Cancelada && request.VeiculoId != null", veiculoId.Condition);

        Assert.Contains(context.Conditions, c => c.Message == "Programação cancelada não pode ser alterada.");
        Assert.Contains(context.CallGraph, m => m == "ProgramacaoService.ObterOuFalhar");
    }

    [Fact]
    public async Task Patch_finalizar_segue_metodo_da_entidade()
    {
        var context = await fixture.ContextAsync("PATCH /api/programacoes/{id}/finalizar");

        Assert.Contains("Programacao.Finalizar", context.CallGraph);

        var update = Assert.Single(context.EntityChanges);
        Assert.Equal(["Status", "DataFim", "UsuarioFinalizacaoId"], update.Properties);
        Assert.Equal("usuarioId > 0 && Status == ProgramacaoStatus.EmAndamento", update.Condition);

        var badRequest = Assert.Single(context.Conditions, c => c.Expression == "usuarioId <= 0");
        Assert.Equal("return BadRequest", badRequest.Action);
    }

    [Fact]
    public async Task Patch_cancelar_traduz_default_do_switch()
    {
        var context = await fixture.ContextAsync("PATCH /api/programacoes/{id}/cancelar");

        var update = Assert.Single(context.EntityChanges);
        Assert.Equal(
            "programacao.Status != ProgramacaoStatus.Finalizada && programacao.Status != ProgramacaoStatus.Cancelada",
            update.Condition);
        Assert.Contains(context.Conditions, c => c.Kind == ConditionKinds.Switch);
    }

    [Fact]
    public async Task Contexto_nao_envia_segredos_e_inclui_tipos_relevantes()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");

        // Infraestrutura (e-mail) não vai para a IA; e nenhum código enviado contém o segredo.
        Assert.DoesNotContain(context.Methods, m => m.Name == "EmailNotificacaoService.Notificar");
        Assert.DoesNotContain("senha-super-secreta-123", string.Join("\n", context.Methods.Select(m => m.Code)));
        Assert.Contains(context.Methods, m => m.Name == "ProgramacaoService.Criar");
        Assert.Contains(context.Types, t => t.Name == "CriarProgramacaoRequest");
        Assert.Contains(context.Types, t => t.Name == "ProgramacaoStatus");
        Assert.Contains(context.Types, t => t.Name == "Programacao");
    }

    [Fact]
    public async Task Analise_com_ia_usa_cache_quando_codigo_nao_mudou()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "PATCH /api/programacoes/{id}/finalizar");
        var before = fixture.Ai.Calls;

        var first = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint);
        var second = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(before + 1, fixture.Ai.Calls);
        Assert.Equal("fake-model", second.Version.AiModel);
        Assert.Equal(first.Ai!.Summary, second.Ai!.Summary);
    }

    [Fact]
    public async Task Mostra_o_codigo_da_regra_destacando_o_if_inteiro()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var rule = Assert.Single(context.Conditions, c => c.Expression == "request.Data < DateTime.Today");

        var view = await fixture.Sources.GetAsync(fixture.SolutionPath, rule.SourceFile, rule.SourceLine, null);

        Assert.NotNull(view);
        Assert.Equal(rule.SourceFile, view.File);
        Assert.Equal(rule.SourceLine, view.Line);
        Assert.Contains("if (request.Data < DateTime.Today)", view.Lines[view.Line - 1]);
        Assert.Contains("Data inválida", string.Join("\n", view.Lines.Skip(view.Line - 1).Take(view.EndLine - view.Line + 1)));
    }

    [Fact]
    public async Task Encontra_a_origem_de_todas_as_condicoes_do_registro()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var methods = context.CallTree!.Flatten()
            .Where(n => n.SourceFile != "")
            .Select(n => new MethodLocation(n.SourceFile, n.SourceLine))
            .ToList();
        Assert.NotEmpty(context.ConditionRegistry);

        foreach (var (id, condition) in context.ConditionRegistry)
        {
            var view = await fixture.Sources.FindConditionAsync(fixture.SolutionPath, condition, methods);
            Assert.True(view is not null, $"{id}: origem de '{condition}' não encontrada");
            Assert.InRange(view.Line, 1, view.EndLine);
        }
    }

    [Fact]
    public async Task Nao_mostra_arquivo_fora_da_solucao()
    {
        Assert.Null(await fixture.Sources.GetAsync(fixture.SolutionPath, "../../../Windows/win.ini", 1, null));
    }

    [Fact]
    public async Task Encontra_endpoint_por_controller_e_action()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "ProgramacaoController.Atualizar");
        Assert.Equal("PUT /api/programacoes/{id}", endpoint.Id);
    }
}
