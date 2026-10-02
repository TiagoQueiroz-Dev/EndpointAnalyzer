# Endpoint Analyzer

Analisa o código-fonte de uma API ASP.NET Core e gera a documentação das regras de negócio de cada endpoint `POST`, `PUT` e `PATCH`.

Primeiro o **Roslyn** faz uma análise determinística: call graph, condições e alterações de entidades. Depois, só o contexto relevante vai para o **Claude**, que devolve a documentação em JSON estruturado, com evidência (arquivo/linha) e confiança para cada regra.

Especificação completa: [`endpoint-analyzer-projeto.md`](endpoint-analyzer-projeto.md).

## Estrutura

```text
src/
  EndpointAnalyzer.Core             modelos (EndpointInfo, CallNode, ConditionInfo, EntityChange...) e IAiProvider
  EndpointAnalyzer.Scanner          SolutionLoader, EndpointScanner, MethodResolver, CallGraphBuilder,
                                    InterfaceResolver, DependencyInjectionMap, SyntaxConditions
  EndpointAnalyzer.ChangeDetection  ConditionAnalyzer, EntityChangeAnalyzer, EntityCatalog
  EndpointAnalyzer.Context          AnalysisContextBuilder, SecretSanitizer
  EndpointAnalyzer.AI               ClaudeProvider, PromptBuilder, AnalysisResultSchema
  EndpointAnalyzer.Application      EndpointAnalysisService (orquestra), cache, versionamento, ReportRenderer
  EndpointAnalyzer.Api              API REST + interface web (wwwroot/index.html)
  EndpointAnalyzer.Cli              linha de comando
samples/SampleApi                   API de exemplo usada como alvo e nos testes
tests/EndpointAnalyzer.Tests        testes unitários e de integração
```

## Requisitos

- .NET SDK 10
- Para usar a IA, uma das opções abaixo. Sem nenhuma delas, a análise estática funciona normalmente.
  - **Plano mensal do Claude (Pro/Max/Team):** [Claude Code](https://claude.com/claude-code) instalado e logado com a conta do Claude. Faça o login pelo botão **Entrar com Claude** da interface ou com `claude auth login`. A análise roda via `claude -p` e consome o limite do plano, sem cobrança por uso.
  - **API:** a variável `ANTHROPIC_API_KEY` (cobrança por uso).

No modo automático (padrão), o plano mensal tem prioridade quando o Claude Code está logado com a conta do Claude.

## Linha de comando

```powershell
dotnet build EndpointAnalyzer.sln
$cli = "src/EndpointAnalyzer.Cli/bin/Debug/net10.0/EndpointAnalyzer.Cli.exe"

& $cli samples/SampleApi.sln                                        # lista os endpoints
& $cli samples/SampleApi.sln --endpoint "POST /api/programacoes"    # análise estática
& $cli samples/SampleApi.sln --endpoint "POST /api/programacoes" --ai
& $cli samples/SampleApi.sln --all --ai --out docs/                 # um .md por endpoint
& $cli samples/SampleApi.sln --endpoint "ProgramacaoController.Criar" --json
```

Exemplo de saída (sem IA):

```text
ProgramacaoController.Criar
→ ProgramacaoService.Criar   [via IProgramacaoService]
  → VeiculoRepository.ObterPorId   [via IVeiculoRepository; se request.Data >= DateTime.Today && request.VeiculoId != null]
  → ProgramacaoRepository.Adicionar   [via IProgramacaoRepository; se request.Data >= DateTime.Today]

Conditions:
  [if] request.Data < DateTime.Today
      → throw RegraNegocioException "Data inválida: a programação não pode estar no passado."

Changes:
  Programacao
  INSERT  quando: request.Data >= DateTime.Today
    Programacao.Status = ProgramacaoStatus.Pendente
    Programacao.VeiculoId = veiculo.Id   [se ... && request.VeiculoId != null && veiculo.Disponivel]
```

Variáveis opcionais: `ENDPOINT_ANALYZER_MODEL` (padrão `claude-opus-5-5`), `ENDPOINT_ANALYZER_EFFORT` (padrão `high`) e `ENDPOINT_ANALYZER_DEBUG=1` (mostra o stack trace dos erros).

## API e interface web

```powershell
dotnet run --project src/EndpointAnalyzer.Api
```

Abra `http://localhost:5168`: informe a `.sln`, selecione o endpoint na barra lateral e clique em **ANALISAR**.

A barra lateral segue o Scalar. Os endpoints aparecem agrupados pela tag do OpenAPI (`[Tags("...")]` no método ou controller, `[SwaggerOperation(Tags = ...)]`; sem tag, o nome do controller). Cada item mostra o resumo (`[EndpointSummary]`, `[SwaggerOperation(Summary = ...)]` ou o `<summary>` do comentário XML). A busca (Ctrl+K) filtra por resumo, rota, método ou grupo. No rodapé ficam a conta do Claude e os seletores de modelo e effort.

| Método | Rota | Descrição |
|---|---|---|
| GET | `/api/projects/endpoints?path=...` | lista os endpoints (`path` opcional; padrão em `Analyzer:SolutionPath`) |
| GET | `/api/projects/info` | solução padrão e se a IA está configurada |
| GET | `/api/projects/source?path=...&file=...&line=...` | código do arquivo (só documentos da solução) com o trecho que começa em `line` em destaque (`endLine` opcional) |
| POST | `/api/projects/source/condition` | `{ "condition": "x != null", "methods": [{ "file", "line" }] }` → código de onde vem a condição do registro |
| POST | `/api/analysis` | `{ "endpoint": { "method": "POST", "route": "/api/programacoes" }, "useAi": true }` → relatório + Markdown |
| POST | `/api/analysis/markdown` | mesmo corpo, devolve `text/markdown` |
| GET | `/api/auth/status` | conta do Claude Code (e-mail, plano) e origem da IA ativa |
| POST | `/api/auth/login` | `{ "email": "..." }` → URL de login da conta do Claude |
| POST | `/api/auth/code` | `{ "code": "..." }` → conclui o login com o código exibido após autorizar |
| POST | `/api/auth/logout` | desconecta o Claude Code da máquina |
| POST | `/api/auth/mode` | `{ "mode": "auto" \| "subscription" \| "api" }` |

As rotas `/api/auth/*` só aceitam chamadas da própria máquina. O login vale para o Claude Code dessa máquina: **Sair** também desconecta o Claude Code do terminal.

Na CLI, use `--ai-mode subscription` ou `--ai-mode api` para escolher a origem da IA.

Configuração em `appsettings.json` (`Analyzer`, `Claude`). A chave da API **não** deve ir para o appsettings; use `ANTHROPIC_API_KEY` ou `dotnet user-secrets set Claude:ApiKey ...`.

## O que o MVP detecta

- **Endpoints:** controllers (`[ApiController]`, `ControllerBase` ou sufixo `Controller`), `[Route]` do controller e da classe base, `[HttpPost/Put/Patch("template")]`, tokens `[controller]`/`[action]`/`[area]`.
- **Call graph:** chamadas, construtores e métodos de entidades. Interfaces e métodos abstratos são resolvidos para a implementação, usando o DI (`AddScoped<I, T>()`) quando há mais de uma. Também evita loops (métodos já visitados), respeita `MaxCallDepth` (20) e ignora `System.*`, `Microsoft.*`, `Newtonsoft.*`, `AutoMapper.*`, `Serilog.*` e código sem fonte.
- **Condições:** `if`, `switch` (statement e expression), `throw`, `?? throw`, `cond ? x : throw`, `ThrowIfNull`/`Guard`, DataAnnotations dos DTOs e regras `RuleFor` do FluentValidation.
- **Condição de cada alteração:** combina os `if`/`else`/`switch`/ternários, as guardas anteriores (`if (x) throw;` → vale `!x`) e as condições do caminho desde o endpoint.
- **Entidades:** `DbSet<T>` e `modelBuilder.Entity<T>()` (ou uma heurística quando não há DbContext). Detecta `Add/Update/Remove`, `ExecuteUpdate/ExecuteDelete`, métodos de repositório (`Adicionar`, `Atualizar`, `Excluir`...), `new Entidade { ... }`, atribuições de propriedades e `SaveChanges`.
- **Segurança:** `SecretSanitizer` remove senhas, tokens, connection strings, JWTs e chaves antes do envio, e o contexto leva só os métodos alcançáveis e os DTOs, enums e entidades relevantes.
- **Cache e versionamento:** a resposta da IA fica em `%LOCALAPPDATA%/EndpointAnalyzer/cache`, com chave = hash do contexto + modelo + versão do analisador. Cada relatório registra commit, branch, data, modelo e versão.

## Remoção de ruído

O analisador segue só o que influencia o comportamento do endpoint, não tudo que pode ser alcançado. A especificação está em [`remocao-ruidos-analise-estatica.md`](remocao-ruidos-analise-estatica.md).

| Etapa | Onde | O que faz |
|---|---|---|
| DIResolver | `Scanner/DependencyInjectionMap.cs` | Registros `AddScoped/Transient/Singleton` (inclusive `TryAdd*` e genéricos abertos `typeof(IRepository<>)`), só dos projetos do host do endpoint |
| Resolução de chamadas | `Scanner/CallResolver.cs` | Prioridade: receiver concreto → DI pelo tipo do receiver → construtor (inclusive `: base(...)` e construtor primário) → atribuição → busca. Sobrando várias implementações, a chamada fica **ambígua** e não é expandida |
| GenericTypeResolver | `Scanner/ExecutionContext.cs` | Contexto por nó com genéricos (`TEntity = Veiculo`), `this` concreto (herança contextual) e tipos/valores dos argumentos |
| BranchFeasibilityAnalyzer | `Scanner/BranchFeasibilityAnalyzer.cs` | Corta branches impossíveis: `is`/`switch` por tipo, null checks, bools e enums conhecidos (ex.: `DomainNotification` nunca é `ProgramacaoTransporteEvent`) |
| Data flow | `Scanner/ValueUsageAnalyzer.cs` | Para onde vai o resultado de cada chamada (condição, retorno, exceção, argumento, atribuição). Consultas só ficam quando influenciam uma decisão |
| EffectSinkDetector | `ChangeDetection/Relevance/EffectSinkDetector.cs` | Sinks: THROW, RETURN, INSERT/UPDATE/DELETE, SaveChanges, Publish/Send (BUSINESS_EVENT, DOMAIN_ERROR, INFRA_EVENT), HTTP e arquivo. Classes PRIMARY, SECONDARY e INFRASTRUCTURE |
| Classificação e slicing | `ChangeDetection/Relevance/RelevanceAnalyzer.cs` | Categoria, score e justificativa de cada nó; colapsa a infraestrutura ("Retorna erro de validação"), esconde helpers (registrados em `helpers`) e monta o **grafo de negócio** de trás para frente, a partir dos sinks |
| Registro de condições | idem | `C1: ModelState.IsValid`... as alterações e os nós referenciam ids em vez de concatenar condições |

Resultado:
- O contexto traz o **grafo de negócio** (`businessGraph`, exibido por padrão) e o **técnico** (`callTree`, para debug e auditoria).
- A IA recebe só o grafo de negócio, as condições, os efeitos e o código dos métodos relevantes.
- Na interface, as abas são Resumo, Negócio, Técnico (infraestrutura colapsada) e Completo (grafo bruto). Na CLI, use `--view negocio|tecnico|completo`.

Os padrões de nome usados na classificação (sufixos de tipo e prefixos de método por categoria) ficam em `AnalyzerOptions.Relevance`. Para acrescentar padrões do seu projeto, use o `appsettings.json`:

```json
"Analyzer": {
  "Relevance": {
    "InfrastructureTypeSuffixes": [ "Auditor" ],
    "DataAccessMethodPrefixes": [ "Pesquisar" ]
  }
}
```

## Matriz de cenários

Aba **Cenários** (e seção "Matriz de cenários" no Markdown): cada cenário tem **payload + estado/pré-condições + resultado esperado**, seguindo [`tecnologias-fases-geracao-payloads.md`](tecnologias-fases-geracao-payloads.md). Projeto `src/EndpointAnalyzer.Scenarios`.

| Etapa | Onde | O que faz |
|---|---|---|
| Payload | `InputModel.cs` | Parâmetros da action (rota, query, header, body) e campos dos DTOs, com tipo e nulidade |
| Regras de entrada | `ValidationRules.cs` | DataAnnotations, `[Required]` implícito (referência não anulável) e `RuleFor` do FluentValidation (com `When`/`WithMessage`) |
| Condições → predicados | `SymbolicResolver.cs` | Segue parâmetros até o payload, locais até a inicialização e chamadas até a consulta (repositório, DbContext); o que não traduz vira suposição |
| Fluxo | `FlowModel.cs`, `ExceptionStatusMap.cs` | Regras que bloqueiam (throw, return de erro, notificação, guardas) na ordem de execução, saídas antecipadas, efeitos e status HTTP (return do controller, try/catch na cadeia, middleware de exceções ou nome da exceção) |
| Cenários | `ScenarioGenerator.cs` | Tabela de decisão (caminho feliz, uma linha por regra com as anteriores passando, variações dos ramos, saídas antecipadas), partição de equivalência (ausente, vazio, só espaços, formato, tipo inválido) e valores-limite (dos dois lados) |
| Solver | `Solver.cs` | Domínios próprios (núcleo); **Z3** (`Microsoft.Z3`) só em restrições com várias variáveis ou aritmética (`peso * quantidade > capacidade`) |

O resultado esperado vem da simulação do fluxo com os valores encontrados (primeira regra que dispara, ou sucesso com os efeitos). A IA só escreve título e descrição de cada cenário (`scenarios` no JSON); a chave do cache passou a ser o prompt, que não leva as datas concretas do payload.

## Fica para depois (seção 42 da especificação)

Minimal APIs (`MapPost`), MediatR, eventos/mensageria, `CodexProvider`, persistência em SQLite/PostgreSQL e análise em runtime (`SaveChangesInterceptor`).

## Testes

```powershell
dotnet test
```
