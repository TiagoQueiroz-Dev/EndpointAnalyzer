using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SampleApi.Cadastro;
using SampleApi.Exceptions;

namespace SampleApi.Remessas;

// Fluxo usado para testar a geração de cenários pela árvore de execução: a mesma validação alcançada por dois ramos,
// regras diferentes com a mesma mensagem, retorno antecipado sem escrita, laço com efeito e regra depois da persistência.
// A confirmação testa a propagação de retornos entre métodos: o bool com dois returns falsos decide o BadRequest do
// controller e o null devolvido pelo service decide o NotFound do ternário.

public class ItemRemessaRequest
{
    public int ProdutoId { get; set; }

    public int Quantidade { get; set; }
}

public class CriarRemessaRequest
{
    [Required]
    [StringLength(7, MinimumLength = 7)]
    public string? Placa { get; set; }

    [Range(1, 5)]
    public int Prioridade { get; set; }

    public bool Urgente { get; set; }

    public bool Rascunho { get; set; }

    public string? Referencia { get; set; }

    public List<ItemRemessaRequest> Itens { get; set; } = [];
}

public class ConfirmarRemessaRequest
{
    public int Volumes { get; set; }
}

public class Remessa : Entidade
{
    public string Placa { get; set; } = "";

    public int Prioridade { get; set; }

    public bool Urgente { get; set; }

    public bool Confirmada { get; set; }

    public int Volumes { get; set; }
}

public class ItemRemessa : Entidade
{
    public int ProdutoId { get; set; }

    public int Quantidade { get; set; }
}

public interface IRemessaService
{
    Task<int> Criar(CriarRemessaRequest request);

    bool VolumesValidos(ConfirmarRemessaRequest request);

    Task<Remessa?> Confirmar(int id, ConfirmarRemessaRequest request);
}

public class RemessaService(IRepository<Remessa> remessas, IRepository<ItemRemessa> itens) : IRemessaService
{
    public async Task<int> Criar(CriarRemessaRequest request)
    {
        // Regras diferentes com a mesma mensagem e o mesmo status.
        if (request.Referencia == "CANCELADA")
            throw new RegraNegocioException("Remessa inválida.");
        if (request.Referencia == "BLOQUEADA")
            throw new RegraNegocioException("Remessa inválida.");

        // A mesma validação alcançada pelos dois lados do if.
        if (request.Urgente)
            ValidarPlaca(request.Placa);
        else
            ValidarPlaca(request.Placa);

        // Retorno antecipado sem escrita: mesma resposta HTTP do sucesso, sem efeitos.
        if (request.Rascunho)
            return 0;

        var remessa = new Remessa { Placa = request.Placa!, Prioridade = request.Prioridade, Urgente = request.Urgente };
        remessas.Adicionar(remessa);

        foreach (var item in request.Itens)
            itens.Adicionar(new ItemRemessa { ProdutoId = item.ProdutoId, Quantidade = item.Quantidade });

        await remessas.SalvarAlteracoesAsync();

        // Regra depois da persistência: o fluxo continua depois do SaveChanges.
        if (request.Prioridade > 3 && !request.Urgente)
            throw new RegraNegocioException("Prioridade alta exige remessa urgente.");

        return remessa.Id;
    }

    private static void ValidarPlaca(string? placa)
    {
        if (placa == "AAA0A00")
            throw new RegraNegocioException("Placa reservada.");
    }

    // Dois returns falsos: o controller responde o mesmo BadRequest pelos dois (um desfecho só).
    public bool VolumesValidos(ConfirmarRemessaRequest request)
    {
        if (request.Volumes <= 0)
            return false;
        if (request.Volumes > 100)
            return false;
        return true;
    }

    // Devolve null quando a remessa não existe: o controller decide o NotFound pelo valor devolvido.
    public async Task<Remessa?> Confirmar(int id, ConfirmarRemessaRequest request)
    {
        var remessa = remessas.ObterTodos().FirstOrDefault(r => r.Id == id);
        if (remessa == null)
            return null;

        remessa.Confirmada = true;
        remessa.Volumes = request.Volumes;
        await remessas.SalvarAlteracoesAsync();
        return remessa;
    }
}

[ApiController]
[Route("api/remessas")]
[Tags("Remessa")]
public class RemessaController(IRemessaService service) : ControllerBase
{
    /// <summary>Cria uma remessa (ou só valida, quando é rascunho) e retorna o id.</summary>
    [HttpPost]
    public async Task<IActionResult> Criar(CriarRemessaRequest request)
    {
        var id = await service.Criar(request);
        return Ok(id);
    }

    /// <summary>Confirma a remessa com a quantidade de volumes e retorna o id.</summary>
    [HttpPut("{id}/confirmar")]
    public async Task<IActionResult> Confirmar(int id, ConfirmarRemessaRequest request)
    {
        if (!service.VolumesValidos(request))
            return BadRequest("Quantidade de volumes inválida.");

        var remessa = await service.Confirmar(id, request);
        return remessa is null ? NotFound() : Ok(remessa.Id);
    }
}
