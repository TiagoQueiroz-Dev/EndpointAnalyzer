namespace SampleApi.Entities;

public enum TipoCarga
{
    Normal,
    Perigosa,
    Bloqueada,
}

public class Carga
{
    public int Id { get; set; }

    public int VeiculoId { get; set; }

    public int Quantidade { get; set; }

    public decimal PesoTotalKg { get; set; }

    public TipoCarga Tipo { get; set; }
}
