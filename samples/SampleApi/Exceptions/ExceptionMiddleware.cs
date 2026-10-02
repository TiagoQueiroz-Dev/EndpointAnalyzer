namespace SampleApi.Exceptions;

/// <summary>Tratamento global: converte as exceções de negócio em status HTTP.</summary>
public class ExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = ex switch
            {
                NaoEncontradoException => StatusCodes.Status404NotFound,
                RegraNegocioException => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status500InternalServerError,
            };
            await context.Response.WriteAsJsonAsync(new { erro = ex.Message });
        }
    }
}
