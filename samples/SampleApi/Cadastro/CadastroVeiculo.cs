using Microsoft.AspNetCore.Mvc;
using SampleApi.Entities;

namespace SampleApi.Cadastro;

public class AdicionarVeiculoRequest
{
    public int VeiculoTipoId { get; set; }

    public string? Placa { get; set; }
}

public interface ICadastroVeiculoAppService
{
    Task<int> AdicionarAsync(AdicionarVeiculoRequest request);
}

public class CadastroVeiculoAppService(
    IVeiculoTipoService veiculoTipos,
    ICadastroVeiculoService veiculos,
    IMediatorHandler bus) : ICadastroVeiculoAppService
{
    public async Task<int> AdicionarAsync(AdicionarVeiculoRequest request)
    {
        if (!veiculoTipos.ExistePorId(request.VeiculoTipoId))
            throw new DomainException(nameof(request.VeiculoTipoId), "Tipo de veículo não encontrado");

        var tipo = veiculoTipos.ObterPorId(request.VeiculoTipoId);
        if (tipo!.PlacaObrigatoria)
            veiculos.PlacaEhValida(request.Placa);

        var veiculo = new Veiculo
        {
            Placa = StringExtensions.SanitizaPlaca(request.Placa ?? ""),
            Disponivel = true,
        };

        veiculos.Adicionar(veiculo);
        await veiculos.SalvarAlteracoesAsync();
        await bus.PublicarEvento(new VeiculoCriadoEvent(veiculo.Id));

        return veiculo.Id;
    }
}

/// <summary>Controller base com notificação de erros pelo barramento.</summary>
public abstract class ApiController(IMediatorHandler bus) : ControllerBase
{
    protected IMediatorHandler Bus { get; } = bus;

    protected void NotifyError(string chave, string mensagem) =>
        Bus.PublicarEvento(new DomainNotification(chave, mensagem));

    protected IActionResult ObterErrosModel()
    {
        foreach (var erro in ModelState.Values.SelectMany(v => v.Errors))
            NotifyError(string.Empty, erro.ErrorMessage);

        return BadRequest(ModelState);
    }
}

[ApiController]
[Route("veiculo")]
[Tags("Veículo")]
public class CadastroVeiculoController(ICadastroVeiculoAppService appService, IMediatorHandler bus) : ApiController(bus)
{
    /// <summary>Cadastra um veículo validando o tipo e a placa.</summary>
    [HttpPost("v1")]
    public async Task<IActionResult> AdicionarVeiculoV1(AdicionarVeiculoRequest request)
    {
        if (!ModelState.IsValid)
            return ObterErrosModel();

        try
        {
            return Ok(await appService.AdicionarAsync(request));
        }
        catch (DomainException e)
        {
            NotifyError(e.Chave, e.Message);
            return ObterErrosModel();
        }
    }
}
