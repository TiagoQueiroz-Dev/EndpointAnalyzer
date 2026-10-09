using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace EndpointAnalyzer.Tests;

public class RouteResolutionTests
{
    private const string ApiSource = """
        using System;
        using System.Text.RegularExpressions;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.Routing;
        using Microsoft.AspNetCore.Mvc.ApplicationModels;
        public class Transformer : IOutboundParameterTransformer
        {
            public string TransformOutbound(object value) => value == null ? null :
                Regex.Replace(value.ToString() ?? string.Empty, "([a-z])([A-Z])", "$1-$2").ToLower();
        }
        public class CustomConvention : RouteTokenTransformerConvention
        {
            public CustomConvention(IOutboundParameterTransformer transformer) : base(transformer) { }
            protected override bool ShouldApply(ActionModel action) =>
                action.Controller.ControllerType.BaseType?.Name.Contains(nameof(ODataBaseController)) != true && base.ShouldApply(action);
        }
        public class Startup
        {
            public void Configure(MvcOptions options) { REGISTRATION }
        }
        [Route("api/[controller]")] public abstract class ApiController : ControllerBase { }
        public class AuthorizedApiController : ApiController { }
        public class ODataBaseController : ApiController { }
        public class ProgramacoesTransporteController : AuthorizedApiController
        {
            [HttpPost("{pId}/pedidos")] public void AdicionarPedido(Guid pId) { }
            [HttpGet("[action]")][ActionName("ListarPedidos")] public void ListarAsync() { }
            [HttpPost("~/LiteralSemTransformacao/{id}")] public void Literal(int id) { }
        }
        public class PessoasODataController : ODataBaseController
        {
            [HttpGet("{id}")] public void Obter(int id) { }
        }
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scanner_aplica_convencao_registrada_sem_alterar_rotas_literais_ou_OData(bool registered)
    {
        using var workspace = new AdhocWorkspace();
        var project = Project(workspace, ApiSource.Replace("REGISTRATION", registered
            ? "options.Conventions.Add(new CustomConvention(new Transformer()));" : ""));
        var loaded = new LoadedSolution { Solution = project.Solution, Path = "Api.csproj", RootDirectory = Directory.GetCurrentDirectory() };
        var endpoints = await new EndpointScanner(new AnalyzerOptions { HttpMethods = ["GET", "POST"] }).ScanAsync(loaded);

        var target = Assert.Single(endpoints, e => e.Action == "AdicionarPedido");
        Assert.Equal(registered ? "/api/programacoes-transporte/{pId}/pedidos" : "/api/ProgramacoesTransporte/{pId}/pedidos", target.Route);
        Assert.Equal(registered ? "/api/programacoes-transporte/listar-pedidos" : "/api/ProgramacoesTransporte/ListarPedidos",
            Assert.Single(endpoints, e => e.Action == "ListarAsync").Route);
        Assert.Equal("/LiteralSemTransformacao/{id}", Assert.Single(endpoints, e => e.Action == "Literal").Route);
        Assert.Equal("/api/PessoasOData/{id}", Assert.Single(endpoints, e => e.Controller == "PessoasODataController").Route);

        var catalog = await EndpointCatalog.BuildAsync(loaded, target);
        Assert.Contains(catalog.Endpoints, e => e.IsTarget && e.Route == target.Route);
        Assert.NotNull(catalog.Match("POST", RouteTemplate.Bind(target.Route, new Dictionary<string, string?> { ["pId"] = Guid.NewGuid().ToString() })));
        var compilation = (await project.GetCompilationAsync())!;
        var method = compilation.GetTypeByMetadataName("ProgramacoesTransporteController")!.GetMembers("AdicionarPedido").OfType<IMethodSymbol>().Single();
        var input = InputModel.Build(method, target.Route, "POST");
        var variables = new VarTable(input);
        var assignment = new Assignment();
        assignment.Values[variables.Input(input.Roots.Single())] = new Value { Text = "7003c824-b95c-4f47-35db-08de4c52d816" };
        Assert.Equal(target.Route.Replace("{pId}", "7003c824-b95c-4f47-35db-08de4c52d816"), new PayloadBuilder(input, variables, false).Build(assignment).Url);
    }

    [Fact]
    public void Tokens_escapados_permanecem_literais() =>
        Assert.Equal("/api/[controller]/Pedidos", RouteTemplate.Combine("api/[[controller]]", "[action]", "Ignorado", "Pedidos"));

    [Theory]
    [InlineData("/api/x/{id=7}", null, "/api/x/7")]
    [InlineData("/api/x/{id=7}", "42", "/api/x/42")]
    [InlineData("/api/x/{id?}", null, "/api/x")]
    [InlineData("/api/x/{id:int?}", "42", "/api/x/42")]
    [InlineData("/api/x/{*id}", "a/b c", "/api/x/a%2Fb%20c")]
    [InlineData("/api/x/{**id}", "a/b c", "/api/x/a/b%20c")]
    [InlineData("/api/x/{*id}", null, "/api/x")]
    [InlineData("/api/x/{id:int}", "42", "/api/x/42")]
    [InlineData("/api/x/{id}", "a$1?b", "/api/x/a%241%3Fb")]
    public void Materializa_default_opcional_catchall_e_constraints(string template, string? value, string expected) =>
        Assert.Equal(expected, RouteTemplate.Bind(template, new Dictionary<string, string?> { ["ID"] = value }));

    [Fact]
    public void Opcional_em_segmento_composto_remove_separador() =>
        Assert.Equal("/files/nome", RouteTemplate.Bind("/files/{name}.{ext?}", new Dictionary<string, string?> { ["name"] = "nome" }));

    [Fact]
    public void Falta_de_parametro_obrigatorio_e_reportada() =>
        Assert.Throws<InvalidOperationException>(() => RouteTemplate.Bind("/api/x/{id}", new Dictionary<string, string?>()));

    [Theory]
    [InlineData("/api/x/{id=7}")]
    [InlineData("/api/x/{id?}")]
    [InlineData("/api/x/{*rest}")]
    public void Catalogo_aceita_segmentos_omitidos_permitidos_pelo_template(string template)
    {
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = "GET", Route = template }]);
        Assert.NotNull(catalog.Match("GET", "/api/x"));
        Assert.NotNull(catalog.Match("GET", "/api/x/7"));
    }

    [Fact]
    public void Catalogo_reconhece_parametro_opcional_em_segmento_composto()
    {
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = "GET", Route = "/files/{name}.{ext?}" }]);
        Assert.NotNull(catalog.Match("GET", "/files/arquivo"));
        Assert.NotNull(catalog.Match("GET", "/files/arquivo.txt"));
    }

    [Fact]
    public async Task Transformacao_nao_suportada_falha_em_vez_de_gerar_rota_errada()
    {
        using var workspace = new AdhocWorkspace();
        var source = ApiSource.Replace("REGISTRATION", "options.Conventions.Add(new CustomConvention(new Transformer()));")
            .Replace("Regex.Replace(value.ToString() ?? string.Empty, \"([a-z])([A-Z])\", \"$1-$2\").ToLower()", "value.GetHashCode().ToString()");
        var project = Project(workspace, source);
        var loaded = new LoadedSolution { Solution = project.Solution, Path = "Api.csproj", RootDirectory = Directory.GetCurrentDirectory() };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EndpointScanner(new AnalyzerOptions()).ScanAsync(loaded));
        Assert.Contains("convenção de rota", error.Message);
    }
    private static Project Project(AdhocWorkspace workspace, string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        return workspace.AddProject("Api", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(references).AddDocument("Api.cs", source).Project;
    }
}
