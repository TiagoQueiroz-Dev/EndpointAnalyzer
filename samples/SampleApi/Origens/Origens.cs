using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AutoMapper;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using SampleApi.Cadastro;
using SampleApi.Data;

namespace SampleApi.Origens;

// Padrão com notificações de domínio (sem exceção): o handler publica DomainNotification (Falha, Existe,
// RetornaFalhaEntity) e devolve false; o controller responde Ok sem notificação e BadRequest com notificação
// (ApiResponse). Usado para testar a geração dos cenários a partir das regras que notificam.

public class Endereco : Entidade
{
    public string Apelido { get; set; } = "";
}

public class Local : Entidade
{
    protected Local()
    {
    }

    public Local(int sequencia, Endereco endereco)
    {
        Sequencia = sequencia;
        Endereco = endereco;
        EnderecoId = endereco.Id;
    }

    public int Sequencia { get; set; }

    public int EnderecoId { get; set; }

    public Endereco? Endereco { get; set; }

    public bool EhValido() => true;
}

public class ProgramacaoTransporte : Entidade
{
    private readonly List<Local> _origens = [];

    public IReadOnlyList<Local> Origens => _origens;

    public bool Faturada { get; set; }

    [NotMapped]
    public FluentValidation.Results.ValidationResult ValidationResult { get; set; } = new();

    public void AdicionarOrigem(Local local)
    {
        if (!ValidarProgramacaoFaturada())
            return;
        _origens.Add(local);
    }

    public bool ValidarProgramacaoFaturada()
    {
        if (!Faturada)
            return true;
        AdicionarErroValidacao(nameof(Faturada), "Não é possível alterar uma programação já faturada");
        return false;
    }

    public void AdicionarErroValidacao(string erro, string descricao) =>
        ValidationResult.Errors.Add(new ValidationFailure(erro, descricao));

    public bool EhValido() => ValidationResult.IsValid;
}

public class AdicionarOrigemViewModel
{
    [Required]
    public int Sequencia { get; set; }

    [Required]
    public int EnderecoId { get; set; }
}

public class AdicionarOrigemCommand : IRequest<bool>
{
    public int ProgramacaoTransporteId { get; set; }

    public int Sequencia { get; set; }

    public int EnderecoId { get; set; }
}

public class OrigemProfile : Profile
{
    public OrigemProfile() =>
        CreateMap<(int Id, AdicionarOrigemViewModel Origem), AdicionarOrigemCommand>()
            .ConvertUsing(s => new AdicionarOrigemCommand { ProgramacaoTransporteId = s.Id, Sequencia = s.Origem.Sequencia, EnderecoId = s.Origem.EnderecoId });
}

public class DomainNotificationHandler
{
    public List<DomainNotification> Notificacoes { get; } = [];

    public bool PossuiNotificacoes() => Notificacoes.Count > 0;
}

public interface IProgramacaoTransporteRepository
{
    ProgramacaoTransporte? ObterPorId(int id);

    void AdicionarLocal(Local local);
}

public class ProgramacaoTransporteRepository(AppDbContext db) : IProgramacaoTransporteRepository
{
    public ProgramacaoTransporte? ObterPorId(int id) => db.Set<ProgramacaoTransporte>().Find(id);

    public void AdicionarLocal(Local local) => db.Set<Local>().Add(local);
}

/// <summary>Base dos handlers: falhas por notificação e commit protegido.</summary>
public abstract class NotificacaoCommandHandler(IMediatorHandler bus, AppDbContext db, DomainNotificationHandler notifications)
{
    protected IMediatorHandler Bus { get; } = bus;

    protected bool Falha() => false;

    protected async Task<bool> Falha(string chave, string valor)
    {
        await Bus.PublicarEvento(new DomainNotification(chave, valor));
        return Falha();
    }

    protected async Task<bool> RetornaFalhaEntity(ProgramacaoTransporte entidade)
    {
        foreach (var erro in entidade.ValidationResult.Errors)
            await Bus.PublicarEvento(new DomainNotification(nameof(ProgramacaoTransporte), erro.ErrorMessage));
        return Falha();
    }

    protected bool Existe<TKey, TEntity>(TKey? id, string propriedade, out TEntity? entidade)
        where TKey : struct
        where TEntity : Entidade
    {
        entidade = db.Set<TEntity>().Find(id);
        if (id == null || id.Equals(default(TKey))) return true;
        if (entidade == null)
            Bus.PublicarEvento(new DomainNotification(nameof(TEntity), $"{propriedade} não existe"));
        return entidade != null;
    }

    protected bool Commit()
    {
        if (notifications.PossuiNotificacoes())
            return false;
        try
        {
            db.SaveChanges();
            return true;
        }
        catch (Exception e)
        {
            Bus.PublicarEvento(new DomainNotification("Commit", e.Message));
        }
        return false;
    }
}

public class OrigemCommandHandler(IMediatorHandler bus, AppDbContext db, DomainNotificationHandler notifications, IProgramacaoTransporteRepository programacoes)
    : NotificacaoCommandHandler(bus, db, notifications), IRequestHandler<AdicionarOrigemCommand, bool>
{
    public async Task<bool> Handle(AdicionarOrigemCommand request, CancellationToken cancellationToken)
    {
        var programacao = programacoes.ObterPorId(request.ProgramacaoTransporteId);
        if (programacao == null)
            return await Falha(nameof(ProgramacaoTransporte), $"{nameof(ProgramacaoTransporte)} não existe");

        if (!Existe<int, Endereco>(request.EnderecoId, nameof(request.EnderecoId), out var endereco))
            return Falha();

        var origem = new Local(request.Sequencia, endereco!);

        var existeOrigem = programacao.Origens.Any(o => o.EnderecoId == request.EnderecoId);
        if (existeOrigem == false)
        {
            programacao.AdicionarOrigem(origem);
            programacoes.AdicionarLocal(origem);
        }

        if (!programacao.EhValido())
            return await RetornaFalhaEntity(programacao);

        return Commit();
    }
}

public interface IOrigemAppService
{
    Task<bool> AdicionarOrigem(int id, AdicionarOrigemViewModel origem);
}

public class OrigemAppService(IMapper mapper, IMediator mediator) : IOrigemAppService
{
    public async Task<bool> AdicionarOrigem(int id, AdicionarOrigemViewModel origem)
    {
        var command = mapper.Map<AdicionarOrigemCommand>((id, origem));
        return await mediator.Send(command);
    }
}

/// <summary>Controller base: Ok sem notificação, BadRequest com as notificações.</summary>
public abstract class NotificacaoApiController(DomainNotificationHandler notifications) : ControllerBase
{
    protected ActionResult ApiResponse<T>(T resultado)
    {
        if (IsValidOperation())
            return Ok(resultado);

        return BadRequest(notifications.Notificacoes);
    }

    protected bool IsValidOperation() => !notifications.PossuiNotificacoes();
}

[ApiController]
[Route("api/programacoes-transporte")]
[Tags("Origem")]
public class ProgramacaoTransporteController(IOrigemAppService appService, DomainNotificationHandler notifications) : NotificacaoApiController(notifications)
{
    /// <summary>Adiciona uma origem à programação de transporte.</summary>
    [HttpPost("{id}/origens")]
    public async Task<ActionResult> AdicionarOrigem(int id, AdicionarOrigemViewModel origem)
    {
        if (ModelState.IsValid)
            return ApiResponse(await appService.AdicionarOrigem(id, origem));

        return ApiResponse(await appService.AdicionarOrigem(id, origem));
    }
}
