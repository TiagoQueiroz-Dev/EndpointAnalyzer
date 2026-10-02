using System.ComponentModel.DataAnnotations;
using SampleApi.Entities;

namespace SampleApi.Dtos;

public class CriarCargaRequest
{
    public int VeiculoId { get; set; }

    [Range(1, 1000)]
    public int Quantidade { get; set; }

    public decimal PesoUnitarioKg { get; set; }

    public TipoCarga Tipo { get; set; }
}
