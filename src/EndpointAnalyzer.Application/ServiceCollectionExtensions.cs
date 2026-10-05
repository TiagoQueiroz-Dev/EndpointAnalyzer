using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.ChangeDetection.Relevance;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointAnalyzer.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o analisador. A IA pode rodar pela assinatura do Claude (Claude Code logado com claude.ai)
    /// ou pela API; a API só é registrada quando há credencial
    /// (ClaudeOptions.ApiKey, ANTHROPIC_API_KEY/ANTHROPIC_AUTH_TOKEN ou perfil do `ant auth login`).
    /// </summary>
    public static IServiceCollection AddEndpointAnalyzer(
        this IServiceCollection services,
        Action<AnalyzerOptions>? configureAnalyzer = null,
        Action<ClaudeOptions>? configureClaude = null,
        Action<ClaudeCodeOptions>? configureClaudeCode = null,
        Action<RuntimeOptions>? configureRuntime = null)
    {
        var analyzerOptions = new AnalyzerOptions();
        configureAnalyzer?.Invoke(analyzerOptions);

        var runtimeOptions = new RuntimeOptions();
        configureRuntime?.Invoke(runtimeOptions);

        var claudeOptions = new ClaudeOptions();
        configureClaude?.Invoke(claudeOptions);

        var claudeCodeOptions = new ClaudeCodeOptions();
        configureClaudeCode?.Invoke(claudeCodeOptions);
        // Modelo/effort escolhidos na interface prevalecem sobre o appsettings.
        claudeCodeOptions.LoadSelection();

        services.AddSingleton(analyzerOptions);
        services.AddSingleton<ISolutionLoader, SolutionLoader>();
        services.AddSingleton<SolutionCache>();
        services.AddSingleton<IEndpointScanner, EndpointScanner>();
        services.AddSingleton<IMethodResolver, MethodResolver>();
        services.AddSingleton<ICallGraphBuilder, CallGraphBuilder>();
        services.AddSingleton<IConditionAnalyzer, ConditionAnalyzer>();
        services.AddSingleton<IEntityChangeAnalyzer, EntityChangeAnalyzer>();
        services.AddSingleton<IRelevanceAnalyzer, RelevanceAnalyzer>();
        services.AddSingleton<IAnalysisContextBuilder, AnalysisContextBuilder>();
        services.AddSingleton<IScenarioGenerator, ScenarioGenerator>();
        services.AddSingleton(new AnalysisCache(AnalysisCache.DefaultDirectory));

        // Validação dos cenários com a API em execução (só na análise com IA).
        services.AddSingleton(runtimeOptions);
        services.AddSingleton<IAppRunner, AppRunner>();
        services.AddSingleton<RuntimeValidationService>();
        services.AddSingleton<ApiTokenChecker>();

        // Plano mensal: Claude Code da máquina.
        services.AddSingleton(claudeCodeOptions);
        services.AddSingleton<ClaudeCodeAuth>();
        services.AddSingleton<ClaudeCodeProvider>();
        services.AddSingleton<ClaudeModelCatalog>();

        // API: cobrança por uso.
        var hasCredential = !string.IsNullOrWhiteSpace(claudeOptions.ApiKey)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "anthropic"));
        if (hasCredential)
        {
            services.AddSingleton(claudeOptions);
            services.AddSingleton<ClaudeProvider>();
        }

        services.AddSingleton<AiProviderRouter>();
        services.AddSingleton<IAiProviderSelector>(sp => sp.GetRequiredService<AiProviderRouter>());

        services.AddSingleton<EndpointAnalysisService>();
        services.AddSingleton<SourceViewer>();
        return services;
    }
}
