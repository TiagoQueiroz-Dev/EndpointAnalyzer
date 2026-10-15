namespace SampleDomain;

/// <summary>
/// Entidade em outro projeto: o construtor delega atribuições a um método privado e Renovar atribui as próprias
/// propriedades sem this. (padrões de entidades ricas em projetos de domínio separados).
/// </summary>
public class Contrato
{
    protected Contrato()
    {
    }

    public Contrato(int clienteId, DateTime inicio, int meses)
    {
        ClienteId = clienteId;
        DefinirVigencia(inicio, meses);
    }

    public int Id { get; set; }

    public int ClienteId { get; protected set; }

    public DateTime Inicio { get; protected set; }

    public DateTime Fim { get; protected set; }

    public int Renovacoes { get; protected set; }

    public void Renovar(int meses)
    {
        Fim = Fim.AddMonths(meses);
        Renovacoes++;
    }

    private void DefinirVigencia(DateTime inicio, int meses)
    {
        Inicio = inicio;
        Fim = inicio.AddMonths(meses);
    }
}
