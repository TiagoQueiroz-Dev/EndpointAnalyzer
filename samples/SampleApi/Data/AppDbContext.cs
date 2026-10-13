using Microsoft.EntityFrameworkCore;
using SampleApi.Entities;

namespace SampleApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Programacao> Programacoes => Set<Programacao>();

    public DbSet<Veiculo> Veiculos => Set<Veiculo>();

    public DbSet<Carga> Cargas => Set<Carga>();

    public DbSet<Cadastro.VeiculoTipo> VeiculoTipos => Set<Cadastro.VeiculoTipo>();

    public DbSet<Cadastro.Motivo> Motivos => Set<Cadastro.Motivo>();

    public DbSet<Cadastro.HistoricoVeiculo> HistoricosVeiculo => Set<Cadastro.HistoricoVeiculo>();

    public DbSet<Pedidos.Pedido> Pedidos => Set<Pedidos.Pedido>();

    public DbSet<Origens.ProgramacaoTransporte> ProgramacoesTransporte => Set<Origens.ProgramacaoTransporte>();

    public DbSet<Origens.Local> Locais => Set<Origens.Local>();

    public DbSet<Origens.Endereco> Enderecos => Set<Origens.Endereco>();
}
