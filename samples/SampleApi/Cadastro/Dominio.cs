using SampleApi.Entities;

namespace SampleApi.Cadastro;

// Padrões de um projeto real: services e repositórios genéricos, barramento em memória,
// notificações de domínio e controller base. Usados para testar a remoção de ruído da análise.

public abstract class Entidade
{
    public int Id { get; set; }

    public bool Excluido { get; set; }
}

public class VeiculoTipo : Entidade
{
    public string Nome { get; set; } = "";

    public bool PlacaObrigatoria { get; set; }
}

public class Motivo : Entidade
{
    public string Descricao { get; set; } = "";
}

public class HistoricoVeiculo : Entidade
{
    public int VeiculoId { get; set; }

    public string Descricao { get; set; } = "";
}

public class DomainException(string chave, string mensagem) : Exception(mensagem)
{
    public string Chave { get; } = chave;
}

public static class StringExtensions
{
    public static string SanitizaPlaca(string placa) => placa.Replace("-", "").Replace(" ", "").ToUpperInvariant();
}

// ---- Eventos ----

public abstract class Event
{
    public string Acao { get; protected set; } = "";
}

public class DomainNotification : Event
{
    public DomainNotification(string chave, string valor)
    {
        Chave = chave;
        Valor = valor;
        Acao = nameof(DomainNotification);
    }

    public string Chave { get; }

    public string Valor { get; }
}

public class VeiculoCriadoEvent(int veiculoId) : Event
{
    public int VeiculoId { get; } = veiculoId;
}

public class ProgramacaoAlteradaEvent(int programacaoId) : Event
{
    public int ProgramacaoId { get; } = programacaoId;
}

public interface IMediatorHandler
{
    Task PublicarEvento<T>(T evento) where T : Event;
}

/// <summary>Barramento que grava histórico conforme o tipo do evento.</summary>
public sealed class InMemoryBus(IRepository<HistoricoVeiculo> historicos) : IMediatorHandler
{
    public Task PublicarEvento<T>(T evento) where T : Event
    {
        if (evento.Acao == nameof(DomainNotification))
            return Task.CompletedTask;

        SalvarHistorico(evento);
        return Task.CompletedTask;
    }

    private void SalvarHistorico<T>(T evento) where T : Event
    {
        switch (evento)
        {
            case ProgramacaoAlteradaEvent programacao:
                historicos.Adicionar(new HistoricoVeiculo { VeiculoId = programacao.ProgramacaoId, Descricao = "programação alterada" });
                break;
            case VeiculoCriadoEvent criado:
                historicos.Adicionar(new HistoricoVeiculo { VeiculoId = criado.VeiculoId, Descricao = "veículo criado" });
                break;
        }
    }
}
