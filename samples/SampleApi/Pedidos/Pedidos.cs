using AutoMapper;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using SampleApi.Cadastro;
using SampleApi.Services;

namespace SampleApi.Pedidos;

// Padrão CQRS com MediatR: o controller mapeia a view model para um comando e envia pelo mediator;
// as regras e a persistência ficam no CommandHandler. Usado para testar o despacho Send → Handle.

public enum PedidoStatus
{
    Aberto,
    Cancelado,
}

public class Pedido : Entidade
{
    public int ClienteId { get; set; }

    public int Quantidade { get; set; }

    public PedidoStatus Status { get; set; }
}

public class AdicionarPedidoViewModel
{
    public int ClienteId { get; set; }

    public int Quantidade { get; set; }
}

public class AdicionarPedidoCommand : IRequest<bool>
{
    public int ClienteId { get; set; }

    public int Quantidade { get; set; }
}

public class CancelarPedidoCommand(int pedidoId) : IRequest<bool>
{
    public int PedidoId { get; } = pedidoId;
}

public class PedidoAdicionadoEvent(int pedidoId) : INotification
{
    public int PedidoId { get; } = pedidoId;
}

public class PedidoProfile : Profile
{
    public PedidoProfile() => CreateMap<AdicionarPedidoViewModel, AdicionarPedidoCommand>();
}

public interface IPedidoRepository : IRepository<Pedido>
{
    Pedido? ObterPorId(int id);

    void Atualizar(Pedido pedido);
}

public class PedidoRepository(Data.AppDbContext context) : Repository<Pedido>(context), IPedidoRepository
{
    public Pedido? ObterPorId(int id) => DbSet.Find(id);

    public void Atualizar(Pedido pedido) => DbSet.Update(pedido);
}

/// <summary>Um handler para dois comandos: cada Send vai para o Handle do comando enviado.</summary>
public class PedidoCommandHandler(IPedidoRepository pedidos, IMediator mediator) :
    IRequestHandler<AdicionarPedidoCommand, bool>,
    IRequestHandler<CancelarPedidoCommand, bool>
{
    public async Task<bool> Handle(AdicionarPedidoCommand request, CancellationToken cancellationToken)
    {
        if (request.Quantidade <= 0)
            throw new DomainException(nameof(request.Quantidade), "A quantidade do pedido deve ser maior que zero");

        var pedido = new Pedido
        {
            ClienteId = request.ClienteId,
            Quantidade = request.Quantidade,
            Status = PedidoStatus.Aberto,
        };

        pedidos.Adicionar(pedido);
        await pedidos.SalvarAlteracoesAsync();
        await mediator.Publish(new PedidoAdicionadoEvent(pedido.Id), cancellationToken);
        return true;
    }

    public async Task<bool> Handle(CancelarPedidoCommand request, CancellationToken cancellationToken)
    {
        var pedido = pedidos.ObterPorId(request.PedidoId)
            ?? throw new DomainException(nameof(request.PedidoId), "Pedido não encontrado");

        if (pedido.Status == PedidoStatus.Cancelado)
            throw new DomainException(nameof(request.PedidoId), "Pedido já cancelado");

        pedido.Status = PedidoStatus.Cancelado;
        pedidos.Atualizar(pedido);
        await pedidos.SalvarAlteracoesAsync();
        return true;
    }
}

public class PedidoAdicionadoEventHandler(INotificacaoService notificacao) : INotificationHandler<PedidoAdicionadoEvent>
{
    public Task Handle(PedidoAdicionadoEvent notification, CancellationToken cancellationToken) =>
        notificacao.Notificar($"Pedido {notification.PedidoId} recebido");
}

/// <summary>Barramento do projeto (com fonte) por cima do MediatR.</summary>
public interface IPedidoBus
{
    Task<bool> EnviarComando<T>(T comando) where T : IRequest<bool>;
}

public class PedidoBus(IMediator mediator) : IPedidoBus
{
    public Task<bool> EnviarComando<T>(T comando) where T : IRequest<bool> => mediator.Send(comando);
}

[ApiController]
[Route("api/pedidos")]
[Tags("Pedido")]
public class PedidoController(IMediator mediator, IMapper mapper, IPedidoBus bus) : ControllerBase
{
    private IMapper Mapper { get; } = mapper;

    /// <summary>Adiciona um pedido (MediatR direto).</summary>
    [HttpPost]
    public async Task<IActionResult> Adicionar(AdicionarPedidoViewModel pAdicionarPedidoViewModel)
    {
        var xCommand = Mapper.Map<AdicionarPedidoCommand>(pAdicionarPedidoViewModel);
        var ok = await mediator.Send(xCommand);
        return ok ? Ok() : BadRequest();
    }

    /// <summary>Cancela um pedido (pelo barramento do projeto).</summary>
    [HttpPut("{id}/cancelar")]
    public async Task<IActionResult> Cancelar(int id)
    {
        var ok = await bus.EnviarComando(new CancelarPedidoCommand(id));
        return ok ? NoContent() : BadRequest();
    }
}
