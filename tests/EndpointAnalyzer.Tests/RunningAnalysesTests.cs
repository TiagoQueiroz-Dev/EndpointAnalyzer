using EndpointAnalyzer.Application;

namespace EndpointAnalyzer.Tests;

public class RunningAnalysesTests
{
    [Fact]
    public void Mesma_analise_em_andamento_e_recusada_ate_a_primeira_terminar()
    {
        var running = new RunningAnalyses();
        var solution = Path.Combine(Path.GetTempPath(), "Api.sln");

        var first = running.Start(solution, "Api", "POST /api/x");
        var duplicate = Assert.Throws<AnalysisSessionException>(() => running.Start(solution, "Api", "POST /api/x"));
        Assert.Equal(409, duplicate.StatusCode);

        // Outro endpoint, outro projeto ou outra solução rodam normalmente.
        using (running.Start(solution, "Api", "PUT /api/x")) { }
        using (running.Start(solution, "Outro", "POST /api/x")) { }
        using (running.Start(Path.Combine(Path.GetTempPath(), "Outra.sln"), "Api", "POST /api/x")) { }

        first.Dispose();
        first.Dispose();
        using var again = running.Start(solution, "Api", "POST /api/x");
    }
}
