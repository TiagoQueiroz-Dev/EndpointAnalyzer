using System.Text.Json;
using EndpointAnalyzer.AI;
using EndpointAnalyzer.Context;
using EndpointAnalyzer.Core.Models;
using EndpointAnalyzer.Scanner;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EndpointAnalyzer.Tests;

public class SecretSanitizerTests
{
    [Theory]
    [InlineData("private const string SmtpPassword = \"abc123\";", "abc123")]
    [InlineData("var cs = \"Server=db;Database=app;User Id=sa;Password=P@ss;\";", "P@ss")]
    [InlineData("var key = \"sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA\";", "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("client.Token = \"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U\";", "dozjgNryP4J3jVmNHl0w5N")]
    [InlineData("headers.Add(\"Authorization\", \"Bearer abcdefghijklmnopqrstuvwxyz123456\");", "abcdefghijklmnopqrstuvwxyz123456")]
    public void Remove_valores_sensiveis(string code, string secret) =>
        Assert.DoesNotContain(secret, SecretSanitizer.Sanitize(code));

    [Fact]
    public void Mantem_codigo_comum() =>
        Assert.Equal("if (request.Data < DateTime.Today) throw new X(\"Data inválida\");",
            SecretSanitizer.Sanitize("if (request.Data < DateTime.Today) throw new X(\"Data inválida\");"));
}

public class SyntaxConditionsTests
{
    [Theory]
    [InlineData("a == b", "a != b")]
    [InlineData("a != null", "a == null")]
    [InlineData("!x.Ativo", "x.Ativo")]
    [InlineData("(a < b)", "a >= b")]
    [InlineData("x is null", "x is not null")]
    [InlineData("x is not null", "x is null")]
    [InlineData("a && b", "(!a || !b)")]
    [InlineData("a || b", "!a && !b")]
    [InlineData("x.Ativo() && y != null", "(!x.Ativo() || y == null)")]
    public void Nega_condicoes(string condition, string expected) =>
        Assert.Equal(expected, SyntaxConditions.Negate(SyntaxFactory.ParseExpression(condition)));

    [Fact]
    public void Divide_conjuncao_respeitando_parenteses() =>
        Assert.Equal(["a", "(b || c && d)", "f(x && y)"], SyntaxConditions.SplitConjunction("a && (b || c && d) && f(x && y)"));

    [Fact]
    public void Calcula_parte_comum() =>
        Assert.Equal("a && c", SyntaxConditions.Common(["a && b && c", "a && c", "c && a && d"]));

    [Fact]
    public void Inclui_guardas_anteriores_no_caminho()
    {
        var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("""
            void M()
            {
                if (p.Status == Cancelada) throw new E();
                if (r.Valor != null)
                {
                    p.Valor = r.Valor;
                }
            }
            """)!;
        var assignment = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single();

        Assert.Equal("p.Status != Cancelada && r.Valor != null", SyntaxConditions.GetEnclosingCondition(assignment, method));
    }

    [Fact]
    public void Caminho_de_controle_identifica_estrutura_e_ramo()
    {
        var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("""
            void M()
            {
                if (!ModelState.IsValid) return Erro();
                foreach (var item in itens)
                {
                    if (item.Ativo) Processar(item);
                    else Ignorar(item);
                }
                try { Salvar(); }
                catch (DomainException) { Tratar(); }
            }
            """)!;
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .ToDictionary(c => c.Expression.ToString(), c => SyntaxConditions.GetControlPath(c, method));

        // Dentro da guarda: ramo "sim" do if; depois dela: ramo "não" do mesmo if, com a saída.
        var guard = Assert.Single(calls["Erro"]!);
        Assert.Equal(("if", "!ModelState.IsValid", "sim", (string?)null), (guard.Kind, guard.Expression, guard.Branch, guard.Exit));

        var processar = calls["Processar"]!;
        Assert.Equal(["if:não:return", "foreach:sim:", "if:sim:"], processar.Select(s => $"{s.Kind}:{s.Branch}:{s.Exit}"));
        Assert.Equal(guard.Key, processar[0].Key);
        Assert.Equal("item em itens", processar[1].Expression);
        // Linhas da estrutura inteira (o popup de código destaca esse trecho).
        Assert.Equal((3, 3), (guard.Line, guard.EndLine));
        Assert.Equal((4, 8), (processar[1].Line, processar[1].EndLine));

        // if/else: mesma estrutura, ramos diferentes.
        var ignorar = calls["Ignorar"]!;
        Assert.Equal(processar[2].Key, ignorar[2].Key);
        Assert.Equal("não", ignorar[2].Branch);

        Assert.Equal(["if:não", "catch:DomainException"], calls["Tratar"]!.Select(s => $"{s.Kind}:{s.Branch}"));
        Assert.Equal(["if:não"], calls["Salvar"]!.Select(s => $"{s.Kind}:{s.Branch}"));
    }

    [Fact]
    public void Origens_apontam_para_o_codigo_que_gerou_cada_condicao()
    {
        var method = (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration("""
            void M()
            {
                if (p.Status == Cancelada || p.Fim) throw new E();
                p.Valor = r?.Valor ?? 0;
            }
            """)!;
        var origins = SyntaxConditions.Origins(method).ToLookup(o => o.Atom, o => method.SyntaxTree.GetText().ToString(o.Origin));

        // Guarda negada (De Morgan): cada parte aponta para a condição do if.
        Assert.Contains("p.Status == Cancelada || p.Fim", origins["p.Status != Cancelada"]);
        Assert.Contains("p.Status == Cancelada || p.Fim", origins["!p.Fim"]);
        Assert.Contains("r", origins["r != null"]);
        Assert.Contains("r?.Valor", origins["r?.Valor == null"]);
    }
}

public class PromptBuilderTests
{
    [Fact]
    public void Prompt_inclui_endpoint_fluxo_e_codigo()
    {
        var context = new EndpointAnalysisContext
        {
            Endpoint = new EndpointInfo { HttpMethod = "POST", Route = "/api/programacoes", Controller = "ProgramacaoController", Action = "Criar" },
            CallGraph = ["ProgramacaoController.Criar", "ProgramacaoService.Criar"],
            Methods = [new CodeSnippet { Name = "ProgramacaoService.Criar", File = "ProgramacaoService.cs", Line = 81, Code = "if (x) throw new Y();" }],
        };

        var prompt = PromptBuilder.BuildUserPrompt(context);

        Assert.Contains("Endpoint: POST /api/programacoes", prompt);
        Assert.Contains("ProgramacaoController.Criar\n→ ProgramacaoService.Criar", prompt);
        Assert.Contains("<method name=\"ProgramacaoService.Criar\" file=\"ProgramacaoService.cs\" line=\"81\">", prompt);
    }

    [Fact]
    public void Prompt_nao_inclui_estrutura_de_controle_do_fluxograma()
    {
        var context = new EndpointAnalysisContext
        {
            Endpoint = new EndpointInfo { HttpMethod = "POST", Route = "/x", Controller = "C", Action = "A" },
            BusinessGraph = new CallNode
            {
                TypeName = "C", MethodName = "A",
                Children = [new CallNode { TypeName = "S", MethodName = "M", Condition = "x != null",
                    Control = [new ControlStep { Kind = ControlKinds.If, Key = "if@1:2", Expression = "x != null", Branch = "sim" }] }],
            },
        };

        var prompt = PromptBuilder.BuildUserPrompt(context);

        Assert.Contains("\"condition\": \"x != null\"", prompt);
        Assert.DoesNotContain("\"control\"", prompt);
    }

    [Fact]
    public void Itens_do_fluxograma_param_na_infraestrutura_e_ignoram_resumos()
    {
        var context = new EndpointAnalysisContext
        {
            CallTree = new CallNode
            {
                TypeName = "C", MethodName = "A",
                Children =
                [
                    new CallNode { TypeName = "S", MethodName = "M", Control = [new ControlStep { Kind = ControlKinds.If, Expression = "x != null" }] },
                    new CallNode { TypeName = "Bus", MethodName = "Publicar", Category = NodeCategories.Infrastructure,
                        Children = [new CallNode { TypeName = "Bus", MethodName = "Interno" }] },
                ],
            },
            BusinessGraph = new CallNode
            {
                TypeName = "C", MethodName = "A",
                Children = [new CallNode { TypeName = "Bus", MethodName = "Publicar", Summary = "Publica evento X" }],
            },
        };

        var json = JsonSerializer.Serialize(PromptBuilder.FlowItems(context));

        Assert.Equal("""{"methods":["C.A","S.M","Bus.Publicar"],"decisions":[{"expression":"x != null","kind":"if"}]}""", json);
    }

    [Fact]
    public void Schema_exige_todos_os_campos()
    {
        var schema = AnalysisResultSchema.Create();
        var required = schema["required"].EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["summary", "businessRules", "validations", "entityChanges", "uncertainties", "flowLabels", "scenarios"], required);
        Assert.False(schema["additionalProperties"].GetBoolean());

        // O JSON devolvido pela IA desserializa no modelo da aplicação.
        var json = """{"summary":"s","businessRules":[{"id":"REGRA-001","description":"d","confidence":0.99,"evidence":{"file":"f.cs","method":"m","line":81,"code":"c"}}],"validations":[],"entityChanges":[{"entity":"Programacao","operation":"INSERT","properties":[{"name":"Status","condition":""}]}],"uncertainties":[]}""";
        var result = JsonSerializer.Deserialize<EndpointAnalysisResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(81, result.BusinessRules[0].Evidence!.Line);
        Assert.Equal("Status", result.EntityChanges[0].Properties[0].Name);
    }
}
