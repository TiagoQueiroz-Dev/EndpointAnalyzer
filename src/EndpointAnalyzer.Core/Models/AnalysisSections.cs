namespace EndpointAnalyzer.Core.Models;

/// <summary>
/// Abas do resultado que a análise deve produzir. Cada uma liga só as etapas de que precisa:
/// Resumo (documentação da IA), Negócio (rótulos do fluxograma), Completo (matriz/Z3 e runtime) e Cenários (matriz, títulos da IA e runtime).
/// </summary>
[Flags]
public enum AnalysisSections
{
    None = 0,
    Summary = 1,
    Business = 2,
    Complete = 4,
    Scenarios = 8,
    All = Summary | Business | Complete | Scenarios,
}

public static class AnalysisSectionNames
{
    private static readonly (string Name, AnalysisSections Section)[] Names =
    [
        ("summary", AnalysisSections.Summary),
        ("business", AnalysisSections.Business),
        ("complete", AnalysisSections.Complete),
        ("scenarios", AnalysisSections.Scenarios),
    ];

    /// <summary>["summary", "scenarios"] → Summary | Scenarios. Nulo = todas.</summary>
    /// <param name="allowEmpty">Lista vazia vale None (ex.: abas que usam IA) em vez de erro.</param>
    public static AnalysisSections Parse(IEnumerable<string>? names, bool allowEmpty = false)
    {
        if (names is null) return AnalysisSections.All;
        var sections = AnalysisSections.None;
        foreach (var name in names)
        {
            var match = Names.FirstOrDefault(n => string.Equals(n.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
                throw new ArgumentException($"Aba desconhecida: '{name}'. Use {string.Join(", ", Names.Select(n => n.Name))}.");
            sections |= match.Section;
        }
        return sections == AnalysisSections.None && !allowEmpty ? throw new ArgumentException("Selecione ao menos uma aba para a análise.") : sections;
    }

    public static List<string> ToNames(AnalysisSections sections) =>
        Names.Where(n => sections.HasFlag(n.Section)).Select(n => n.Name).ToList();
}
