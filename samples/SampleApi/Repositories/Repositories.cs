using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Entities;

namespace SampleApi.Repositories;

public interface IProgramacaoRepository
{
    Task<Programacao?> ObterPorId(int id);

    Task Adicionar(Programacao programacao);

    Task Salvar();
}

public class ProgramacaoRepository(AppDbContext context) : IProgramacaoRepository
{
    public Task<Programacao?> ObterPorId(int id) =>
        context.Programacoes.FirstOrDefaultAsync(x => x.Id == id);

    public async Task Adicionar(Programacao programacao)
    {
        context.Programacoes.Add(programacao);
        await context.SaveChangesAsync();
    }

    public Task Salvar() => context.SaveChangesAsync();
}

public interface IVeiculoRepository
{
    Task<Veiculo?> ObterPorId(int id);
}

public class VeiculoRepository(AppDbContext context) : IVeiculoRepository
{
    public Task<Veiculo?> ObterPorId(int id) =>
        context.Veiculos.FirstOrDefaultAsync(x => x.Id == id);
}

public interface ICargaRepository
{
    Task Adicionar(Carga carga);
}

public class CargaRepository(AppDbContext context) : ICargaRepository
{
    public async Task Adicionar(Carga carga)
    {
        context.Cargas.Add(carga);
        await context.SaveChangesAsync();
    }
}
