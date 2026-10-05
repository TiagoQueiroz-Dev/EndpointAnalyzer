using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class ApiTokenTests(SampleSolutionFixture fixture)
{
    [Theory]
    [InlineData("eyJhbGciOi.abc.def", "Bearer eyJhbGciOi.abc.def")]
    [InlineData("  Bearer eyJ.abc  ", "Bearer eyJ.abc")]
    [InlineData("Basic dXNlcjpzZW5oYQ==", "Basic dXNlcjpzZW5oYQ==")]
    [InlineData("Authorization: Bearer xyz", "Bearer xyz")]
    public void Token_vira_header_authorization(string token, string expected) =>
        Assert.Equal(expected, ApiTokenChecker.Authorization(token));

    [Fact]
    public void Sem_token_nao_ha_header() => Assert.Null(ApiTokenChecker.Authorization("   "));

    [Fact]
    public async Task Executor_envia_o_token_sem_registrar_no_relatorio()
    {
        var handler = new CaptureHandler();
        var report = new RuntimeValidation();
        var catalog = EndpointCatalog.Of([new CatalogEndpoint { Method = "GET", Route = "/api/itens" }]);
        var executor = new ScenarioExecutor(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new RuntimeOptions(), catalog, true, report,
            ApiTokenChecker.Authorization("segredo123"));

        var execution = await executor.ExecuteAsync(new RuntimeRequest("GET", "/api/itens"), RuntimePhases.Acquisition);

        Assert.True(executor.Authenticated);
        Assert.Equal("Bearer segredo123", handler.Authorization);
        Assert.Equal(200, execution.Status);
        Assert.Null(execution.Headers);
        Assert.DoesNotContain("segredo123", System.Text.Json.JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task Verifica_o_token_subindo_o_servico()
    {
        // A SampleApi não exige autenticação: o token não é recusado, e a mensagem diz que nada exigia autenticação.
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/veiculos");
        var result = await fixture.Service.CheckApiTokenAsync(fixture.SolutionPath, endpoint, "qualquer-token");

        Assert.True(result.Valid, result.Message);
        Assert.Contains("nenhum GET testado exige autenticação", result.Message);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
