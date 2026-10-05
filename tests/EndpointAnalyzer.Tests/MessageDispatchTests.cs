using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;

namespace EndpointAnalyzer.Tests;

/// <summary>
/// Despacho de comandos/eventos para os handlers (samples/SampleApi/Pedidos): mediator.Send(command) e
/// bus.EnviarComando(command) seguem até o Handle do CommandHandler, com as regras, alterações e persistência dele.
/// </summary>
[Collection(SampleSolutionCollection.Name)]
public class MessageDispatchTests(SampleSolutionFixture fixture)
{
    private const string Adicionar = "POST /api/pedidos";
    private const string Cancelar = "PUT /api/pedidos/{id}/cancelar";

    [Fact]
    public async Task Mediator_Send_entra_no_Handle_do_comando()
    {
        var context = await fixture.ContextAsync(Adicionar);

        // IMediator.Send não tem fonte: o handler entra como filho do controller, resolvido pelo tipo do comando.
        var handle = Assert.Single(context.CallTree!.Children, n => n.FullName == "PedidoCommandHandler.Handle");
        Assert.Equal(ResolutionStrategies.MessageHandler, handle.Resolution);
        Assert.Equal("AdicionarPedidoCommand", handle.ResolvedFrom);
        Assert.Equal("mediator", handle.Receiver);

        // O Handle certo (AdicionarPedidoCommand), não o do CancelarPedidoCommand da mesma classe.
        Assert.Contains(handle.Children, n => n.FullName == "PedidoRepository.Adicionar");
        Assert.DoesNotContain(handle.Children, n => n.FullName == "PedidoRepository.Atualizar");
    }

    [Fact]
    public async Task Regras_alteracoes_e_persistencia_do_handler_entram_na_analise()
    {
        var context = await fixture.ContextAsync(Adicionar);

        Assert.Contains(context.Conditions, c => c.Message == "A quantidade do pedido deve ser maior que zero" && c.Method == "PedidoCommandHandler.Handle");

        var insert = Assert.Single(context.EntityChanges, c => c.Entity == "Pedido");
        Assert.Equal(EntityOperations.Insert, insert.Operation);
        Assert.Contains(insert.PropertyChanges, p => p.Property == "Status" && p.Value == "PedidoStatus.Aberto");
        Assert.Contains(context.Effects, e => e.Kind == SinkKinds.SaveChanges);

        Assert.Contains(context.BusinessGraph!.Flatten(), n => n.FullName == "PedidoCommandHandler.Handle");
    }

    [Fact]
    public async Task Evento_publicado_no_handler_segue_para_o_handler_do_evento()
    {
        var context = await fixture.ContextAsync(Adicionar);
        var handle = context.CallTree!.Flatten().First(n => n.FullName == "PedidoCommandHandler.Handle");

        var evento = Assert.Single(handle.Children, n => n.FullName == "PedidoAdicionadoEventHandler.Handle");
        Assert.Equal("PedidoAdicionadoEvent", evento.ResolvedFrom);
        Assert.Equal("request.Quantidade > 0", evento.Condition);
    }

    [Fact]
    public async Task Barramento_do_projeto_nao_esconde_o_handler_dentro_da_infraestrutura()
    {
        var context = await fixture.ContextAsync(Cancelar);
        var root = context.CallTree!;

        // PedidoBus.EnviarComando (infraestrutura) fica no grafo técnico; o handler é irmão dele, não filho.
        var bus = Assert.Single(root.Children, n => n.FullName == "PedidoBus.EnviarComando");
        Assert.DoesNotContain(bus.Flatten(), n => n.MethodName == "Handle");
        var handle = Assert.Single(root.Flatten(), n => n.FullName == "PedidoCommandHandler.Handle");
        Assert.Contains(handle, root.Children);

        Assert.Contains(context.Conditions, c => c.Message == "Pedido não encontrado");
        Assert.Contains(context.Conditions, c => c.Message == "Pedido já cancelado");
        var update = Assert.Single(context.EntityChanges, c => c.Entity == "Pedido");
        Assert.Equal(EntityOperations.Update, update.Operation);

        var business = context.BusinessGraph!.Flatten().ToList();
        Assert.Contains(business, n => n.FullName == "PedidoCommandHandler.Handle");
        Assert.Contains(business, n => n.Summary == "Envia comando CancelarPedidoCommand");
    }

    [Fact]
    public async Task Handlers_por_interface_propria_e_por_classe_base_generica()
    {
        const string code = """
            using System.Threading.Tasks;
            public interface ICommandHandler<in T> { Task Handle(T command); }
            public abstract class CommandHandler<T> { public abstract Task Executar(T command); }
            public class CriarCommand { }
            public class ExcluirCommand { }
            public class GenericoCommand { }
            public class CriarHandler : ICommandHandler<CriarCommand> { public Task Handle(CriarCommand command) => Task.CompletedTask; }
            public class ExcluirHandler : CommandHandler<ExcluirCommand> { public override Task Executar(ExcluirCommand command) => Task.CompletedTask; }
            public class LogHandler<T> : ICommandHandler<T> { public Task Handle(T command) => Task.CompletedTask; }
            """;

        using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
        var project = workspace.AddProject("Handlers", Microsoft.CodeAnalysis.LanguageNames.CSharp)
            .WithCompilationOptions(new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(references)
            .AddDocument("Handlers.cs", code).Project;

        var map = await MessageHandlerMap.BuildAsync(project.Solution, project);
        var compilation = (await project.GetCompilationAsync())!;

        var criar = Assert.Single(map.HandlersOf(compilation.GetTypeByMetadataName("CriarCommand")!));
        Assert.Equal(("CriarHandler", "Handle"), (criar.Type.Name, criar.Method.Name));
        var excluir = Assert.Single(map.HandlersOf(compilation.GetTypeByMetadataName("ExcluirCommand")!));
        Assert.Equal(("ExcluirHandler", "Executar"), (excluir.Type.Name, excluir.Method.Name));

        // Handler genérico aberto (LogHandler<T>) não é handler de um comando específico.
        Assert.Empty(map.HandlersOf(compilation.GetTypeByMetadataName("GenericoCommand")!));
    }

    [Fact]
    public async Task Cenarios_seguem_o_comando_mapeado_ate_o_payload()
    {
        var context = await fixture.ContextAsync(Adicionar);
        var matrix = context.Scenarios!;

        // Mapper.Map<AdicionarPedidoCommand>(viewModel): request.Quantidade do handler é o campo quantidade do body.
        var happy = matrix.Scenarios[0];
        Assert.Equal(ScenarioKinds.Success, happy.Kind);
        Assert.True((long)happy.Request.Body!["quantidade"]! > 0);

        var regra = Assert.Single(matrix.Scenarios, s => s.Kind == ScenarioKinds.Rule && s.Expected.Messages.Contains("A quantidade do pedido deve ser maior que zero"));
        Assert.True((long)regra.Request.Body!["quantidade"]! <= 0);
    }
}
