using System.ComponentModel.DataAnnotations;

namespace SampleApi.Dtos;

public class CriarProgramacaoRequest
{
    [Required(ErrorMessage = "A data é obrigatória.")]
    public DateTime Data { get; set; }

    public int? VeiculoId { get; set; }

    [MaxLength(500)]
    public string? Observacao { get; set; }
}

public class AtualizarProgramacaoRequest
{
    public DateTime? Data { get; set; }

    public int? VeiculoId { get; set; }

    [MaxLength(500)]
    public string? Observacao { get; set; }
}

public class CriarVeiculoRequest
{
    [Required]
    [StringLength(7, MinimumLength = 7, ErrorMessage = "A placa deve ter 7 caracteres.")]
    public string Placa { get; set; } = "";
}

public class CancelarProgramacaoRequest
{
    [Required]
    [StringLength(200, MinimumLength = 5)]
    public string Motivo { get; set; } = "";
}
