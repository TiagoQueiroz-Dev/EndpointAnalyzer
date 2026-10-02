using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using EndpointAnalyzer.Application;
using Microsoft.Extensions.DependencyInjection;

// Uso:
//   EndpointAnalyzer.Cli <solucao.sln>                                  lista os endpoints POST/PUT/PATCH
//   EndpointAnalyzer.Cli <solucao.sln> --endpoint "POST /api/x"         análise estática do endpoint
//   EndpointAnalyzer.Cli <solucao.sln> --endpoint "POST /api/x" --ai    análise + documentação pela IA
//   EndpointAnalyzer.Cli <solucao.sln> --all [--ai] --out docs/         gera um .md por endpoint
// Opções: --json (imprime o relatório em JSON), --out <arquivo ou pasta>,
//         --ai-mode auto|subscription|api (assinatura do Claude via Claude Code ou API)
//         --view negocio|tecnico|completo (grafo exibido na análise estática)

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Uso: EndpointAnalyzer.Cli <solucao.sln|projeto.csproj> [--endpoint \"POST /api/rota\"] [--all] [--ai] [--ai-mode auto|subscription|api] [--json] [--out caminho]");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;

var solutionPath = args[0];
var endpointArg = Option("--endpoint");
var outPath = Option("--out");
var useAi = args.Contains("--ai");
var asJson = args.Contains("--json");
var all = args.Contains("--all");
var view = Option("--view") ?? "negocio"; // negocio | tecnico | completo

var services = new ServiceCollection()
    .AddEndpointAnalyzer(configureClaude: o =>
    {
        o.Model = Environment.GetEnvironmentVariable("ENDPOINT_ANALYZER_MODEL") ?? o.Model;
        o.Effort = Environment.GetEnvironmentVariable("ENDPOINT_ANALYZER_EFFORT") ?? o.Effort;
    })
    .BuildServiceProvider();

var service = services.GetRequiredService<EndpointAnalysisService>();

// No plano mensal vale a escolha salva pela interface; as variáveis de ambiente têm prioridade.
var claudeCode = services.GetRequiredService<EndpointAnalyzer.AI.ClaudeCode.ClaudeCodeOptions>();
claudeCode.Model = Environment.GetEnvironmentVariable("ENDPOINT_ANALYZER_MODEL") ?? claudeCode.Model;
claudeCode.Effort = Environment.GetEnvironmentVariable("ENDPOINT_ANALYZER_EFFORT") ?? claudeCode.Effort;

// --ai-mode auto (padrão) | subscription (plano mensal via Claude Code) | api (ANTHROPIC_API_KEY)
if (Option("--ai-mode") is { } mode)
{
    if (!Enum.TryParse<AiMode>(mode, ignoreCase: true, out var aiMode))
    {
        Console.Error.WriteLine("--ai-mode deve ser auto, subscription ou api.");
        return 1;
    }
    services.GetRequiredService<AiProviderRouter>().Mode = aiMode;
}
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

try
{
    if (useAi && !await service.IsAiAvailableAsync())
    {
        Console.Error.WriteLine("IA não disponível: rode `claude auth login` (plano mensal) ou defina ANTHROPIC_API_KEY.");
        return 2;
    }

    var endpoints = await service.ListEndpointsAsync(solutionPath);

    if (endpointArg is null && !all)
    {
        Console.WriteLine($"Endpoints em {Path.GetFileName(solutionPath)}:");
        Console.WriteLine();
        foreach (var e in endpoints)
            Console.WriteLine($"  {e.HttpMethod,-6} {e.Route,-45} {e.Controller}.{e.Action}  ({e.SourceFile}:{e.SourceLine})");
        Console.WriteLine();
        Console.WriteLine($"{endpoints.Count} endpoint(s). Use --endpoint \"METODO /rota\" para analisar.");
        return 0;
    }

    var selected = all ? endpoints.ToList() : [await service.FindEndpointAsync(solutionPath, endpointArg!)];

    foreach (var endpoint in selected)
    {
        if (useAi) Console.Error.WriteLine($"Analisando {endpoint.Id} com IA...");
        var report = await service.AnalyzeAsync(solutionPath, endpoint, useAi);

        var output = asJson
            ? JsonSerializer.Serialize(report, jsonOptions)
            : useAi || outPath is not null ? ReportRenderer.Markdown(report) : ReportRenderer.StaticSummary(report.Context, view);

        if (outPath is null)
        {
            Console.WriteLine(output);
            continue;
        }

        var file = all || Directory.Exists(outPath) || outPath.EndsWith('/') || outPath.EndsWith('\\')
            ? Path.Combine(outPath, FileName(endpoint.Id) + (asJson ? ".json" : ".md"))
            : outPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, output);
        Console.WriteLine($"{endpoint.Id} → {file}");
    }

    return 0;
}
catch (Exception ex) when (ex is FileNotFoundException or KeyNotFoundException or InvalidOperationException or ArgumentException
                               or EndpointAnalyzer.AI.AiProviderException or TimeoutException)
{
    Console.Error.WriteLine(Environment.GetEnvironmentVariable("ENDPOINT_ANALYZER_DEBUG") is null ? ex.Message : ex.ToString());
    return 1;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FileName(string endpointId)
{
    var invalid = Path.GetInvalidFileNameChars().Concat(['/', '{', '}', ' ', ':']).ToHashSet();
    var name = new string(endpointId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    return string.Join('_', name.Split('_', StringSplitOptions.RemoveEmptyEntries));
}
