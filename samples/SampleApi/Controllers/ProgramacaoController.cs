using Microsoft.AspNetCore.Mvc;
using SampleApi.Dtos;
using SampleApi.Services;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/programacoes")]
[Tags("Programação de transporte")]
public class ProgramacaoController(IProgramacaoService service) : ControllerBase
{
    /// <summary>Retorna uma programação de transporte.</summary>
    [HttpGet("{id}")]
    public IActionResult Obter(int id) => Ok(id);

    /// <summary>
    /// Retorna o id da programação de transporte criada.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Criar(CriarProgramacaoRequest request)
    {
        var id = await service.Criar(request);
        return CreatedAtAction(nameof(Obter), new { id }, null);
    }

    /// <summary>Atualiza a data, o veículo e a observação de uma programação.</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Atualizar(int id, AtualizarProgramacaoRequest request)
    {
        await service.Atualizar(id, request);
        return NoContent();
    }

    /// <summary>Finaliza uma programação em andamento.</summary>
    [HttpPatch("{id}/finalizar")]
    public async Task<IActionResult> Finalizar(int id, [FromQuery] int usuarioId)
    {
        if (usuarioId <= 0)
            return BadRequest("Usuário inválido.");

        await service.Finalizar(id, usuarioId);
        return NoContent();
    }

    /// <summary>Cancela uma programação informando o motivo.</summary>
    [HttpPatch("{id}/cancelar")]
    public async Task<IActionResult> Cancelar(int id, CancelarProgramacaoRequest request)
    {
        await service.Cancelar(id, request);
        return NoContent();
    }

    /// <summary>Exclui uma programação.</summary>
    [HttpDelete("{id}")]
    public IActionResult Excluir(int id) => NoContent();
}
