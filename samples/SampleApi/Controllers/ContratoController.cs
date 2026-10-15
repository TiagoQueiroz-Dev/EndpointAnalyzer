using Microsoft.AspNetCore.Mvc;
using SampleApi.Data;
using SampleDomain;

namespace SampleApi.Controllers;

/// <summary>Usa uma entidade de outro projeto (SampleDomain): os métodos dela são resolvidos a partir desta compilação.</summary>
[ApiController]
[Route("api/contratos")]
[Tags("Contrato")]
public class ContratoController(AppDbContext context) : ControllerBase
{
    /// <summary>Retorna o id do contrato criado.</summary>
    [HttpPost]
    public async Task<IActionResult> Criar(int clienteId, int meses)
    {
        var contrato = new Contrato(clienteId, DateTime.UtcNow.Date, meses);
        context.Contratos.Add(contrato);
        await context.SaveChangesAsync();
        return Ok(contrato.Id);
    }

    /// <summary>Retorna se o contrato foi renovado.</summary>
    [HttpPut("{id}/renovar")]
    public async Task<IActionResult> Renovar(int id, int meses)
    {
        var contrato = await context.Contratos.FindAsync(id);
        if (contrato is null)
            return NotFound();

        contrato.Renovar(meses);
        await context.SaveChangesAsync();
        return Ok(true);
    }
}
