using EndpointAnalyzer.Core.Models;

namespace EndpointAnalyzer.Application;

public sealed record ScenarioRevalidationResult(string AnalysisId, RuntimeValidation Runtime, string Markdown, RuntimeExecution Execution);
