using EndpointAnalyzer.AI;
using EndpointAnalyzer.Application;
using EndpointAnalyzer.Context.BusinessFlow;
using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Tests;

[Collection(SampleSolutionCollection.Name)]
public class BusinessFlowTests(SampleSolutionFixture fixture)
{
    [Fact]
    public async Task Evidencias_vem_da_analise_estatica_com_origem_no_codigo()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");

        var (graph, evidence) = BusinessFlowEvidenceBuilder.Build(context);

        // N1 é a action; os nós seguem o grafo técnico, classificados antes da IA.
        Assert.Equal("ProgramacaoController.Criar", graph.Nodes[0].Method);
        Assert.Equal(TechnicalNodeKinds.Controller, graph.Nodes[0].Kind);
        Assert.Contains(graph.Nodes, n => n.Method == "ProgramacaoService.Criar" && n.Relevant);
        Assert.All(graph.Edges, e => Assert.Contains(graph.Nodes, n => n.Id == e.To));

        // Condições do registro, exceções com mensagem, persistências e efeitos externos, todos com arquivo:linha.
        Assert.Contains(evidence.Conditions, c => c.Expression.Contains("request.Data < DateTime.Today") && c.Source.StartsWith("ProgramacaoService.cs:"));
        Assert.Contains(evidence.Exceptions, e => e.Type == "RegraNegocioException" && e.Message == "Data inválida: a programação não pode estar no passado.");
        var insert = Assert.Single(evidence.Persistence, p => p.Type == "INSERT" && p.Entity == "Programacao");
        // INSERT aponta para o new da entidade.
        Assert.Contains("new Programacao", File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, insert.File))[insert.Line - 1]);
    }

    [Fact]
    public async Task Abstracoes_genericas_viram_o_uso_pela_entidade()
    {
        var context = await fixture.ContextAsync("POST /veiculo/v1");

        var (graph, evidence) = BusinessFlowEvidenceBuilder.Build(context);

        // ServiceBase<TEntity>.ExistePorId: um nó só, no ponto de uso, com a entidade e a instrução do chamador.
        var exists = Assert.Single(graph.Nodes, n => n.Via == "ServiceBase<TEntity>.ExistePorId");
        Assert.Equal(TechnicalNodeKinds.Abstraction, exists.Kind);
        Assert.Equal("VeiculoTipo", exists.Entity);
        Assert.DoesNotContain("Servicos.cs", exists.File);
        Assert.Contains("ExistePorId(", exists.Usage);
        Assert.Contains("Repository<TEntity>.ExistePorId", exists.Hides!);
        // O que a abstração faz por dentro não vira nó do fluxo.
        Assert.DoesNotContain(graph.Nodes, n => n.Method.StartsWith("Repository.") || n.Method.EndsWith(".Find"));
        Assert.Contains(evidence.Queries, q => q.NodeId == exists.Id && q.Entity == "VeiculoTipo" && q.Via == exists.Via);

        // Wrapper que só repassa (CadastroVeiculoService.Adicionar(v) => repository.Adicionar(v)) é a mesma abstração,
        // no ponto em que o app service chama o wrapper.
        var add = Assert.Single(graph.Nodes, n => n.Method == "CadastroVeiculoService.Adicionar");
        Assert.Equal((TechnicalNodeKinds.Abstraction, "Repository<TEntity>.Adicionar", "Veiculo"), (add.Kind, add.Via, add.Entity));
        Assert.Equal("CadastroVeiculoAppService.AdicionarAsync", add.Caller);
        Assert.DoesNotContain(graph.Nodes, n => n.Method == "CadastroVeiculoRepository.Adicionar");

        // Nada aponta para o corpo das abstrações genéricas (Repository<TEntity> e ServiceBase<TEntity> em Servicos.cs).
        var generic = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(fixture.SolutionPath)!, "SampleApi", "Cadastro", "Servicos.cs"));
        bool InsideGeneric(EvidenceBase e) => e.File.EndsWith("Servicos.cs") && e.Line > 0
            && (Array.FindIndex(generic, l => l.Contains("class Repository<TEntity>")) < e.Line - 1 && e.Line - 1 < Array.FindIndex(generic, l => l.Contains("interface IVeiculoTipoRepository"))
                || Array.FindIndex(generic, l => l.Contains("class ServiceBase<TEntity>")) < e.Line - 1 && e.Line - 1 < Array.FindIndex(generic, l => l.Contains("interface IVeiculoTipoService")));
        Assert.DoesNotContain(evidence.Persistence.Cast<EvidenceBase>().Concat(evidence.Queries).Concat(evidence.Exceptions), InsideGeneric);
        Assert.Contains(evidence.Persistence, p => p.Via is not null && p.Via.StartsWith("Repository<TEntity>."));

        // O prompt explica as abstrações para a IA.
        Assert.Contains("Nunca crie um passo para a abstração", BusinessFlowAiAnalyzer.SystemPrompt);
        Assert.Contains("\"kind\": \"Abstraction\"", BusinessFlowAiAnalyzer.BuildUserPrompt(context, graph, evidence));
    }

    [Fact]
    public async Task Validacao_remove_passos_sem_evidencia_e_religa_o_fluxo()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var (graph, evidence) = BusinessFlowEvidenceBuilder.Build(context);
        var condition = evidence.Conditions[0].Id;
        var insert = evidence.Persistence.First(p => p.Type == "INSERT").Id;

        var result = new BusinessFlowResult
        {
            Steps =
            [
                new() { Id = "B1", Type = "entry", Description = "Receber solicitação", EvidenceIds = ["N1"], Next = "B2" },
                new() { Id = "B2", Type = "validation", Description = "Data no passado?", EvidenceIds = [condition, "Z99"],
                    Branches = [new() { Condition = "Sim", Target = "B3" }, new() { Condition = "Não", Target = "B4" }] },
                new() { Id = "B3", Type = "error", Description = "Retornar erro", EvidenceIds = ["E1"] },
                // Sem evidência: sai do fluxo e quem apontava para ele segue para o próximo (B5).
                new() { Id = "B4", Type = "businessRule", Description = "Inventado pela IA", EvidenceIds = ["Z1"], Next = "B5" },
                new() { Id = "B5", Type = "persistence", Description = "Gravar programação", EvidenceIds = [insert], Next = "B9" },
                // Persistência sem evidência de persistência: fica, mas inconsistente.
                new() { Id = "B6", Type = "persistence", Description = "Gravar histórico", EvidenceIds = ["N1"] },
            ],
            CollapsedNodes = [new() { StepId = "B4", NodeIds = ["N2", "N999"], Reason = "x" }],
            UncertainSteps = [new() { StepId = "B4", Reason = "?" }],
        };

        var issues = BusinessFlowValidator.Validate(result, graph, evidence);

        Assert.DoesNotContain(result.Steps, s => s.Id == "B4");
        Assert.Equal(["B3", "B5"], result.Steps.Single(s => s.Id == "B2").Branches.Select(b => b.Target));
        Assert.Equal([condition], result.Steps.Single(s => s.Id == "B2").EvidenceIds);
        Assert.Equal("", result.Steps.Single(s => s.Id == "B5").Next);
        Assert.NotNull(result.Steps.Single(s => s.Id == "B6").Inconsistencies);
        Assert.Null(result.Steps.Single(s => s.Id == "B5").Inconsistencies);
        // O agrupamento do passo removido vira "removido do fluxo"; nós inexistentes saem; a incerteza do passo removido também.
        Assert.Equal("", result.CollapsedNodes[0].StepId);
        Assert.Equal(["N2"], result.CollapsedNodes[0].NodeIds);
        Assert.Empty(result.UncertainSteps);
        Assert.Contains(issues, i => i.StartsWith("B4 removido"));
        Assert.Contains(issues, i => i.Contains("Z99"));
        Assert.Contains(issues, i => i.Contains("B9"));
        // Rastreabilidade: a condição e o local dela no código.
        var trace = result.Steps.Single(s => s.Id == "B2").Trace!;
        Assert.Equal(evidence.Conditions[0].Expression, trace.Condition);
        Assert.Equal(evidence.Conditions[0].Line, trace.Line);

        var mermaid = BusinessFlowMermaidGenerator.Generate(result, graph, showTechnicalDetails: false, showCollapsedNodes: true);
        Assert.Contains("B2{\"Data no passado?\"}", mermaid);
        Assert.Contains("B2 -->|\"Sim\"| B3", mermaid);
        Assert.Contains("B2 -->|\"Não\"| B5", mermaid);
        Assert.Contains("B5[(\"Gravar programação\")]", mermaid);
        Assert.Contains("K1[\"<b>Removidos do fluxo:</b><br/>", mermaid);
        Assert.Contains("⚠ Gravar histórico", mermaid);
    }

    [Fact]
    public async Task Prompt_do_fluxo_leva_evidencias_sem_caminho_completo()
    {
        var context = await fixture.ContextAsync("POST /api/programacoes");
        var (graph, evidence) = BusinessFlowEvidenceBuilder.Build(context);

        var prompt = BusinessFlowAiAnalyzer.BuildUserPrompt(context, graph, evidence);

        Assert.Contains("<call_graph>", prompt);
        Assert.Contains("\"id\": \"N1\"", prompt);
        Assert.Contains("\"kind\": \"Controller\"", prompt);
        Assert.Contains("<persistence>", prompt);
        Assert.DoesNotContain("\"file\":", prompt);
        Assert.Equal(["steps", "collapsedNodes", "uncertainSteps"],
            BusinessFlowAiAnalyzer.Schema()["required"].EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public async Task Ia_por_aba_negocio_gera_o_fluxo_de_negocio_sem_documentacao_nem_cenarios()
    {
        var endpoint = await fixture.Service.FindEndpointAsync(fixture.SolutionPath, "POST /api/programacoes");
        var before = fixture.Ai.Calls;

        var report = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint, useAi: true,
            sections: AnalysisSections.Business, aiSections: AnalysisSections.Business);

        Assert.Equal(before, fixture.Ai.Calls);  // sem a chamada da documentação
        Assert.Null(report.Ai);
        Assert.Null(report.Context.Scenarios);
        Assert.Null(report.Runtime);
        var flow = Assert.IsType<BusinessFlowAnalysis>(report.BusinessFlow);
        Assert.Null(flow.Error);
        Assert.Equal(4, flow.Diagrams.Count);
        Assert.Contains(flow.Result!.Steps, s => s.Type == BusinessFlowStepTypes.Persistence && s.Trace?.File is not null);
        Assert.Contains("## Fluxo de negócio (IA)", ReportRenderer.Markdown(report));

        // Resumo com IA e Negócio sem IA: documentação sim, fluxo de negócio não.
        var summary = await fixture.Service.AnalyzeAsync(fixture.SolutionPath, endpoint, useAi: true,
            sections: AnalysisSections.Summary | AnalysisSections.Business, aiSections: AnalysisSections.Summary);
        Assert.NotNull(summary.Ai);
        Assert.Null(summary.BusinessFlow);
    }
}
