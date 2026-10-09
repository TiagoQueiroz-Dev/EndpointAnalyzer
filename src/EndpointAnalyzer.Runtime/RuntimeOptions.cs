namespace EndpointAnalyzer.Runtime;

/// <summary>
/// Configuração da validação dos cenários em runtime (seção "Runtime" do appsettings). Só é usada na análise com IA.
/// </summary>
public class RuntimeOptions
{
    /// <summary>SQLite local; nulo usa %LOCALAPPDATA%/EndpointAnalyzer/analyses.db.</summary>
    public string? AnalysisDatabasePath { get; set; }

    /// <summary>Tempo de retenção do contexto em memória; análises no SQLite não expiram.</summary>
    public int AnalysisSessionMinutes { get; set; } = 30;

    public int MaxAnalysisSessions { get; set; } = 20;

    /// <summary>Orçamento separado da exploração automática, por sessão de análise.</summary>
    public int MaxManualRequests { get; set; } = 30;

    public int MaxManualAttemptsPerScenario { get; set; } = 10;

    public int MaxManualBodyChars { get; set; } = 100_000;

    /// <summary>Valida a matriz em runtime nas análises com IA (a requisição pode desligar).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>.csproj do serviço a compilar e executar; padrão: o projeto onde o endpoint foi encontrado.</summary>
    public string? Project { get; set; }

    public int StartupTimeoutSeconds { get; set; } = 180;

    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>POST/PUT/PATCH/DELETE no serviço (false: só GETs, os cenários de escrita ficam não materializados).</summary>
    public bool AllowWrites { get; set; } = true;

    /// <summary>Headers enviados em todas as requisições (ex.: Authorization do usuário de testes).</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Limite de requisições por análise (aquisição + cenários).</summary>
    public int MaxRequests { get; set; } = 300;

    /// <summary>Rodadas de aquisição de dados com a IA (cada uma pode pedir várias requisições).</summary>
    public int MaxAcquisitionRounds { get; set; } = 3;

    /// <summary>Requisições pedidas pela IA em cada rodada de aquisição ou exploração.</summary>
    public int MaxRequestsPerRound { get; set; } = 12;

    /// <summary>Execuções do endpoint alvo por cenário.</summary>
    public int MaxAttemptsPerScenario { get; set; } = 6;

    /// <summary>Rodadas de exploração (tentativa e erro guiada pela IA a partir do baseline).</summary>
    public int MaxExplorationRounds { get; set; } = 6;

    /// <summary>Cenários da matriz candidata validados por análise (os demais ficam inconclusivos).</summary>
    public int MaxScenarios { get; set; } = 40;

    /// <summary>Tamanho máximo do corpo de resposta guardado em cada execução.</summary>
    public int MaxStoredBodyChars { get; set; } = 8000;
}
