using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Entities;

namespace SampleApi.Cadastro;

// ---- Repositórios genéricos ----

public interface IRepository<TEntity> where TEntity : class
{
    IQueryable<TEntity> ObterTodos();

    bool ExistePorId(int id);

    void Adicionar(TEntity entidade);

    Task<int> SalvarAlteracoesAsync();
}

public class Repository<TEntity>(AppDbContext context) : IRepository<TEntity> where TEntity : class
{
    protected readonly AppDbContext Db = context;
    protected readonly DbSet<TEntity> DbSet = context.Set<TEntity>();

    public virtual IQueryable<TEntity> ObterTodos() => DbSet;

    public virtual bool ExistePorId(int id) => DbSet.Find(id) is not null;

    public virtual void Adicionar(TEntity entidade) => DbSet.Add(entidade);

    public Task<int> SalvarAlteracoesAsync() => Db.SaveChangesAsync();
}

public interface IVeiculoTipoRepository : IRepository<VeiculoTipo>;

public class VeiculoTipoRepository(AppDbContext context) : Repository<VeiculoTipo>(context), IVeiculoTipoRepository;

public interface IMotivoRepository : IRepository<Motivo>;

public class MotivoRepository(AppDbContext context) : Repository<Motivo>(context), IMotivoRepository
{
    public override IQueryable<Motivo> ObterTodos() => DbSet.Where(m => !m.Excluido);
}

public interface ICadastroVeiculoRepository : IRepository<Veiculo>
{
    bool PossuiPlacaExistente(string placa);
}

public class CadastroVeiculoRepository(AppDbContext context) : Repository<Veiculo>(context), ICadastroVeiculoRepository
{
    public bool PossuiPlacaExistente(string placa)
    {
        var sanitizada = StringExtensions.SanitizaPlaca(placa);
        return DbSet.Any(v => v.Placa == sanitizada);
    }
}

// ---- Services genéricos ----

public interface IServiceBase<TEntity> where TEntity : Entidade
{
    bool ExistePorId(int? id);

    TEntity? ObterPorId(int id);

    IQueryable<TEntity> ObterTodos();
}

public abstract class ServiceBase<TEntity> : IServiceBase<TEntity> where TEntity : Entidade
{
    private readonly IRepository<TEntity> _repository;

    protected ServiceBase(IRepository<TEntity> repository)
    {
        _repository = repository;
    }

    public virtual bool ExistePorId(int? id)
    {
        if (id == null)
            return false;

        return _repository.ExistePorId(id.Value);
    }

    public TEntity? ObterPorId(int id) => ObterTodos().FirstOrDefault(e => e.Id == id);

    public virtual IQueryable<TEntity> ObterTodos() => _repository.ObterTodos().Where(e => !e.Excluido);
}

public interface IVeiculoTipoService : IServiceBase<VeiculoTipo>;

public class VeiculoTipoService(IVeiculoTipoRepository repository) : ServiceBase<VeiculoTipo>(repository), IVeiculoTipoService;

public interface IMotivoService : IServiceBase<Motivo>;

/// <summary>Outra implementação de IServiceBase: não pode aparecer no fluxo do cadastro de veículo.</summary>
public class MotivoService(IMotivoRepository repository) : ServiceBase<Motivo>(repository), IMotivoService
{
    public override bool ExistePorId(int? id) => id > 0 && base.ExistePorId(id);
}

public interface ICadastroVeiculoService
{
    void PlacaEhValida(string? placa);

    void Adicionar(Veiculo veiculo);

    Task<int> SalvarAlteracoesAsync();
}

public class CadastroVeiculoService(ICadastroVeiculoRepository repository) : ICadastroVeiculoService
{
    public void PlacaEhValida(string? placa)
    {
        if (string.IsNullOrWhiteSpace(placa))
            throw new DomainException(nameof(placa), "Placa obrigatória para esse tipo de veículo");

        if (repository.PossuiPlacaExistente(placa))
            throw new DomainException(nameof(placa), "Já existe um veículo com essa placa");
    }

    public void Adicionar(Veiculo veiculo) => repository.Adicionar(veiculo);

    public Task<int> SalvarAlteracoesAsync() => repository.SalvarAlteracoesAsync();
}
