namespace SampleApi.Entities;

public class Veiculo
{
    public int Id { get; set; }

    public string Placa { get; set; } = "";

    public bool Disponivel { get; set; } = true;

    public int? UltimaProgramacaoId { get; set; }

    public decimal CapacidadeKg { get; set; }
}
