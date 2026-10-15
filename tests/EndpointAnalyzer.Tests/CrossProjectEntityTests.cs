using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Entidade em outro projeto (samples/SampleDomain): o método da entidade é resolvido pela compilação da API e a
/// propriedade pela do domínio. Os dois símbolos do mesmo tipo são distintos e as atribuições continuam sendo da entidade.
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class CrossProjectEntityTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Metodo_da_entidade_que_atribui_sem_this_e_update()
    {
        var context = await fixture.ContextAsync("PUT /api/contratos/{id}/renovar");

        var update = Assert.Single(context.EntityChanges, c => c.Entity == "Contrato");
        Assert.Equal(EntityOperations.Update, update.Operation);
        Assert.Equal(["Fim", "Renovacoes"], update.Properties.Order());
    }

    [Fact]
    public async Task Metodo_chamado_pelo_construtor_em_outro_projeto_faz_parte_do_insert()
    {
        var context = await fixture.ContextAsync("POST /api/contratos");

        var insert = Assert.Single(context.EntityChanges, c => c.Entity == "Contrato");
        Assert.Equal(EntityOperations.Insert, insert.Operation);
        Assert.Equal(["ClienteId", "Fim", "Inicio"], insert.Properties.Order());
    }
}
