using SampleApi.Exceptions;

namespace SampleApi.Entities;

public enum ProgramacaoStatus
{
    Pendente,
    EmAndamento,
    Finalizada,
    Cancelada,
}

public class Programacao
{
    public int Id { get; set; }

    public DateTime Data { get; set; }

    public DateTime DataInicio { get; set; }

    public DateTime? DataFim { get; set; }

    public ProgramacaoStatus Status { get; set; }

    public int? VeiculoId { get; set; }

    public int? UsuarioFinalizacaoId { get; set; }

    public string? Observacao { get; set; }

    public string? MotivoCancelamento { get; set; }

    public void Finalizar(int usuarioId)
    {
        if (Status != ProgramacaoStatus.EmAndamento)
            throw new RegraNegocioException("Somente programações em andamento podem ser finalizadas.");

        Status = ProgramacaoStatus.Finalizada;
        DataFim = DateTime.Now;
        UsuarioFinalizacaoId = usuarioId;
    }
}
