using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Critérios de validação da remoção de ruído (remocao-ruidos-analise-estatica.md, §31),
/// sobre samples/SampleApi/Cadastro: services/repositórios genéricos, barramento, DomainNotification e ApiController.
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class NoiseRemovalTests(SampleSolutionFixture fixture)
{
    private const string Endpoint = "POST /veiculo/v1";

    [Fact]
    public async Task Criterio1_TEntity_nao_aparece_como_entidade_final()
    {
        var context = await fixture.ContextAsync(Endpoint);

        Assert.DoesNotContain(context.EntityChanges, c => c.Entity == "TEntity");
        var insert = Assert.Single(context.EntityChanges, c => c.EffectClass == EffectClasses.Primary);
        Assert.Equal(("Veiculo", EntityOperations.Insert), (insert.Entity, insert.Operation));

        // ServiceBase<TEntity>.ExistePorId executa com TEntity = VeiculoTipo, no repositório do VeiculoTipo.
        var existe = context.CallTree!.Flatten().First(n => n.FullName == "VeiculoTipoService.ExistePorId");
        Assert.Equal("ServiceBase<VeiculoTipo>", existe.DeclaringType);
        Assert.Equal("VeiculoTipoRepository.ExistePorId", Assert.Single(existe.Children).FullName);
    }

    [Fact]
    public async Task Cada_chamada_guarda_onde_acontece_no_metodo_chamador()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var nodes = context.CallTree!.Flatten().ToList();

        // return Ok(await appService.AdicionarAsync(request)); no controller (a declaração fica no AppService).
        var adicionar = nodes.First(n => n.FullName == "CadastroVeiculoAppService.AdicionarAsync");
        Assert.Equal(("SampleApi/Cadastro/CadastroVeiculo.cs", 77, 77), (adicionar.CallFile, adicionar.CallLine, adicionar.CallEndLine));

        // Chamada na condição do if: só a expressão, não o if inteiro. Declarada em ServiceBase<TEntity> (Servicos.cs).
        var existe = nodes.First(n => n.FullName == "VeiculoTipoService.ExistePorId");
        Assert.Equal(("SampleApi/Cadastro/CadastroVeiculo.cs", 25, 25), (existe.CallFile, existe.CallLine, existe.CallEndLine));
        Assert.NotEqual(existe.CallFile, existe.SourceFile);

        // O grafo de negócio preserva o ponto de chamada.
        var noNegocio = context.BusinessGraph!.Flatten().First(n => n.FullName == "CadastroVeiculoAppService.AdicionarAsync");
        Assert.Equal(77, noNegocio.CallLine);
    }

    [Fact]
    public async Task Criterio2_interfaces_resolvidas_por_DI_nao_expandem_implementacoes_nao_registradas()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var all = context.CallTree!.Flatten().ToList();

        // IVeiculoTipoService → VeiculoTipoService (DI), nunca MotivoService (outra implementação de IServiceBase).
        Assert.DoesNotContain(all, n => n.TypeName.StartsWith("Motivo"));
        var existe = all.First(n => n.FullName == "VeiculoTipoService.ExistePorId");
        Assert.Equal("DI", existe.Resolution);
        Assert.Equal("_veiculoTipos".TrimStart('_'), existe.Receiver);

        // _repository (IRepository<TEntity>) inferido pelo construtor: VeiculoTipoService(IVeiculoTipoRepository) : ServiceBase(repository).
        var repositorio = Assert.Single(existe.Children);
        Assert.Contains("construtor", repositorio.Resolution);
        Assert.Equal(0, context.Stats.AmbiguousCalls);
    }

    [Fact]
    public async Task Criterio3_e_5_branches_impossiveis_eliminados_e_DomainNotification_nao_expande_handlers()
    {
        var context = await fixture.ContextAsync(Endpoint);

        // NotifyError publica DomainNotification: os cases ProgramacaoAlteradaEvent/VeiculoCriadoEvent do SalvarHistorico são impossíveis.
        var notify = context.CallTree!.Flatten().First(n => n.MethodName == "NotifyError");
        var historico = notify.Flatten().First(n => n.MethodName == "SalvarHistorico");
        Assert.Empty(historico.Children);
        Assert.True(context.Stats.PrunedBranches >= 2);

        // Já no evento de negócio (VeiculoCriadoEvent) o case correspondente continua alcançável.
        Assert.Contains(context.Effects, e => e.Kind == SinkKinds.Publish && e.EventKind == EventKinds.BusinessEvent && e.Target == "VeiculoCriadoEvent");
    }

    [Fact]
    public async Task Criterio4_infraestrutura_nao_domina_o_grafo_de_negocio()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var business = context.BusinessGraph!.Flatten().ToList();

        Assert.DoesNotContain(business, n => n.Summary is null && n.TypeName == "InMemoryBus");
        // Só nós-resumo representam a infraestrutura (o mecanismo interno fica no grafo técnico).
        Assert.All(business.Where(n => n.Category == NodeCategories.Infrastructure), n => Assert.NotNull(n.Summary));
        Assert.DoesNotContain(business, n => n.MethodName == "SalvarHistorico");

        // ObterErrosModel → NotifyError → InMemoryBus... vira "Retorna erro de validação".
        Assert.Contains(business, n => n.Summary == "Retorna erro de validação" && n.CollapsedCount > 1);
        Assert.Contains(business, n => n.Summary == "Publica evento VeiculoCriadoEvent");
    }

    [Fact]
    public async Task Criterio6_entidades_representam_efeitos_reais()
    {
        var context = await fixture.ContextAsync(Endpoint);

        // O histórico gravado pelo barramento é efeito de infraestrutura, não do endpoint.
        Assert.DoesNotContain(context.EntityChanges, c => c.Entity == "HistoricoVeiculo" && c.EffectClass == EffectClasses.Primary);
        Assert.Contains(context.Effects, e => e.Kind == SinkKinds.SaveChanges);
    }

    [Fact]
    public async Task Criterio7_toda_regra_tem_caminho_ate_o_endpoint()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var businessMethods = context.BusinessGraph!.Flatten().Select(n => n.FullName).ToHashSet();

        var rules = context.Conditions.Where(c => c.Kind != ConditionKinds.Validation).ToList();
        Assert.Contains(rules, r => r.Message == "Tipo de veículo não encontrado");
        Assert.Contains(rules, r => r.Message == "Já existe um veículo com essa placa");
        Assert.All(rules, r => Assert.Contains(r.Method, businessMethods));
    }

    [Fact]
    public async Task Criterio8_e_9_grafo_de_negocio_menor_e_cada_no_justificado()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var business = context.BusinessGraph!.Flatten().Skip(1).ToList();

        Assert.True(context.Stats.BusinessNodes < context.Stats.TechnicalNodes);
        Assert.All(business, n => Assert.NotNull(n.Category));
        Assert.All(business.Where(n => n.Summary is null), n => Assert.NotEmpty(n.Reasons!));

        var expected = new[]
        {
            "CadastroVeiculoAppService.AdicionarAsync",
            "VeiculoTipoService.ExistePorId",
            "VeiculoTipoService.ObterPorId",
            "CadastroVeiculoService.PlacaEhValida",
            "CadastroVeiculoService.Adicionar",
            "CadastroVeiculoService.SalvarAlteracoesAsync",
        };
        Assert.All(expected, name => Assert.Contains(business, n => n.FullName == name));

        var placa = business.First(n => n.FullName == "CadastroVeiculoService.PlacaEhValida");
        Assert.Equal(NodeCategories.Validation, placa.Category);
        var adicionar = business.First(n => n.FullName == "CadastroVeiculoService.Adicionar");
        Assert.Equal(NodeCategories.Persistence, adicionar.Category);
        // Repositório por baixo do service de persistência é detalhe técnico.
        Assert.Empty(adicionar.Children);
    }

    [Fact]
    public async Task Helpers_sao_ocultados_sem_perder_significado()
    {
        var context = await fixture.ContextAsync(Endpoint);

        Assert.DoesNotContain(context.BusinessGraph!.Flatten(), n => n.MethodName == "SanitizaPlaca");
        var app = context.BusinessGraph!.Flatten().First(n => n.FullName == "CadastroVeiculoAppService.AdicionarAsync");
        Assert.Contains("StringExtensions.SanitizaPlaca", app.Helpers!);
    }

    [Fact]
    public async Task Condicoes_ficam_no_registro_e_sao_referenciadas_por_id()
    {
        var context = await fixture.ContextAsync(Endpoint);

        var insert = context.EntityChanges.Single(c => c.Entity == "Veiculo");
        Assert.NotNull(insert.ConditionIds);
        Assert.All(insert.ConditionIds!, id => Assert.True(context.ConditionRegistry.ContainsKey(id)));
        Assert.Contains(context.ConditionRegistry.Values, v => v == "ModelState.IsValid");
        // Nada de condições gigantes concatenadas: cada entrada é uma condição atômica.
        Assert.All(context.ConditionRegistry.Values, v => Assert.DoesNotContain(" && ", v.Trim('(', ')')));
    }

    [Fact]
    public async Task Prompt_para_a_IA_nao_leva_o_grafo_tecnico()
    {
        var context = await fixture.ContextAsync(Endpoint);
        var prompt = AI.PromptBuilder.BuildUserPrompt(context);

        Assert.Contains("<business_graph>", prompt);
        Assert.Contains("<condition_registry>", prompt);
        Assert.DoesNotContain("SalvarHistorico", prompt);
        Assert.DoesNotContain("<method name=\"InMemoryBus", prompt);
    }
}
