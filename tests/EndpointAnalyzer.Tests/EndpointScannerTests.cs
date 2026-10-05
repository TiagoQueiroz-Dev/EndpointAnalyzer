using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class EndpointScannerTests(SampleSolutionFixture fixture)
{
    [Fact]
    public void Lista_apenas_POST_PUT_e_PATCH_agrupados_na_ordem_do_codigo()
    {
        var ids = fixture.Endpoints.Select(e => $"{e.Group} | {e.Id}").ToList();

        Assert.Equal(
        [
            "Carga | POST /api/cargas",
            "Pedido | POST /api/pedidos",
            "Pedido | PUT /api/pedidos/{id}/cancelar",
            "Programação de transporte | POST /api/programacoes",
            "Programação de transporte | PUT /api/programacoes/{id}",
            "Programação de transporte | PATCH /api/programacoes/{id}/finalizar",
            "Programação de transporte | PATCH /api/programacoes/{id}/cancelar",
            "Veículo | POST /veiculo/v1",
            "Veículo | POST /api/veiculos",
            "Veículo | PUT /api/veiculos/{id}/disponibilidade",
        ], ids);
    }

    [Fact]
    public void Preenche_controller_action_resumo_e_origem()
    {
        var criar = fixture.Endpoints.Single(e => e.Id == "POST /api/programacoes");

        Assert.Equal("ProgramacaoController", criar.Controller);
        Assert.Equal("Criar", criar.Action);
        Assert.Equal("Retorna o id da programação de transporte criada.", criar.Summary);
        Assert.Equal("SampleApi/Controllers/ProgramacaoController.cs", criar.SourceFile);
        Assert.Equal(20, criar.SourceLine);
        Assert.StartsWith("M:SampleApi.Controllers.ProgramacaoController.Criar", criar.MethodId);
    }

    [Theory]
    [InlineData("""[Tags("Veículo")]""", "Veículo")]
    [InlineData("""[Microsoft.AspNetCore.Http.Tags("Pessoa", "Outra")]""", "Pessoa")]
    [InlineData("""[SwaggerOperation(Summary = "x", Tags = new[] { "Pedido" })]""", "Pedido")]
    [InlineData("""[HttpPost]""", null)]
    public void Le_a_tag_do_OpenAPI(string attribute, string? expected)
    {
        var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration($"{attribute} public void M() {{ }}")!;
        Assert.Equal(expected, EndpointMetadata.Tag(method.AllAttributes(), null));
    }

    [Fact]
    public void Le_o_resumo_do_comentario_xml_mesmo_sem_GenerateDocumentationFile()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                /// <summary>
                /// Retorna o <see cref="T:Ns.Veiculo"/> criado.
                /// </summary>
                [HttpPost]
                public void Criar() { }
            }
            """, new CSharpParseOptions(documentationMode: DocumentationMode.None));
        var compilation = CSharpCompilation.Create("t", [tree]);
        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var summary = EndpointMetadata.Summary(method, model.GetDeclaredSymbol(method)!, method.AllAttributes().ToList(), model);

        Assert.Equal("Retorna o Veiculo criado.", summary);
    }

    [Fact]
    public async Task Analisa_endpoint_que_usa_DbContext_direto_no_controller()
    {
        var context = await fixture.ContextAsync("PUT /api/veiculos/{id}/disponibilidade");

        var update = Assert.Single(context.EntityChanges);
        Assert.Equal("Veiculo", update.Entity);
        Assert.Equal("veiculo is not null", update.Condition);
        Assert.Contains(update.PropertyChanges, p => p is { Property: "UltimaProgramacaoId", Condition: "veiculo is not null && disponivel" });
    }

    [Theory]
    [InlineData("api/[controller]", null, "/api/Programacao")]
    [InlineData("api/[controller]", "{id}", "/api/Programacao/{id}")]
    [InlineData("api/programacoes/", "/outra/rota", "/outra/rota")]
    [InlineData("api/programacoes", "~/raiz", "/raiz")]
    [InlineData(null, "api/[action]", "/api/Criar")]
    [InlineData(null, null, "/")]
    public void Combina_rotas_como_o_ASP_NET_Core(string? controller, string? action, string expected) =>
        Assert.Equal(expected, RouteTemplate.Combine(controller, action, "Programacao", "Criar"));
}
