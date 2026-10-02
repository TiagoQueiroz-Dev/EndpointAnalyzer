using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Anthropic.Exceptions;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.Application;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointAnalyzer(
    // Analyzer:MaxCallDepth, Analyzer:IgnoredNamespaces e Analyzer:Relevance (listas são acrescentadas aos padrões).
    analyzer => builder.Configuration.GetSection("Analyzer").Bind(analyzer),
    // A chave da API vem de ANTHROPIC_API_KEY (ou user-secrets "Claude:ApiKey"); nunca do appsettings versionado.
    claude => builder.Configuration.GetSection("Claude").Bind(claude),
    // Plano mensal: usa o Claude Code da máquina, logado com a conta do Claude.
    claudeCode => builder.Configuration.GetSection("ClaudeCode").Bind(claudeCode));

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = exception switch
    {
        FileNotFoundException or KeyNotFoundException => StatusCodes.Status404NotFound,
        InvalidOperationException or ArgumentException => StatusCodes.Status400BadRequest,
        AiProviderException or AnthropicApiException or ClaudeCodeNotInstalledException => StatusCodes.Status502BadGateway,
        TimeoutException => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError,
    };
    await context.Response.WriteAsJsonAsync(new { error = exception?.Message }, ErrorJson);
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.Run();

public partial class Program
{
    private static readonly System.Text.Json.JsonSerializerOptions ErrorJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
