using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAnalyzer.AI.ClaudeCode;

namespace EndpointAnalyzer.Tests;

public class ClaudeModelCatalogTests
{
    private static readonly ClaudeAuthStatus Account = new(true, true, "claude.ai", "catalog@example.test");

    [Fact]
    public void Descobre_modelos_novos_e_capacidades_sem_catalogo_fixo()
    {
        using var json = JsonDocument.Parse("""
            {"models":[
              {"value":"default","resolvedModel":"claude-opus-futuro","displayName":"Default"},
              {"value":"opus","resolvedModel":"claude-opus-futuro","displayName":"Opus","description":"Opus futuro · descrição","supportsEffort":true,"supportedEffortLevels":["low","medium","high"]},
              {"value":"haiku","resolvedModel":"claude-haiku-5-5","displayName":"Haiku","description":"Fastest for quick answers","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
              {"value":"novo","resolvedModel":"claude-familia-nova-9","displayName":"Família nova","supportsEffort":true,"supportedEffortLevels":["low","ultra"],"defaultEffort":"ultra"},
              {"value":"claude-legado","displayName":"Legado","supportsEffort":false}
            ]}
            """);
        var models = ClaudeCodeModelDiscovery.ParseModels(json.RootElement);

        Assert.Equal(4, models.Count);
        var haiku = Assert.Single(models, m => m.Id == "claude-haiku-5-5");
        Assert.Equal("Haiku 5.5", haiku.Name);
        Assert.Contains("xhigh", haiku.Efforts);
        Assert.Null(haiku.DefaultEffort); // Sem metadado explícito, deixa o Claude Code escolher seu padrão.
        var future = Assert.Single(models, m => m.Id == "claude-familia-nova-9");
        Assert.Equal("ultra", future.DefaultEffort);
        Assert.Empty(Assert.Single(models, m => m.Id == "claude-legado").Efforts);
        Assert.All(models, m => Assert.True(m.Available));
    }

    [Theory]
    [InlineData("claude-haiku-5-5", "Haiku 5.5")]
    [InlineData("claude-sonnet-5-5", "Sonnet 5.5")]
    [InlineData("claude-opus-4-6-20260205", "Opus 4.6")]
    [InlineData("claude-nova-familia-9-2", "Nova Familia 9.2")]
    [InlineData("claude-opus-5-5[1m]", "Opus 5.5[1m]")]
    public void Nome_e_versao_vem_do_id_e_nunca_da_description(string id, string expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            models = new[] { new { value = id, resolvedModel = id, displayName = "Modelo",
                description = "For complex work and everyday tasks" } },
        }));
        Assert.Equal(expected, ClaudeCodeModelDiscovery.ParseModels(json.RootElement).Single().Name);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"models\":null}")]
    public void Resposta_sem_modelos_falha_explicitamente_em_vez_de_inventar_lista(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        Assert.Throws<InvalidOperationException>(() => ClaudeCodeModelDiscovery.ParseModels(json.RootElement));
    }

    [Fact]
    public async Task Refresh_consulta_a_origem_e_cache_valido_e_reutilizado()
    {
        using var fixture = new CatalogFixture();
        await fixture.Catalog.GetAsync(Account);
        fixture.Models = [Model("claude-haiku-proximo")];
        Assert.Equal("claude-haiku-5-5", (await fixture.Catalog.GetAsync(Account)).Single().Id);
        Assert.Equal("claude-haiku-5-5", (await fixture.CreateCatalog().GetAsync(Account)).Single().Id);
        Assert.Equal(1, fixture.Calls);
        Assert.Equal("claude-haiku-proximo", (await fixture.Catalog.GetAsync(Account, refresh: true)).Single().Id);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task Cache_expira_mesmo_sem_reiniciar_o_catalogo()
    {
        using var fixture = new CatalogFixture();
        await fixture.Catalog.GetAsync(Account);
        fixture.Clock.UtcNow += TimeSpan.FromHours(24);
        fixture.Models = [Model("modelo-recem-lancado")];
        Assert.Equal("modelo-recem-lancado", (await fixture.Catalog.GetAsync(Account)).Single().Id);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task Cache_antigo_da_lista_fixa_e_descartado()
    {
        using var fixture = new CatalogFixture();
        await fixture.Catalog.GetAsync(Account);
        var file = Directory.GetFiles(fixture.Path, "models-*.json").Single();
        var cache = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        cache.Remove("version");
        cache["models"] = JsonSerializer.SerializeToNode(new[] { Model("claude-haiku-4-5") });
        await File.WriteAllTextAsync(file, cache.ToJsonString());
        Assert.Equal("claude-haiku-5-5", (await fixture.CreateCatalog().GetAsync(Account)).Single().Id);
        Assert.Equal(2, fixture.Calls);
    }

    [Fact]
    public async Task Requisicoes_concorrentes_compartilham_a_consulta()
    {
        using var fixture = new CatalogFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Completion = release.Task;
        var first = fixture.Catalog.GetAsync(Account);
        var second = fixture.Catalog.GetAsync(Account);
        Assert.Equal(1, fixture.Calls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Falha_no_refresh_e_exposta_e_pode_ser_tentada_novamente()
    {
        using var fixture = new CatalogFixture();
        await fixture.Catalog.GetAsync(Account);
        fixture.Failure = new InvalidOperationException("consulta indisponível");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.GetAsync(Account, refresh: true));
        fixture.Failure = null;
        fixture.Models = [Model("claude-novo")];
        Assert.Equal("claude-novo", (await fixture.Catalog.GetAsync(Account, refresh: true)).Single().Id);
        Assert.Equal(3, fixture.Calls);
    }

    [Fact]
    public async Task Sem_login_nao_consulta_nem_reutiliza_modelos_de_outra_conta()
    {
        using var fixture = new CatalogFixture();
        await fixture.Catalog.GetAsync(Account);
        Assert.Empty(await fixture.Catalog.GetAsync(Account with { LoggedIn = false }));
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public async Task Cancelamento_da_consulta_libera_a_trava_para_nova_tentativa()
    {
        using var fixture = new CatalogFixture();
        fixture.Completion = new TaskCompletionSource().Task;
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Catalog.GetAsync(Account, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        fixture.Completion = Task.CompletedTask;
        Assert.NotEmpty(await fixture.Catalog.GetAsync(Account));
        Assert.Equal(2, fixture.Calls);
    }

    private static ClaudeModelInfo Model(string id) => new(id, id, true, null, ["low", "medium"], null);

    private sealed class CatalogFixture : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("claude-catalog-").FullName;
        public ManualClock Clock { get; } = new();
        public IReadOnlyList<ClaudeModelInfo> Models { get; set; } = [Model("claude-haiku-5-5")];
        public Task Completion { get; set; } = Task.CompletedTask;
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public ClaudeModelCatalog Catalog { get; }
        public CatalogFixture() => Catalog = CreateCatalog();
        public ClaudeModelCatalog CreateCatalog() => new(new ClaudeCodeOptions(), Path, Clock, async token =>
        {
            Calls++;
            await Completion.WaitAsync(token);
            if (Failure is not null) throw Failure;
            return Models;
        });
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
