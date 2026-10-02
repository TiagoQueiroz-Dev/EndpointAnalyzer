namespace EndpointAnalyzer.Core.Models;

public class SourceReference
{
    public string File { get; set; } = "";

    public string? Method { get; set; }

    public int Line { get; set; }

    public string? Code { get; set; }

    public override string ToString() => $"{File}:{Line}";
}
