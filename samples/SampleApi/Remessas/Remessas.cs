using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using SampleApi.Cadastro;
using SampleApi.Exceptions;

namespace SampleApi.Remessas;

// Fluxo usado para testar a geração de cenários pela árvore de execução: a mesma validação alcançada por dois ramos,
// regras diferentes com a mesma mensagem, retorno antecipado sem escrita, laço com efeito e regra depois da persistência.

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

public class Remessa : Entidade
{
    public string Placa { get; set; } = "";

    public int Prioridade { get; set; }

    public bool Urgente { get; set; }
}

public class ItemRemessa : Entidade
{
    public int ProdutoId { get; set; }

    public int Quantidade { get; set; }
}

public interface IRemessaService
{
    Task<int> Criar(CriarRemessaRequest request);
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
}
