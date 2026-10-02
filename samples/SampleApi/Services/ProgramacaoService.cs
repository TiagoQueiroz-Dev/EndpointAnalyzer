using SampleApi.Dtos;
using SampleApi.Entities;
using SampleApi.Exceptions;
using SampleApi.Repositories;

namespace SampleApi.Services;

public interface IProgramacaoService
{
    Task<int> Criar(CriarProgramacaoRequest request);

    Task Atualizar(int id, AtualizarProgramacaoRequest request);

    Task Finalizar(int id, int usuarioId);

    Task Cancelar(int id, CancelarProgramacaoRequest request);
}

public class ProgramacaoService(
    IProgramacaoRepository repository,
    IVeiculoRepository veiculos,
    INotificacaoService notificacoes) : IProgramacaoService
{
    public async Task<int> Criar(CriarProgramacaoRequest request)
    {
        if (request.Data < DateTime.Today)
            throw new RegraNegocioException("Data inválida: a programação não pode estar no passado.");

        var programacao = new Programacao
        {
            Data = request.Data,
            DataInicio = request.Data,
            Status = ProgramacaoStatus.Pendente,
            Observacao = request.Observacao,
        };

        if (request.VeiculoId != null)
        {
            var veiculo = await veiculos.ObterPorId(request.VeiculoId.Value)
                ?? throw new NaoEncontradoException("Veículo não encontrado.");

            if (!veiculo.Disponivel)
                throw new RegraNegocioException("Veículo indisponível.");

            programacao.VeiculoId = veiculo.Id;
            veiculo.Disponivel = false;
        }

        await repository.Adicionar(programacao);
        await notificacoes.Notificar($"Programação {programacao.Id} criada.");

        return programacao.Id;
    }

    public async Task Atualizar(int id, AtualizarProgramacaoRequest request)
    {
        var programacao = await ObterOuFalhar(id);

        if (programacao.Status == ProgramacaoStatus.Cancelada)
            throw new RegraNegocioException("Programação cancelada não pode ser alterada.");

        if (request.Data.HasValue)
            programacao.Data = request.Data.Value;

        if (request.VeiculoId != null)
            programacao.VeiculoId = request.VeiculoId;

        programacao.Observacao = request.Observacao;

        await repository.Salvar();
    }

    public async Task Finalizar(int id, int usuarioId)
    {
        var programacao = await ObterOuFalhar(id);
        programacao.Finalizar(usuarioId);
        await repository.Salvar();
    }

    public async Task Cancelar(int id, CancelarProgramacaoRequest request)
    {
        var programacao = await ObterOuFalhar(id);

        switch (programacao.Status)
        {
            case ProgramacaoStatus.Finalizada:
                throw new RegraNegocioException("Programação finalizada não pode ser cancelada.");
            case ProgramacaoStatus.Cancelada:
                return;
            default:
                programacao.Status = ProgramacaoStatus.Cancelada;
                programacao.MotivoCancelamento = request.Motivo;
                break;
        }

        await repository.Salvar();
    }

    private async Task<Programacao> ObterOuFalhar(int id) =>
        await repository.ObterPorId(id) ?? throw new NaoEncontradoException($"Programação {id} não encontrada.");
}
