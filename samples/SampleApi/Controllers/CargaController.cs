using Microsoft.AspNetCore.Mvc;
using SampleApi.Dtos;
using SampleApi.Services;

namespace SampleApi.Controllers;

[ApiController]
[Route("api/cargas")]
[Tags("Carga")]
public class CargaController(ICargaService service) : ControllerBase
{
    /// <summary>Registra uma carga em um veículo respeitando a capacidade.</summary>
    [HttpPost]
    public async Task<IActionResult> Criar(CriarCargaRequest request) => Ok(await service.Criar(request));
}
