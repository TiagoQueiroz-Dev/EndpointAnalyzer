using SampleApi.Dtos;
using SampleApi.Entities;
using SampleApi.Exceptions;
using SampleApi.Repositories;

namespace SampleApi.Services;

public interface ICargaService
{
    Task<int> Criar(CriarCargaRequest request);
}

public class CargaService(ICargaRepository cargas, IVeiculoRepository veiculos) : ICargaService
{
    public async Task<int> Criar(CriarCargaRequest request)
    {
        if (request.Tipo == TipoCarga.Bloqueada)
            throw new RegraNegocioException("Tipo de carga bloqueado.");

        var veiculo = await veiculos.ObterPorId(request.VeiculoId)
            ?? throw new NaoEncontradoException("Veículo não encontrado.");

        // Restrições com mais de uma variável: resolvidas pelo Z3 na geração de cenários.
        if (request.PesoUnitarioKg * request.Quantidade > veiculo.CapacidadeKg)
            throw new RegraNegocioException("Carga excede a capacidade do veículo.");

        if (request.Tipo == TipoCarga.Perigosa && request.Quantidade > veiculo.CapacidadeKg / 2)
            throw new RegraNegocioException("Carga perigosa limitada à metade da capacidade.");

        var carga = new Carga
        {
            VeiculoId = veiculo.Id,
            Quantidade = request.Quantidade,
            PesoTotalKg = request.PesoUnitarioKg * request.Quantidade,
            Tipo = request.Tipo,
        };
        await cargas.Adicionar(carga);
        return carga.Id;
    }
}
