namespace SampleApi.Services;

public interface INotificacaoService
{
    Task Notificar(string mensagem);
}

public class EmailNotificacaoService(ILogger<EmailNotificacaoService> logger) : INotificacaoService
{
    // Valor fictício: serve para conferir que o SecretSanitizer não envia segredos para a IA.
    private const string SmtpPassword = "senha-super-secreta-123";

    public Task Notificar(string mensagem)
    {
        logger.LogInformation("Enviando e-mail: {Mensagem} ({Tamanho})", mensagem, SmtpPassword.Length);
        return Task.CompletedTask;
    }
}
