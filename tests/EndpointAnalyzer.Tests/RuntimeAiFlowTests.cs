using System.Text.Json;
using System.Text.RegularExpressions;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Fluxo completo com IA em POST /api/programacoes: a IA (roteirizada) cria veículos pela API, liga os dados reais aos
/// cenários e, na exploração, prepara novos dados quando o estado mudou. Os validators do FluentValidation da SampleApi
/// não rodam automaticamente: esses cenários não podem ser confirmados (seriam falsos positivos da análise estática).
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class RuntimeAiFlowTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Ia_busca_dados_reais_e_a_matriz_validada_nao_tem_falso_positivo()
    {
        var ai = new ScriptedRuntimeAi();
        var provider = new ServiceCollection()
            .AddEndpointAnalyzer()
            .AddSingleton(new AnalysisCache(Path.Combine(fixture.CacheDirectory, "flow")))
            .AddSingleton<IAiProviderSelector>(new SingleAiProviderSelector(ai))
            .BuildServiceProvider();
        var service = provider.GetRequiredService<EndpointAnalysisService>();
        var endpoint = await service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");

        var report = await service.AnalyzeAsync(fixture.SolutionPath, endpoint, useAi: true);

        var runtime = report.Runtime!;
        var log = string.Join("\n", runtime.Log);
        Assert.True(runtime.Matrix is not null, runtime.Error + "\n" + log);
        var statics = report.Context.Scenarios!.Scenarios;
        var byId = runtime.Matrix!.Scenarios.Concat(runtime.Matrix.Removed).ToDictionary(s => s.Id);
        string Status(Func<Scenario, bool> find) => byId[statics.First(find).Id].Status;
        string Why(Func<Scenario, bool> find) => string.Join(" ", byId[statics.First(find).Id].Reasons) + "\n" + log;

        // Requisitos → aquisição (escrita para criar os veículos) → contexto verificado nas respostas.
        Assert.Equal(3, runtime.Requirements.Count);
        Assert.Contains(runtime.Executions, e => e is { Phase: RuntimePhases.Acquisition, Method: "POST", Url: "/api/veiculos", Status: 200 });
        Assert.Contains(runtime.Context, c => c is { Key: "veiculoDisponivel", Verified: true });
        Assert.Contains(runtime.Context, c => c is { Key: "veiculoInexistente", Verified: false });

        Assert.True(Status(s => s.Kind == ScenarioKinds.Success && s.Focus is null) == ScenarioValidationStatuses.Confirmed, Why(s => s.Kind == ScenarioKinds.Success && s.Focus is null));
        Assert.True(Status(s => s.Expected.Messages.Contains("Veículo indisponível.")) == ScenarioValidationStatuses.Confirmed, Why(s => s.Expected.Messages.Contains("Veículo indisponível.")));
        Assert.True(Status(s => s.Expected.Messages.Contains("Veículo não encontrado.")) == ScenarioValidationStatuses.Confirmed, Why(s => s.Expected.Messages.Contains("Veículo não encontrado.")));
        Assert.Equal(ScenarioValidationStatuses.Confirmed, Status(s => s.Expected.Messages.Any(m => m.StartsWith("Data inválida"))));

        // Limites de sucesso: o baseline deixou o veículo 1 indisponível; a exploração criou outro veículo.
        foreach (var boundary in statics.Where(s => s.Kind == ScenarioKinds.Boundary))
            Assert.True(byId[boundary.Id].Status == ScenarioValidationStatuses.Confirmed, $"{boundary.Id}: {Why(s => s.Id == boundary.Id)}");
        Assert.Contains(runtime.Executions, e => e is { Phase: RuntimePhases.Exploration, Method: "POST", Url: "/api/veiculos" });

        // FluentValidation sem validação automática: a análise estática previa 400, mas o fluxo segue para o service.
        // Nunca é confirmado; sai da matriz só com a evidência (2 execuções estáveis + resultado previsto por outra regra).
        var fluent = statics.Where(s => s.Expected.Messages.Contains("Informe a data da programação.") || s.Expected.Messages.Contains("Veículo inválido.")).ToList();
        Assert.Equal(2, fluent.Count);
        foreach (var scenario in fluent)
        {
            var removed = Assert.Single(runtime.Matrix.Removed, r => r.Id == scenario.Id);
            Assert.Equal(ScenarioValidationStatuses.Unreachable, removed.Status);
            Assert.Equal(2, removed.Attempts);
            Assert.Contains(removed.Evidence, e => e.Contains("resultado previsto para"));
            Assert.DoesNotContain(runtime.Matrix.Scenarios, s => s.Id == scenario.Id);
        }
        Assert.Contains("Retirados da matriz (inalcançáveis)", ReportRenderer.Markdown(report));

        // Toda confirmação tem execução real com o status esperado.
        foreach (var confirmed in runtime.Matrix.Scenarios.Where(s => s.Status == ScenarioValidationStatuses.Confirmed))
            Assert.Equal(confirmed.Expected.HttpStatus, runtime.Executions.Single(e => e.Id == confirmed.ExecutionId).Status);
    }
}

/// <summary>IA roteirizada: responde às etapas da validação em runtime lendo os prompts, como uma IA faria.</summary>
internal sealed partial class ScriptedRuntimeAi : IAiProvider
{
    public string Model => "scripted";

    public List<string> Purposes { get; } = [];

    public Task<EndpointAnalysisResult> AnalyzeAsync(EndpointAnalysisContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EndpointAnalysisResult { Summary = "ok" });

    public Task<JsonElement> CompleteJsonAsync(AiJsonRequest request, CancellationToken cancellationToken = default)
    {
        lock (Purposes) Purposes.Add(request.Purpose);
        object response = request.Purpose switch
        {
            var p when p.StartsWith("planejar") => Requirements(),
            var p when p.StartsWith("adquirir") => Acquire(request.UserPrompt),
            var p when p.StartsWith("ligar") => Bind(request.UserPrompt),
            _ => Explore(request.UserPrompt),
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(response));
    }

    private static object Requirements() => new
    {
        requirements = new[]
        {
            new { id = "R1", description = "Veículo existente e disponível", entity = "Veiculo", constraints = new[] { "Disponivel == true" }, scenarios = Array.Empty<string>(), fields = new[] { "request.VeiculoId" } },
            new { id = "R2", description = "Veículo existente e indisponível", entity = "Veiculo", constraints = new[] { "Disponivel == false" }, scenarios = Array.Empty<string>(), fields = new[] { "request.VeiculoId" } },
            new { id = "R3", description = "Id de veículo que não existe", entity = "Veiculo", constraints = new[] { "ObterPorId == null" }, scenarios = Array.Empty<string>(), fields = new[] { "request.VeiculoId" } },
        },
        notes = Array.Empty<string>(),
    };

    private sealed record Created(string Execution, string Placa, int Id);

    /// <summary>Veículos criados nas execuções do prompt: "EX-01 POST /api/veiculos body={"placa":"RTA1B23"} → 200: 1".</summary>
    private static List<Created> Vehicles(string prompt) => CreatedRegex().Matches(prompt)
        .Select(m => new Created(m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value))).DistinctBy(c => c.Execution).ToList();

    private static object Acquire(string prompt)
    {
        var vehicles = Vehicles(prompt);
        var put = PutRegex().Match(prompt);
        object[] none = [];
        if (vehicles.Count < 2)
            return new
            {
                requests = new[] { Post("RTA1B23", "R1"), Post("RTB1C23", "R2") },
                context = none, unsatisfiable = none, done = false,
            };
        var available = vehicles.First(v => v.Placa == "RTA1B23");
        var unavailable = vehicles.First(v => v.Placa == "RTB1C23");
        var item = Item("veiculoDisponivel", $"{{\"id\":{available.Id},\"placa\":\"RTA1B23\",\"disponivel\":true}}", available.Execution, "R1");
        if (!put.Success)
            return new
            {
                requests = new[] { new { method = "PUT", url = $"/api/veiculos/{unavailable.Id}/disponibilidade?disponivel=false", bodyJson = "", reason = "deixar indisponível", requirementIds = new[] { "R2" } } },
                context = new[] { item }, unsatisfiable = none, done = false,
            };
        return new
        {
            requests = none,
            context = new[]
            {
                item,
                Item("veiculoIndisponivel", $"{{\"id\":{unavailable.Id},\"disponivel\":false}}", put.Groups[1].Value, "R2"),
                // Derivado dos dados reais (maior id + 100000): não está em nenhuma resposta.
                Item("veiculoInexistente", $"{{\"id\":{vehicles.Max(v => v.Id) + 100000}}}", "", "R3"),
            },
            unsatisfiable = none,
            done = true,
        };
    }

    private sealed record Block(string Id, string Kind, string Title, string Text);

    private static List<Block> Blocks(string prompt) => BlockRegex().Matches(prompt)
        .Select(m => new Block(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim(), m.Value)).ToList();

    private static object Bind(string prompt)
    {
        var context = ContextIds(prompt);
        var scenarios = Blocks(prompt).Select(b =>
        {
            var member = DisponivelRegex().Match(b.Text).Groups[1].Value;
            var root = RootRegex().Match(b.Text).Groups[1].Value;
            object[] bindings =
                b.Title.StartsWith("Caminho feliz") ? [Bind("request.VeiculoId", context["veiculoDisponivel"], "veiculoDisponivel"), Bind(member, "true", "veiculoDisponivel")]
                : b.Title.Contains("Veículo indisponível") ? [Bind("request.VeiculoId", context["veiculoIndisponivel"], "veiculoIndisponivel"), Bind(member, "false", "veiculoIndisponivel")]
                : b.Title.Contains("Veículo não encontrado") ? [Bind("request.VeiculoId", context["veiculoInexistente"], "veiculoInexistente"), Bind(root, "null", "veiculoInexistente")]
                : [];
            return new { id = b.Id, materializable = true, reason = "", bindings };
        });
        return new { scenarios };
    }

    private static object Explore(string prompt)
    {
        var pending = prompt[prompt.IndexOf("<pending>", StringComparison.Ordinal)..];
        var vehicles = Vehicles(prompt);
        var contextItems = new List<object>();
        var requests = new List<object>();
        var scenarios = new List<object>();
        foreach (var b in Blocks(pending))
        {
            object[] none = [];
            if (b.Kind == "validacao" && !b.Text.Contains("observado: 400"))
            {
                scenarios.Add(new { id = b.Id, action = "give_up", bindings = none, classification = "inalcancavel",
                    justification = "O validator do FluentValidation não é executado: falta AddFluentValidationAutoValidation em Program.cs." });
                continue;
            }
            if (b.Text.Contains("indispon", StringComparison.Ordinal))
            {
                var placa = $"RTZ{b.Id[^2..]}AB";
                var created = vehicles.FirstOrDefault(v => v.Placa == placa);
                if (created is null)
                {
                    requests.Add(Post(placa, "R1"));
                    scenarios.Add(new { id = b.Id, action = "acquire", bindings = none, classification = "", justification = "O veículo do baseline ficou indisponível." });
                    continue;
                }
                var key = $"veiculoLivre{b.Id}";
                contextItems.Add(Item(key, $"{{\"id\":{created.Id},\"placa\":\"{placa}\"}}", created.Execution, "R1"));
                var member = DisponivelRegex().Match(b.Text).Groups[1].Value;
                scenarios.Add(new { id = b.Id, action = "retry", bindings = new object[] { Bind("request.VeiculoId", created.Id.ToString(), key), Bind(member, "true", key) },
                    classification = "", justification = "Veículo novo, disponível." });
                continue;
            }
            scenarios.Add(new { id = b.Id, action = "give_up", bindings = none, classification = "inconclusivo", justification = "sem alternativa" });
        }
        return new { scenarios, requests, context = contextItems };
    }

    private static object Post(string placa, string requirement) =>
        new { method = "POST", url = "/api/veiculos", bodyJson = $"{{\"placa\":\"{placa}\"}}", reason = "criar veículo", requirementIds = new[] { requirement } };

    private static object Item(string key, string value, string execution, string requirement) =>
        new { key, description = key, valueJson = value, executionId = execution, requirementIds = new[] { requirement } };

    private static object Bind(string variable, string value, string source) => new { variable, valueJson = value, source };

    /// <summary>key → id do item do contexto ("- veiculoDisponivel: ... = {"id":1,...}").</summary>
    private static Dictionary<string, string> ContextIds(string prompt) => ContextRegex().Matches(prompt)
        .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.Last().Groups[2].Value);

    [GeneratedRegex(@"(EX-\d+) POST /api/veiculos body=\{""placa"":""(\w+)""\} → 200: (\d+)")]
    private static partial Regex CreatedRegex();

    [GeneratedRegex(@"(EX-\d+) PUT /api/veiculos/\d+/disponibilidade\?disponivel=false → 200")]
    private static partial Regex PutRegex();

    [GeneratedRegex(@"## (CEN-\d+) \[(\w+)\] ([^\n]*)[\s\S]*?(?=## CEN-|\z)")]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"- (\S+\.Disponivel) ")]
    private static partial Regex DisponivelRegex();

    [GeneratedRegex(@"- (\S+\)) \*? ?\(estado, \w+ \(objeto")]
    private static partial Regex RootRegex();

    [GeneratedRegex(@"- (\w+)(?: \[NÃO VERIFICADO\])?: [^=]*= \{""id"":(\d+)")]
    private static partial Regex ContextRegex();
}
