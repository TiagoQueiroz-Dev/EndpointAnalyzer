using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SampleApi.Data;
using SampleApi.Dtos;
using SampleApi.Entities;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/veiculos")]
[Tags("Veículo")]
public class VeiculoController(AppDbContext context) : ControllerBase
{
    /// <summary>Retorna o id do veículo criado.</summary>
    [HttpPost]
    public async Task<IActionResult> Criar(CriarVeiculoRequest request)
    {
        if (await context.Veiculos.AnyAsync(v => v.Placa == request.Placa))
            return Conflict("Já existe um veículo com esta placa.");

        var veiculo = new Veiculo { Placa = request.Placa.ToUpperInvariant(), Disponivel = true };
        context.Veiculos.Add(veiculo);
        await context.SaveChangesAsync();

        return Ok(veiculo.Id);
    }

    /// <summary>Retorna se a alteração da disponibilidade do veículo foi realizada com sucesso.</summary>
    [HttpPut("{id}/disponibilidade")]
    public async Task<IActionResult> AlterarDisponibilidade(int id, [FromQuery] bool disponivel)
    {
        var veiculo = await context.Veiculos.FindAsync(id);
        if (veiculo is null)
            return NotFound();

        veiculo.Disponivel = disponivel;
        if (disponivel)
            veiculo.UltimaProgramacaoId = null;

        await context.SaveChangesAsync();
        return Ok(true);
    }
}
