using EndpointAnalyzer.AI;
using EndpointAnalyzer.AI.ClaudeCode;
using EndpointAnalyzer.AI.Codex;
using EndpointAnalyzer.ChangeDetection;
using EndpointAnalyzer.ChangeDetection.Relevance;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Core.Interfaces;
using EndpointAnalyzer.Runtime;
using EndpointAnalyzer.Scanner;
using EndpointAnalyzer.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;

namespace EndpointAnalyzer.Application;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o analisador. A IA pode rodar pela assinatura do Claude (Claude Code logado com claude.ai), pela conta do
    /// Codex (Codex CLI logado com o ChatGPT) ou pela API do Claude; a API só é registrada quando há credencial
    /// (ClaudeOptions.ApiKey, ANTHROPIC_API_KEY/ANTHROPIC_AUTH_TOKEN ou perfil do `ant auth login`).
    /// </summary>
    public static IServiceCollection AddEndpointAnalyzer(
        this IServiceCollection services,
        Action<AnalyzerOptions>? configureAnalyzer = null,
        Action<ClaudeOptions>? configureClaude = null,
        Action<ClaudeCodeOptions>? configureClaudeCode = null,
        Action<RuntimeOptions>? configureRuntime = null,
        Action<CodexOptions>? configureCodex = null)
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

        var codexOptions = new CodexOptions();
        configureCodex?.Invoke(codexOptions);
        codexOptions.LoadSelection();

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
        services.AddSingleton<IBusinessFlowAiAnalyzer, BusinessFlowAiAnalyzer>();
        services.AddSingleton(new AnalysisCache(AnalysisCache.DefaultDirectory));

        // Validação dos cenários com a API em execução (só na análise com IA).
        services.AddSingleton(runtimeOptions);
        var keyDirectory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(SqliteAnalysisStore.DatabasePath(runtimeOptions))!, "keys"));
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(keyDirectory.FullName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var protection = services.AddDataProtection().SetApplicationName("EndpointAnalyzer")
            .PersistKeysToFileSystem(keyDirectory);
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        services.AddSingleton<SqliteAnalysisStore>();
        services.AddSingleton<AnalysisSessionStore>();
        services.AddSingleton<AnalysisDiffService>();
        services.AddSingleton<IAppRunner, AppRunner>();
        services.AddSingleton<RuntimeValidationService>();
        services.AddSingleton<ApiTokenChecker>();

        // Plano mensal: Claude Code da máquina.
        services.AddSingleton(claudeCodeOptions);
        services.AddSingleton<ClaudeCodeAuth>();
        services.AddSingleton<ClaudeModelCatalog>();
        services.AddSingleton<ClaudeCodeUsage>();
        // Várias contas Claude, cada uma com o próprio login; a primeira reaproveita o login acima.
        services.AddSingleton<AiAvailability>();
        services.AddSingleton(sp => new ClaudeAccounts(claudeCodeOptions, sp.GetRequiredService<AiAvailability>(),
            sp.GetRequiredService<ClaudeCodeAuth>(), sp.GetRequiredService<ClaudeCodeUsage>(), sp.GetRequiredService<ClaudeModelCatalog>()));

        // Contas do Codex (limite do plano do ChatGPT): Codex CLI da máquina, cada conta com o próprio login; a primeira
        // reaproveita o login do projeto (Codex:HomeDirectory).
        services.AddSingleton(codexOptions);
        services.AddSingleton<CodexAuth>();
        services.AddSingleton<CodexModelCatalog>();
        services.AddSingleton(sp => new CodexAccounts(codexOptions, sp.GetRequiredService<AiAvailability>(),
            sp.GetRequiredService<CodexAuth>(), sp.GetRequiredService<CodexModelCatalog>()));
        // Continuar no outro provedor ao esgotar as contas: ao lado das contas do Claude (padrão: a pasta de dados do analisador).
        services.AddSingleton(new AiProviderSettings(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(claudeCodeOptions.AccountsDirectory))!, "ai-settings.json")));

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
