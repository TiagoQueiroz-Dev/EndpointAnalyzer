# Projeto: Analisador Inteligente de Endpoints .NET

## 1. Objetivo

Criar uma aplicação capaz de analisar o código-fonte de um projeto ASP.NET Core e:

1. Identificar automaticamente os endpoints existentes.
2. Exibir os endpoints disponíveis para o usuário.
3. Permitir selecionar um endpoint para análise.
4. Rastrear o fluxo interno executado por esse endpoint.
5. Identificar:
   - regras de negócio;
   - validações;
   - serviços chamados;
   - repositórios utilizados;
   - entidades criadas ou alteradas;
   - propriedades alteradas;
   - condições necessárias para que cada alteração ocorra.
6. Enviar apenas o contexto relevante para uma IA, como Claude ou Codex.
7. Gerar uma documentação compreensível do comportamento do endpoint.

Inicialmente, o projeto deve analisar apenas endpoints:

- `POST`
- `PUT`
- `PATCH`

Endpoints `GET` e `DELETE` podem ser ignorados no MVP.

---

# 2. Princípio da arquitetura

A IA não deve receber o repositório inteiro e tentar descobrir tudo sozinha.

O sistema deve primeiro realizar uma análise determinística do código usando Roslyn.

Fluxo:

```text
Código-fonte
    ↓
Descoberta de endpoints
    ↓
Análise estática com Roslyn
    ↓
Call Graph
    ↓
Detecção de alterações
    ↓
Contexto estruturado
    ↓
Claude / Codex
    ↓
Relatório de regras de negócio
```

Responsabilidades:

```text
Roslyn
→ entende a estrutura do código.

IA
→ interpreta semanticamente o código e transforma em documentação.
```

---

# 3. Stack sugerida

## Backend

```text
.NET 10
ASP.NET Core
Roslyn
Microsoft.CodeAnalysis
Microsoft.CodeAnalysis.CSharp
Microsoft.CodeAnalysis.Workspaces.MSBuild
```

## Frontend

Pode ser utilizado:

```text
Angular
```

ou inicialmente uma interface simples usando:

```text
Swagger / Razor / Blazor
```

O frontend não é prioridade no MVP.

---

# 4. Estrutura sugerida da solução

```text
EndpointAnalyzer.sln

src/

    EndpointAnalyzer.Api/
        Controllers/
        Program.cs

    EndpointAnalyzer.Core/
        Models/
        Interfaces/

    EndpointAnalyzer.Scanner/
        EndpointScanner.cs
        SolutionLoader.cs
        CallGraphBuilder.cs
        SymbolResolver.cs

    EndpointAnalyzer.ChangeDetection/
        EntityChangeAnalyzer.cs
        PropertyChangeAnalyzer.cs
        EfCoreAnalyzer.cs

    EndpointAnalyzer.Context/
        AnalysisContextBuilder.cs

    EndpointAnalyzer.AI/
        IAiProvider.cs
        ClaudeProvider.cs
        CodexProvider.cs
        PromptBuilder.cs

    EndpointAnalyzer.Application/
        EndpointAnalysisService.cs

tests/

    EndpointAnalyzer.Tests/
```

---

# 5. Modelo principal

Criar um modelo representando um endpoint.

```csharp
public class EndpointInfo
{
    public string HttpMethod { get; set; }

    public string Route { get; set; }

    public string Controller { get; set; }

    public string Action { get; set; }

    public string SourceFile { get; set; }

    public int SourceLine { get; set; }
}
```

Exemplo:

```json
{
  "httpMethod": "POST",
  "route": "/api/programacoes",
  "controller": "ProgramacaoController",
  "action": "Criar",
  "sourceFile": "Controllers/ProgramacaoController.cs",
  "sourceLine": 34
}
```

---

# 6. Etapa 1 — carregar a solução

O sistema deverá receber o caminho de uma solução `.sln`.

Exemplo:

```text
D:\Projetos\Sistema\Sistema.sln
```

Utilizar:

```csharp
MSBuildWorkspace
```

Exemplo conceitual:

```csharp
using Microsoft.CodeAnalysis.MSBuild;

var workspace = MSBuildWorkspace.Create();

var solution = await workspace.OpenSolutionAsync(
    @"D:\Projetos\Sistema\Sistema.sln"
);
```

A partir desse ponto será possível acessar:

```text
Solution
Projects
Documents
SyntaxTree
SemanticModel
Symbols
```

---

# 7. Etapa 2 — descobrir endpoints

O scanner deve procurar controllers ASP.NET Core.

Exemplo:

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProgramacaoController : ControllerBase
{
}
```

Depois deve procurar métodos com atributos:

```text
HttpPost
HttpPut
HttpPatch
```

Exemplo:

```csharp
[HttpPost]
public async Task<IActionResult> Criar(...)
```

Ou:

```csharp
[HttpPut("{id}")]
public async Task<IActionResult> Atualizar(...)
```

O scanner deverá montar:

```text
POST /api/programacao
PUT /api/programacao/{id}
```

---

# 8. EndpointScanner

Responsabilidade:

```text
Percorrer todos os documentos C#.
        ↓
Encontrar classes Controller.
        ↓
Encontrar métodos públicos.
        ↓
Detectar HttpPost / HttpPut / HttpPatch.
        ↓
Resolver Route.
        ↓
Criar EndpointInfo.
```

Interface:

```csharp
public interface IEndpointScanner
{
    Task<IReadOnlyList<EndpointInfo>> ScanAsync(
        Solution solution
    );
}
```

---

# 9. Etapa 3 — selecionar endpoint

O frontend poderá mostrar:

```text
[ ] POST   /api/programacoes
[ ] PUT    /api/programacoes/{id}
[ ] PATCH  /api/programacoes/{id}
```

Quando o usuário selecionar:

```text
POST /api/programacoes
```

o backend receberá algo como:

```json
{
  "controller": "ProgramacaoController",
  "action": "Criar"
}
```

---

# 10. Etapa 4 — localizar o método do endpoint

Utilizar Roslyn para encontrar o símbolo:

```text
ProgramacaoController.Criar
```

Representado pelo Roslyn como:

```csharp
IMethodSymbol
```

A partir desse método começa a análise.

---

# 11. Etapa 5 — construir o Call Graph

O sistema deverá descobrir quais métodos são executados a partir daquele endpoint.

Exemplo:

```csharp
public async Task<IActionResult> Criar(...)
{
    await _programacaoService.Criar(request);

    return Ok();
}
```

O analisador identifica:

```text
ProgramacaoController.Criar
        ↓
ProgramacaoService.Criar
```

Depois entra no método:

```csharp
public async Task Criar(...)
{
    await _repository.Adicionar(...);
}
```

Resultado:

```text
ProgramacaoController.Criar
        ↓
ProgramacaoService.Criar
        ↓
ProgramacaoRepository.Adicionar
```

---

# 12. Modelo do Call Graph

```csharp
public class CallNode
{
    public string MethodName { get; set; }

    public string TypeName { get; set; }

    public string SourceFile { get; set; }

    public int SourceLine { get; set; }

    public List<CallNode> Children { get; set; }
}
```

Exemplo:

```json
{
  "method": "Criar",
  "type": "ProgramacaoController",
  "children": [
    {
      "method": "Criar",
      "type": "ProgramacaoService",
      "children": [
        {
          "method": "Adicionar",
          "type": "ProgramacaoRepository"
        }
      ]
    }
  ]
}
```

---

# 13. Etapa 6 — resolver interfaces

Um problema comum será:

```csharp
private readonly IProgramacaoService _service;
```

O endpoint chama:

```csharp
_service.Criar(...)
```

Porém o código real está em:

```csharp
ProgramacaoService
```

O sistema precisa descobrir:

```text
IProgramacaoService
        ↓
ProgramacaoService
```

Isso deve ser feito utilizando símbolos do Roslyn.

O analisador deverá localizar classes que implementem:

```csharp
INamedTypeSymbol
```

e verificar:

```text
AllInterfaces
```

---

# 14. Dependências e DI

Também deve ser possível analisar registros de Dependency Injection.

Exemplo:

```csharp
services.AddScoped<
    IProgramacaoService,
    ProgramacaoService
>();
```

Resultado:

```text
IProgramacaoService
→ ProgramacaoService
```

Isso ajuda quando existem múltiplas implementações.

---

# 15. Evitar loops no Call Graph

Um método pode chamar outro método que volta a chamar o primeiro.

Exemplo:

```text
A → B → C → A
```

O analisador deve manter:

```csharp
HashSet<ISymbol>
```

com métodos já visitados.

Exemplo:

```text
VisitedMethods
```

Antes de visitar um método:

```text
já visitou?

SIM → não entra novamente.
NÃO → continua análise.
```

---

# 16. Limite de profundidade

Também deve existir:

```text
MAX_CALL_DEPTH
```

Exemplo:

```text
20
```

Isso evita percorrer bibliotecas ou fluxos enormes indefinidamente.

---

# 17. Ignorar código irrelevante

Não seguir chamadas pertencentes a:

```text
System.*
Microsoft.*
Newtonsoft.*
AutoMapper.*
Serilog.*
```

Também não seguir métodos externos sem código-fonte disponível.

Exemplo:

```text
DateTime.Now
Task.Delay
ILogger.LogInformation
```

---

# 18. Etapa 7 — detectar regras e condições

O analisador deve coletar estruturas:

```text
if
switch
throw
return antecipado
validações
```

Exemplo:

```csharp
if (programacao.Status == ProgramacaoStatus.Cancelada)
{
    throw new RegraNegocioException(
        "Programação cancelada não pode ser alterada."
    );
}
```

Gerar:

```json
{
  "type": "condition",
  "expression": "programacao.Status == ProgramacaoStatus.Cancelada",
  "action": "throw RegraNegocioException",
  "message": "Programação cancelada não pode ser alterada."
}
```

---

# 19. Modelo de regra encontrada

```csharp
public class ConditionInfo
{
    public string Expression { get; set; }

    public string Action { get; set; }

    public string Message { get; set; }

    public string SourceFile { get; set; }

    public int SourceLine { get; set; }
}
```

---

# 20. Etapa 8 — detectar entidades

O sistema deverá detectar entidades manipuladas durante o fluxo.

Exemplo:

```csharp
var programacao =
    await _context.Programacoes
        .FirstAsync(x => x.Id == id);
```

Resultado:

```text
Entity:
Programacao
```

---

# 21. Detectar INSERT

Exemplos:

```csharp
_context.Programacoes.Add(programacao);
```

```csharp
_context.Add(programacao);
```

```csharp
_repository.Adicionar(programacao);
```

Resultado:

```json
{
  "entity": "Programacao",
  "operation": "INSERT"
}
```

---

# 22. Detectar UPDATE

Exemplo:

```csharp
programacao.Status =
    ProgramacaoStatus.Finalizada;
```

Resultado:

```json
{
  "entity": "Programacao",
  "operation": "UPDATE",
  "property": "Status"
}
```

---

# 23. Detectar múltiplos campos

Exemplo:

```csharp
programacao.Status = ProgramacaoStatus.Finalizada;
programacao.DataFim = DateTime.Now;
programacao.UsuarioFinalizacaoId = usuarioId;
```

Resultado:

```json
{
  "entity": "Programacao",
  "operation": "UPDATE",
  "properties": [
    "Status",
    "DataFim",
    "UsuarioFinalizacaoId"
  ]
}
```

---

# 24. Detectar alterações condicionais

Exemplo:

```csharp
if (request.VeiculoId != null)
{
    programacao.VeiculoId = request.VeiculoId;
}
```

Resultado:

```json
{
  "entity": "Programacao",
  "property": "VeiculoId",
  "condition": "request.VeiculoId != null"
}
```

Isso é importante para a documentação final.

---

# 25. Etapa 9 — detectar SaveChanges

Procurar:

```csharp
SaveChanges()
```

ou:

```csharp
SaveChangesAsync()
```

Isso ajuda a determinar onde as alterações são efetivamente persistidas.

---

# 26. EntityChangeAnalyzer

Interface:

```csharp
public interface IEntityChangeAnalyzer
{
    Task<IReadOnlyList<EntityChange>>
        AnalyzeAsync(CallNode root);
}
```

Modelo:

```csharp
public class EntityChange
{
    public string Entity { get; set; }

    public string Operation { get; set; }

    public List<string> Properties { get; set; }

    public string Condition { get; set; }

    public SourceReference Source { get; set; }
}
```

---

# 27. Etapa 10 — gerar um contexto estruturado

Depois da análise estática, gerar um JSON.

Exemplo:

```json
{
  "endpoint": {
    "method": "POST",
    "route": "/api/programacoes"
  },

  "callGraph": [
    "ProgramacaoController.Criar",
    "ProgramacaoService.Criar",
    "ProgramacaoRepository.Adicionar"
  ],

  "conditions": [
    {
      "expression": "request.Data < DateTime.Today",
      "action": "throw",
      "message": "Data inválida"
    }
  ],

  "entityChanges": [
    {
      "entity": "Programacao",
      "operation": "INSERT",
      "properties": [
        "Status",
        "DataInicio",
        "VeiculoId"
      ]
    }
  ]
}
```

---

# 28. Não mandar apenas o JSON para a IA

Além da estrutura, envie também os pequenos trechos de código que servem de evidência.

Exemplo:

```json
{
  "condition": "request.Data < DateTime.Today",

  "source": {
    "file": "ProgramacaoService.cs",
    "line": 81
  },

  "code": "if (request.Data < DateTime.Today) throw ..."
}
```

Isso permite que a IA interprete corretamente o contexto.

---

# 29. Etapa 11 — integração com IA

Criar uma abstração.

```csharp
public interface IAiProvider
{
    Task<EndpointAnalysisResult> AnalyzeAsync(
        EndpointAnalysisContext context
    );
}
```

Implementações:

```text
ClaudeProvider
CodexProvider
```

Assim a aplicação não fica dependente de um único fornecedor.

---

# 30. Prompt sugerido

O PromptBuilder poderá gerar algo semelhante a:

```text
Você é um analisador de código e regras de negócio.

Analise exclusivamente as informações fornecidas.

Não invente regras que não estejam sustentadas pelo código.

Endpoint:

POST /api/programacoes

Fluxo:

ProgramacaoController.Criar
→ ProgramacaoService.Criar
→ ProgramacaoRepository.Adicionar

Condições encontradas:

...

Entidades alteradas:

...

Código relevante:

...

Retorne:

1. Objetivo do endpoint.
2. Regras de negócio.
3. Validações.
4. Entidades criadas.
5. Entidades alteradas.
6. Campos alterados.
7. Condições necessárias para cada alteração.
8. Evidência de código para cada conclusão.
9. Pontos que não puderam ser determinados com certeza.
```

---

# 31. Formato obrigatório da resposta da IA

Não retornar texto livre.

Utilizar JSON estruturado.

Exemplo:

```json
{
  "summary": "Cria uma nova programação de transporte.",

  "businessRules": [
    {
      "description": "Não permite datas anteriores ao dia atual.",
      "confidence": 0.99,
      "evidence": {
        "file": "ProgramacaoService.cs",
        "line": 81
      }
    }
  ],

  "entityChanges": [
    {
      "entity": "Programacao",
      "operation": "INSERT",
      "properties": [
        "Status",
        "DataInicio",
        "VeiculoId"
      ]
    }
  ]
}
```

---

# 32. Por que usar resposta estruturada

Evita que a IA responda de formas diferentes a cada execução.

Permite:

```text
renderizar interface;
salvar resultados;
comparar endpoints;
exportar documentação;
gerar testes;
detectar regressões.
```

---

# 33. Resultado apresentado ao usuário

Exemplo:

```text
POST /api/programacoes
```

## Objetivo

Criar uma programação de transporte.

## Regras

### REGRA 001

Não permitir criação de programação com data anterior à atual.

Evidência:

```text
ProgramacaoService.cs:81
```

### REGRA 002

Toda nova programação deve iniciar com status `Pendente`.

Evidência:

```text
ProgramacaoService.cs:102
```

---

## Entidades alteradas

### Programacao

Operação:

```text
INSERT
```

Campos:

```text
Status
DataInicio
VeiculoId
```

---

# 34. Confiança

Toda conclusão da IA deve possuir confiança.

Exemplo:

```text
Alta
Média
Baixa
```

ou:

```text
0.00 → 1.00
```

Exemplo:

```json
{
  "description": "Programação inicia como pendente.",
  "confidence": 0.99
}
```

---

# 35. Evidência obrigatória

Uma regra não deve ser apresentada como confirmada sem evidência.

Formato:

```text
Regra

↓

Arquivo

↓

Método

↓

Linha

↓

Trecho do código
```

Exemplo:

```text
Programação cancelada não pode ser modificada.

ProgramacaoService.cs
Método: Atualizar
Linha: 143
```

Trecho:

```csharp
if (programacao.Status == Cancelada)
    throw new RegraNegocioException(...);
```

---

# 36. Análise dinâmica futura

Depois do MVP pode ser adicionada análise em runtime.

Utilizar:

```text
EF Core
SaveChangesInterceptor
```

O interceptor poderá registrar:

```text
entidade
estado
propriedade
valor anterior
valor novo
```

Exemplo:

```json
{
  "entity": "Programacao",
  "state": "Modified",

  "changes": {
    "Status": {
      "old": "Pendente",
      "new": "Finalizada"
    }
  }
}
```

---

# 37. Static Analysis + Runtime Analysis

No futuro:

```text
STATIC ANALYSIS

O endpoint pode alterar:

Programacao
Veiculo
Motorista
```

+

```text
RUNTIME ANALYSIS

Neste cenário foram alterados:

Programacao
Veiculo
```

Isso permite descobrir diferenças entre:

```text
o que o código pode fazer

e

o que realmente aconteceu em determinada execução.
```

---

# 38. EndpointAnalysisService

Classe que orquestra todo o processo.

```csharp
public class EndpointAnalysisService
{
    private readonly ICallGraphBuilder _callGraph;
    private readonly IEntityChangeAnalyzer _changes;
    private readonly IAnalysisContextBuilder _context;
    private readonly IAiProvider _ai;

    public async Task<EndpointAnalysisResult> Analyze(
        EndpointInfo endpoint)
    {
        var graph =
            await _callGraph.BuildAsync(endpoint);

        var changes =
            await _changes.AnalyzeAsync(graph);

        var context =
            await _context.BuildAsync(
                endpoint,
                graph,
                changes
            );

        return await _ai.AnalyzeAsync(context);
    }
}
```

---

# 39. API do analisador

Exemplo:

```text
GET /api/projects/endpoints
```

Retorno:

```json
[
  {
    "method": "POST",
    "route": "/api/programacoes"
  },

  {
    "method": "PUT",
    "route": "/api/programacoes/{id}"
  }
]
```

---

Analisar endpoint:

```text
POST /api/analysis
```

Body:

```json
{
  "endpoint": {
    "method": "POST",
    "route": "/api/programacoes"
  }
}
```

---

# 40. Interface inicial

Uma interface simples já é suficiente.

```text
ENDPOINT ANALYZER

Projeto:
Sistema.sln

Endpoints:

[ ] POST  /api/programacoes
[ ] PUT   /api/programacoes/{id}
[ ] PATCH /api/programacoes/{id}

        [ ANALISAR ]
```

Resultado:

```text
POST /api/programacoes

Objetivo:
Criar programação.

Regras:
3

Entidades alteradas:
2

Programacao
INSERT

Veiculo
UPDATE
```

---

# 41. MVP

O primeiro MVP NÃO deve tentar resolver todos os padrões possíveis de um projeto .NET.

Suportar inicialmente:

```text
ASP.NET Core Controllers

HttpPost
HttpPut
HttpPatch

Services

Interfaces

Repositories

Entity Framework Core

Add
Update
SaveChanges
SaveChangesAsync

atribuições de propriedades

if

throw

switch
```

---

# 42. Recursos que podem ficar para depois

Não implementar inicialmente:

```text
reflection complexa;
dynamic;
event sourcing;
CQRS completo;
MediatR avançado;
mensageria;
Kafka;
RabbitMQ;
stored procedures;
triggers SQL;
microserviços externos;
reflection runtime;
source generators;
expression trees complexas.
```

Adicionar esses recursos progressivamente.

---

# 43. Suporte futuro para MediatR

Projetos podem utilizar:

```csharp
await _mediator.Send(command);
```

Fluxo:

```text
Controller
   ↓
Command
   ↓
CommandHandler
   ↓
Service
   ↓
Repository
```

Criar futuramente:

```text
MediatRAnalyzer
```

para resolver:

```text
IRequest<T>
→ IRequestHandler<T>
```

---

# 44. Suporte futuro para eventos

Exemplo:

```csharp
await _bus.Publish(
    new ProgramacaoCriadaEvent(...)
);
```

Nesse cenário:

```text
Endpoint
   ↓
Event
   ↓
Consumer A
   ↓
Consumer B
```

O sistema precisará tratar isso como:

```text
efeitos indiretos.
```

---

# 45. Persistência das análises

Inicialmente pode ser usado:

```text
SQLite
```

Depois:

```text
PostgreSQL
```

Salvar:

```text
Project
Endpoint
Analysis
BusinessRule
EntityChange
SourceReference
AnalysisVersion
```

---

# 46. Versionamento

Cada análise deve armazenar:

```text
commit hash
branch
data
modelo de IA utilizado
versão do analisador
```

Exemplo:

```json
{
  "commit": "a817dd5",
  "branch": "develop",
  "aiModel": "model-x",
  "analyzerVersion": "0.1.0"
}
```

Isso permite comparar comportamento entre versões do código.

---

# 47. Cache

Não analisar o mesmo código repetidamente.

Chave:

```text
commit hash
+
endpoint
+
analyzer version
```

Se não mudou:

```text
usar análise anterior.
```

---

# 48. Segurança

Nunca enviar automaticamente:

```text
appsettings secrets
connection strings
tokens
passwords
certificados
.env
```

Antes de montar o contexto da IA criar:

```text
SecretSanitizer
```

Ele deve remover valores sensíveis.

---

# 49. Contexto mínimo

A IA deve receber somente:

```text
endpoint;
métodos alcançáveis;
condições;
entidades;
alterações;
DTOs relevantes;
enums relevantes;
trechos necessários.
```

Evitar enviar:

```text
repositório inteiro.
```

Benefícios:

```text
menos tokens;
menor custo;
resposta mais rápida;
menos distração;
menos hallucinação.
```

---

# 50. Ordem recomendada de implementação

## Fase 1

Criar solução.

```text
EndpointAnalyzer.sln
```

---

## Fase 2

Implementar:

```text
SolutionLoader
```

Objetivo:

```text
abrir .sln usando Roslyn.
```

---

## Fase 3

Implementar:

```text
EndpointScanner
```

Resultado esperado:

```text
listar POST, PUT e PATCH.
```

---

## Fase 4

Implementar:

```text
MethodResolver
```

Objetivo:

```text
endpoint
→ IMethodSymbol
```

---

## Fase 5

Implementar:

```text
CallGraphBuilder
```

Primeira versão:

```text
método
→ método
→ método
```

---

## Fase 6

Implementar:

```text
InterfaceResolver
```

Resolver:

```text
IService
→ Service
```

---

## Fase 7

Implementar:

```text
ConditionAnalyzer
```

Detectar:

```text
if
switch
throw
```

---

## Fase 8

Implementar:

```text
EntityChangeAnalyzer
```

Detectar:

```text
Add
Update
property assignment
SaveChanges
```

---

## Fase 9

Gerar:

```text
EndpointAnalysisContext
```

em JSON.

---

## Fase 10

Integrar primeiro provedor de IA.

```text
IAiProvider
```

---

## Fase 11

Criar interface.

```text
lista endpoints
↓
selecionar
↓
analisar
↓
exibir relatório
```

---

# 51. Primeiro objetivo técnico

Antes de integrar qualquer IA, o sistema deve conseguir receber:

```text
POST /api/programacoes
```

e imprimir:

```text
ProgramacaoController.Criar
→ ProgramacaoService.Criar
→ ProgramacaoRepository.Adicionar

Conditions:

request.Data < DateTime.Today

Changes:

Programacao
INSERT

Programacao.Status
Programacao.DataInicio
Programacao.VeiculoId
```

Se isso funcionar, a parte mais difícil do projeto já estará encaminhada.

---

# 52. Segundo objetivo técnico

Adicionar IA e transformar:

```text
Programacao.Status = Pendente
```

em:

```text
Regra:

Toda nova programação inicia com status Pendente.
```

---

# 53. Terceiro objetivo técnico

Gerar automaticamente documentação semelhante a:

```text
POST /api/programacoes

Descrição:
Cria uma programação de transporte.

Regras:

1.
A data da programação não pode estar no passado.

2.
Toda nova programação inicia com status Pendente.

3.
Caso um veículo seja informado, ele é associado
à programação.

Entidades:

Programacao
INSERT

Campos:

Status
DataInicio
VeiculoId
```

---

# 54. Arquitetura final

```text
                 ┌─────────────────────┐
                 │      FRONTEND       │
                 │                     │
                 │ Endpoint Explorer   │
                 └─────────┬───────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │         API         │
                 └─────────┬───────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │    SolutionLoader   │
                 └─────────┬───────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │   EndpointScanner   │
                 └─────────┬───────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │  CallGraphBuilder   │
                 │      ROSLYN         │
                 └─────────┬───────────┘
                           │
                           ▼
              ┌──────────────────────────┐
              │   Static Code Analysis   │
              │                          │
              │ Conditions               │
              │ Entities                 │
              │ Properties               │
              │ Persistence              │
              └────────────┬─────────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │   Context Builder   │
                 └─────────┬───────────┘
                           │
                           ▼
                 ┌─────────────────────┐
                 │     AI Provider     │
                 │                     │
                 │ Claude / Codex      │
                 └─────────┬───────────┘
                           │
                           ▼
              ┌──────────────────────────┐
              │       DOCUMENTAÇÃO       │
              │                          │
              │ Regras                   │
              │ Entidades                │
              │ Campos                   │
              │ Condições                │
              │ Evidências               │
              └──────────────────────────┘
```

---

# 55. Visão final do produto

O produto deve funcionar conceitualmente como:

```text
Código-fonte
       ↓
Compilador / Analisador
       ↓
Modelo estruturado do comportamento
       ↓
IA
       ↓
Documentação de regras de negócio
```

A ferramenta não deve ser apenas:

```text
"uma IA lendo um projeto".
```

Ela deve funcionar como:

```text
compilador de código
        ↓
modelo de comportamento
        ↓
documentação inteligente.
```

Essa separação é essencial para tornar o sistema mais confiável, auditável e escalável.
