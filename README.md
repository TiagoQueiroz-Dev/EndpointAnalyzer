# Endpoint Analyzer

Analisa o código-fonte de uma API ASP.NET Core e gera a documentação das regras de negócio de cada endpoint `POST`, `PUT` e `PATCH`.

Primeiro o **Roslyn** faz uma análise determinística: call graph, condições e alterações de entidades. Depois, só o contexto relevante vai para o **Claude**, que devolve a documentação em JSON estruturado, com evidência (arquivo/linha) e confiança para cada regra.

Especificação completa: [`endpoint-analyzer-projeto.md`](endpoint-analyzer-projeto.md).

## Estrutura

```text
src/
  EndpointAnalyzer.Core             modelos (EndpointInfo, CallNode, ConditionInfo, EntityChange...) e IAiProvider
  EndpointAnalyzer.Scanner          SolutionLoader, EndpointScanner, MethodResolver, CallGraphBuilder,
                                    InterfaceResolver, DependencyInjectionMap, MessageHandlerMap, SyntaxConditions
  EndpointAnalyzer.ChangeDetection  ConditionAnalyzer, EntityChangeAnalyzer, EntityCatalog
  EndpointAnalyzer.Context          AnalysisContextBuilder, SecretSanitizer
  EndpointAnalyzer.AI               ClaudeProvider, ClaudeCode (assinatura), Codex (CodexProvider, CodexAuth), PromptBuilder, AnalysisResultSchema
  EndpointAnalyzer.Runtime          validação da matriz com a API em execução (AppRunner, catálogo, IA, execução, reconciliação)
  EndpointAnalyzer.Application      EndpointAnalysisService (orquestra), cache, versionamento, ReportRenderer
  EndpointAnalyzer.Api              API REST + interface web (wwwroot/index.html)
  EndpointAnalyzer.Cli              linha de comando
samples/SampleApi                   API de exemplo usada como alvo e nos testes
tests/EndpointAnalyzer.Tests        testes unitários e de integração
```

## Requisitos

- .NET SDK 10
- Para usar a IA, uma das opções abaixo. Sem nenhuma delas, a análise estática funciona normalmente.
  - **Plano mensal do Claude (Pro/Max/Team):** [Claude Code](https://claude.com/claude-code) instalado na máquina (instalador nativo, npm ou no PATH). Faça o login pelo botão **Entrar com Claude** da interface. O login do projeto é separado do Claude Code do terminal (fica em `%LOCALAPPDATA%\EndpointAnalyzer\claude`, configurável em `ClaudeCode:ConfigDirectory`), então a conta logada no projeto pode ser outra. A análise roda via `claude -p` e consome o limite do plano da conta logada no projeto, sem cobrança por uso.
  - **Conta do Codex (plano do ChatGPT: Plus, Pro, Business...):** Codex CLI instalado na máquina (`npm install -g @openai/codex`, no PATH ou o CLI que acompanha o app desktop do Codex). Faça o login pelo botão **Entrar com Codex** da interface: a autorização abre no navegador e o próprio Codex recebe o retorno (porta local 1455), sem código para colar. O login do projeto é separado do Codex do terminal e do app (fica em `%LOCALAPPDATA%\EndpointAnalyzer\codex`, configurável em `Codex:HomeDirectory`). A análise roda via `codex exec` (sandbox somente leitura, diretório vazio, sessão não gravada) e consome o limite do plano da conta logada no projeto.
  - **API:** a variável `ANTHROPIC_API_KEY` (cobrança por uso).

As contas do Claude e do Codex são independentes: as duas podem ficar conectadas ao mesmo tempo, e conectar ou desconectar uma não afeta a outra. A interface escolhe o provedor de cada análise (seletor ao lado da origem da IA); toda a IA da análise (documentação, fluxo de negócio e validação em runtime) roda no provedor escolhido, com o mesmo contexto e o mesmo JSON Schema, então o resultado aparece nos mesmos relatórios. O relatório registra o provedor e o modelo (`version.aiProvider`, `version.aiModel`). Sem provedor na requisição, vale o Claude e, sem ele, o Codex. No Claude, o modo automático (padrão) dá prioridade ao plano mensal quando o Claude Code está logado com a conta do Claude.

Falha de autenticação (sessão expirada, conta desconectada) ou limite de uso atingido volta como erro tipado (`AiProviderUnavailableException`: HTTP 502 com `code: "authentication"` ou HTTP 429 com `code: "usage_limit"`, e `provider`), a validação em runtime para em vez de seguir sem IA, e a interface atualiza o estado da conta. É a base para continuar automaticamente uma análise no outro provedor. Mensagens de erro passam por remoção de tokens antes de chegar aos logs e relatórios.

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

A barra lateral segue o Scalar. Os endpoints aparecem agrupados pela tag do OpenAPI (`[Tags("...")]` no método ou controller, `[SwaggerOperation(Tags = ...)]`; sem tag, o nome do controller). Cada item mostra o resumo (`[EndpointSummary]`, `[SwaggerOperation(Summary = ...)]` ou o `<summary>` do comentário XML). A busca (Ctrl+K) filtra por resumo, rota, método ou grupo. No rodapé ficam as contas do Claude e do Codex, cada uma com os seletores de modelo e effort e o uso do limite do plano (sessão de 5 horas e semana, com o horário de renovação). O uso é atualizado depois de cada análise e ao voltar para a aba; no Claude, vem do mesmo `/usage` do Claude Code (consulta do protocolo de controle, sem prompt ao modelo e sem consumir o limite).

| Método | Rota | Descrição |
|---|---|---|
| GET | `/api/projects/endpoints?path=...` | lista os endpoints (`path` opcional; padrão em `Analyzer:SolutionPath`) |
| GET | `/api/projects/info` | solução padrão e se a IA está configurada |
| GET | `/api/projects/source?path=...&file=...&line=...` | código do arquivo (só documentos da solução) com o trecho que começa em `line` em destaque (`endLine` opcional) |
| POST | `/api/projects/source/condition` | `{ "condition": "x != null", "methods": [{ "file", "line" }] }` → código de onde vem a condição do registro |
| POST | `/api/analysis` | `{ "endpoint": { "method": "POST", "route": "/api/programacoes" }, "useAi": true, "provider": "codex" }` → relatório + Markdown (`provider`: `claude` ou `codex`, opcional) |
| POST | `/api/analysis/markdown` | mesmo corpo, devolve `text/markdown` |
| POST | `/api/analysis/token` | `{ "endpoint": { "method": "POST", "route": "/api/x" }, "token": "eyJ..." }` → sobe o serviço do endpoint (como na análise) e confere se o token é aceito: `{ valid, message }` |
| GET | `/api/auth/status` | conta do Claude Code (e-mail, plano) e origem da IA ativa |
| POST | `/api/auth/login` | `{ "email": "..." }` → URL de login da conta do Claude |
| POST | `/api/auth/code` | `{ "code": "..." }` → conclui o login com o código exibido após autorizar |
| POST | `/api/auth/logout` | desconecta a conta do Claude do projeto |
| POST | `/api/auth/mode` | `{ "mode": "auto" \| "subscription" \| "api" }` (origem do Claude) |
| GET | `/api/auth/usage` | uso do limite do plano do Claude (`usage.primary`: sessão, `usage.secondary`: semana; percentual e renovação), cache de 1 minuto (`refresh=true` força) |
| GET | `/api/auth/codex/status` | conta do Codex (e-mail, plano, método de login), uso do limite (`usage.primary`/`secondary`: percentual, janela, renovação), login em andamento |
| POST | `/api/auth/codex/login` | → URL de autorização da conta do ChatGPT (o retorno do navegador vai direto para o Codex) |
| POST | `/api/auth/codex/cancel` | cancela o login em andamento |
| POST | `/api/auth/codex/logout` | desconecta a conta do Codex do projeto |
| GET | `/api/auth/codex/models` | modelos da conta, efforts de cada um e a seleção atual |
| POST | `/api/auth/codex/model` | `{ "model": "...", "effort": "high" }` |

As rotas `/api/auth/*` só aceitam chamadas da própria máquina. O login vale só para o projeto: **Sair** não desconecta o Claude Code nem o Codex do terminal (ou o app do Codex), e entrar neles não muda a conta do projeto. Nenhuma rota devolve tokens: só e-mail, plano e percentuais de uso.

Na CLI, use `--provider claude` ou `--provider codex` para escolher a IA, e `--ai-mode subscription` ou `--ai-mode api` para a origem do Claude.

Configuração em `appsettings.json` (`Analyzer`, `Claude`, `ClaudeCode`, `Codex`). Em `Codex`: `Executable`, `HomeDirectory`, `Model` (vazio = padrão da conta), `Effort` e `TimeoutMinutes`. A chave da API **não** deve ir para o appsettings; use `ANTHROPIC_API_KEY` ou `dotnet user-secrets set Claude:ApiKey ...`.

### Fila de análises (vários endpoints)

Marque os endpoints na barra lateral e clique em **Analisar em fila**. O checkbox aparece ao passar o mouse sobre o endpoint (ou com foco pelo teclado) e, havendo seleção, em todos; Shift+clique marca o intervalo desde o último clicado. No cabeçalho do grupo ou do projeto, **Selecionar tudo** marca todos os endpoints exibidos dentro dele (com todos marcados, vira **Desmarcar**), ao lado da contagem ("2/4"). A fila analisa um endpoint por vez, na ordem, com as abas, a IA e o provedor marcados no card do endpoint no momento em que começou; o próximo só começa quando o atual termina. O painel da fila mostra a ordem, o progresso ("Analisando 2 de 8 endpoints"), o endpoint em execução, os pendentes e os concluídos, com erro ou cancelados; clicar num item abre o resultado dele, que fica salvo como o de uma análise avulsa (navegador e banco local).

- **Sem duplicidade:** um endpoint pendente ou em análise não entra de novo na fila, e o ANALISAR dele fica desabilitado enquanto estiver nela. Se o endpoint já estiver em análise pelo botão, a fila acompanha essa execução em vez de iniciar outra. No servidor, uma segunda análise do mesmo endpoint (outra aba, cliques repetidos) recebe 409 enquanto a primeira não termina.
- **Cancelamento:** **Cancelar atual** interrompe a análise em andamento (como o CANCELAR ANÁLISE) e a fila segue para o próximo; **Cancelar restantes** impede que os pendentes comecem. Um pendente pode ser removido; um item com erro ou cancelado pode voltar para o fim da fila (**Tentar de novo**).
- **Falhas:** a falha de um endpoint fica registrada no item e a fila continua. Limite de uso atingido ou sessão expirada não são falhas do endpoint: o item volta para a fila e ela pausa, preservando a ordem e os resultados, com **Retomar** e **Continuar com o Codex/Claude** (quando a outra conta está conectada).
- **Persistência:** a fila fica salva no navegador. Ao recarregar a página ela volta pausada (o item interrompido volta a pendente) e só continua com **Retomar**, para não consumir o limite sem o usuário pedir.

## O que o MVP detecta

- **Endpoints:** controllers (`[ApiController]`, `ControllerBase` ou sufixo `Controller`), `[Route]` do controller e da classe base, `[HttpPost/Put/Patch("template")]`, tokens `[controller]`/`[action]`/`[area]`.
- **Call graph:** chamadas, construtores e métodos de entidades. Interfaces e métodos abstratos são resolvidos para a implementação, usando o DI (`AddScoped<I, T>()`) quando há mais de uma. Também evita loops (métodos já visitados), respeita `MaxCallDepth` (20) e ignora `System.*`, `Microsoft.*`, `Newtonsoft.*`, `AutoMapper.*`, `Serilog.*` e código sem fonte.
- **Comandos e eventos (CQRS):** `mediator.Send(command)`, `bus.SendCommand(command)`, `Publish(evento)`... seguem até o `Handle` do handler da mensagem, inclusive quando o despacho passa por uma biblioteca sem fonte (MediatR). Handlers reconhecidos: `IRequestHandler<T>`/`IRequestHandler<T, R>`, `INotificationHandler<T>`, `ICommandHandler<T>`, `IEventHandler<T>`, `IConsumer<T>`, `IHandleMessages<T>` e qualquer interface ou classe base genérica terminada em `Handler`/`Consumer`. O handler entra como filho de quem envia (não do barramento, que é infraestrutura), com as regras, alterações e persistência dele. `DomainNotification` e eventos técnicos (histórico, log) não expandem handlers. Nos cenários, `Mapper.Map<Command>(viewModel)` liga as propriedades do comando aos campos de mesmo nome do payload.
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
| Handlers de mensagens | `Scanner/MessageHandlerMap.cs` | Comando/evento → `Handle` do handler (`IRequestHandler<T>`, `INotificationHandler<T>`...), preferindo os projetos do host. A chamada `Send`/`Publish` (ou feita num `*Bus`/`*Mediator`/`*Dispatcher`) ganha o handler como filho, com estratégia `handler da mensagem` |
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
| Condições → predicados | `SymbolicResolver.cs` | Segue parâmetros até o payload, locais até a inicialização e chamadas até a consulta (repositório, DbContext); o que não traduz vira suposição. **Propaga retornos entre métodos**: uma condição sobre o valor de um método do fluxo com vários returns (`if (!Validar(x))`, `if (resultado == null)`, `if (!resultado.Sucesso)`) vira a disjunção dos returns que a satisfazem, cada um com o caminho até ele dentro do método chamado — inclusive pelo barramento (`bus.EnviarComando` → `Handle`) |
| Fluxo de controle | `FlowModel.cs`, `ExceptionStatusMap.cs` | Regras que bloqueiam (throw, return de erro, notificação, guardas, lado de erro de `return cond ? NotFound() : Ok(x)`), returns do endpoint e de métodos internos, efeitos e status HTTP, na ordem interprocedural de execução |
| Pontos de encerramento | `TerminalOutcomeCollector.cs` | Onde a requisição realmente termina: cada validação de entrada, o tipo inválido no JSON, cada regra que encerra com erro, cada return do endpoint e a resposta final. Return de método interno **não** é ponto de encerramento: o valor dele é propagado e o fluxo segue no chamador |
| Caminhos | `ExecutionPathExplorer.cs` | Percorre os caminhos de execução até os pontos de encerramento, dividindo só onde os dois lados continuam satisfazíveis (regra dispara ou não, return acontece ou não, efeito condicional executa ou não); o solver poda os contraditórios |
| Desfechos | `TerminalOutcomeCollector.cs` | Agrupa os caminhos por ponto de encerramento e, nas respostas de sucesso, pelo conjunto de efeitos (entidade + operação, eventos, SaveChanges): caminhos diferentes que chegam ao mesmo ponto com os mesmos efeitos são **um desfecho só**. Viabilidade trivalente: **SAT**, **UNSAT** (com o motivo) ou **UNKNOWN** (condição opaca, limite de caminhos ou falha do solver), nunca convertida em "inalcançável" |
| Árvore de execução | `FlowTreeBuilder.cs`, `FlowTree.cs` | Visão lógica montada dos caminhos explorados: entrada HTTP, validações antes da action, decisões, efeitos e uma folha por desfecho (grafo com junções) |
| Cenários | `ScenarioGenerator.cs` | **Um cenário por desfecho**: resolve as restrições de um caminho até ele, monta payload, pré-condições e resultado esperado e só aceita o payload cuja simulação chega ao mesmo desfecho |
| Solver | `Solver.cs` | Domínios próprios (núcleo); **Z3** (`Microsoft.Z3`) só em restrições com várias variáveis ou aritmética (`peso * quantidade > capacidade`) |

O resultado esperado vem da simulação do fluxo com os valores encontrados (primeira regra que dispara, ou sucesso com os efeitos). A IA só escreve título e descrição de cada cenário (`scenarios` no JSON); a chave do cache passou a ser o prompt, que não leva as datas concretas do payload.

A matriz tem **uma linha por desfecho** (validação, tipo inválido, regra, return do endpoint, resposta final e cada conjunto de efeitos distinto de uma resposta de sucesso): toda forma de o endpoint encerrar aparece, e nada além disso. Classes de equivalência (null, vazio, espaços), limites (6/7/8 caracteres), iterações de laço e ramos que não mudam a resposta nem os efeitos **não** geram cenários: o título do cenário de validação diz qual classe o valor escolhido representa, e o `[Required]` sem efeito em value type vira observação. Cada cenário tem uma identidade estrutural estável (`key`: hash do endpoint e do desfecho no código), o desfecho que atinge (`terminal`) e a viabilidade (`feasibility`: `sat`, `unknown` ou `unsat`); `CEN-xx` é só exibição. O desfecho que nenhuma entrada gerada alcança (caminho contraditório, condição não decidida) entra **sem payload**, com o motivo em `noPayload`, e fica fora da execução em runtime. `ScenarioGenerationOptions.MaxPaths` limita os caminhos explorados; no limite, os desfechos sem caminho ficam UNKNOWN e a matriz avisa. A árvore vai em `flow`, e as condições não traduzidas aparecem nas observações. Na aba **Diff**, os cenários são pareados pela `key` (matrizes antigas, sem ela, pelo foco, regra e mensagens) e só o resultado esperado conta como mudança de comportamento: trocar o payload representativo é dado de teste.

## Validação dos cenários em runtime (só com IA)

Especificação: [`analise-ia-validacao-dinamica-cenarios.md`](analise-ia-validacao-dinamica-cenarios.md). Projeto `src/EndpointAnalyzer.Runtime`.

Sem IA nada muda: a matriz é a estática e nenhum projeto é iniciado. Com IA (e `Runtime:Enabled`, padrão), a matriz estática vira **candidata** e é validada com a API rodando de verdade; a aba **Cenários** e o Markdown passam a mostrar a **matriz validada** (a estática continua disponível na alternância "Estática (candidata)").

| Etapa | Onde | O que faz |
|---|---|---|
| AppRunner | `AppRunner.cs` | `dotnet build` do projeto do endpoint numa pasta temporária exclusiva (`%TEMP%/EndpointAnalyzer/runtime/...`, fora do `bin/` do projeto, então funciona mesmo com o serviço já rodando pela IDE) e execução dessa cópia em `http://localhost:{porta livre}`, com a pasta do projeto como diretório de trabalho (content root, como na IDE) e sem herdar o `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` do analisador; aguarda o serviço responder; no fim encerra o processo e apaga a pasta temporária (também em falha, cancelamento ou se o analisador cair). Ambiente, configuração e conexão com o banco são os do próprio projeto (responsabilidade do usuário) |
| EndpointCatalog | `EndpointCatalog.cs` | `EndpointScanner` com todos os verbos (GET inclusive): parâmetros, DTOs e formato da resposta. Requisições fora do catálogo são recusadas |
| ScenarioRequirementPlanner | `ScenarioRequirementPlanner.cs` | IA: condições e suposições → requisitos de dados ("Veículo existente com Disponivel = true") |
| DataAcquisitionPlanner | `DataAcquisitionPlanner.cs` | IA escolhe os endpoints (prioritariamente GET) em rodadas curtas; os dados vão para o contexto com a execução de onde vieram e só contam como **verificados** se aparecem na resposta |
| ScenarioContext | `ScenarioContext.cs` | Dados reais reutilizados entre cenários + fatos do estado (válidos até a próxima escrita bem-sucedida) |
| RuntimePayloadMaterializer | `RuntimePayloadMaterializer.cs` + `Scenarios/ScenarioModel.cs` | IA liga as variáveis aos dados reais; o **mesmo solver/Z3 e a mesma simulação** da geração estática conferem os valores e montam rota, query, headers e body. Valores que contradizem o cenário, ou que mudam o resultado simulado, são recusados antes da request |
| ScenarioExecutor | `ScenarioExecutor.cs` | Executa e registra status, corpo, exceção, tempo e payload. Bloqueia escrita com `Runtime:AllowWrites = false` e requisições além do limite |
| ScenarioExplorer | `ScenarioExplorer.cs` | Confirma o caminho feliz (baseline) e, a partir dele, muda só o necessário em cada cenário. Enquanto um cenário não for **confirmado** nem provado **inalcançável**, a IA vê a resposta e propõe outro payload, alterando só as propriedades que o resultado observado aponta (os bindings são incrementais: `bindings` muda variáveis, `unbind` devolve ao baseline). "Desistir" só é aceito como inalcançável com prova (senão a variação proposta é executada e o cenário continua) ou como não materializado para cenário nunca executado |
| MatrixReconciler | `MatrixReconciler.cs` + `OutcomeMatcher.cs` | Classifica: **confirmado**, **descoberto em runtime**, **não materializado**, **inalcançável** e, só quando um limite interrompe a exploração, **inconclusivo** (a validação fica `parcial`, com os cenários na nota) |

Regras contra falso positivo:
- **Confirmado** só quando o status confere **e** algo identifica a regra: a mensagem esperada na resposta, o campo nos erros de validação (mensagem padrão do framework) ou um status que nenhuma outra regra do endpoint produz. A IA nunca confirma um cenário.
- **Confirmado por isolamento** quando o status confere mas a resposta não identifica a regra (mensagem ausente ou status compartilhado): o payload difere do baseline confirmado só nos campos da condição do cenário, todo o estado lido está comprovado e nenhuma outra regra com o mesmo status depende desses campos.
- Uma execução que falhou não remove o cenário. **Inalcançável** (fora da matriz final, listado com a evidência) exige a justificativa da IA, 2+ execuções com **payloads diferentes** aceitos pelo solver, todo o estado lido comprovado por dados reais e o mesmo resultado, previsto por outro cenário. Enquanto a prova não basta, o que falta volta para a IA na rodada seguinte.
- Limites de segurança (`MaxRequests`, `MaxExplorationRounds`, `MaxAttemptsPerScenario`) e uma rodada em que a IA não propõe nada novo encerram a exploração; só nesse caso sobra **inconclusivo**.
- Resultados que nenhum cenário prevê viram **descoberto em runtime** (RT-01...).

Na SampleApi, por exemplo, os validators do FluentValidation não rodam automaticamente (falta `AddFluentValidationAutoValidation`): os cenários estáticos "data padrão → 400" e "veiculoId = 0 → 400" são retirados como inalcançáveis, com as execuções que mostram o 422/404 das regras do service.

Configuração (`Runtime` no appsettings): `Enabled`, `Project` (.csproj do serviço, quando não é o projeto do controller), `AllowWrites` (padrão `true`), `Headers` (ex.: `Authorization` do usuário de testes), `MaxRequests`, `MaxAcquisitionRounds`, `MaxAttemptsPerScenario`, `MaxExplorationRounds`, `MaxScenarios`. Na API, `"validateRuntime": false` desliga por requisição; na CLI, `--no-runtime`.

**Token da API:** o campo no card do endpoint, abaixo de "Validar os cenários com a API em execução", recebe o token do serviço analisado (com ou sem `Bearer `). Ao informar (Enter ou sair do campo), o analisador sobe o serviço do endpoint, do mesmo jeito que na validação em runtime (mesmo projeto, `dotnet build` e executável), e chama um GET que exige autenticação sem e com o token: ✓ quando o token é aceito, ✗ quando o serviço responde 401/403. Na análise, o token vai no header `Authorization` de todas as requisições da validação em runtime (não aparece no relatório nem nos prompts da IA). Na API, `"apiToken"` no corpo de `/api/analysis`; na CLI, `--token <token>`.

## Reanálise manual de cenários

Cada cenário inconclusivo ou não materializado na matriz validada tem um botão **Reanalisar**. O editor abre o último payload, a expectativa original, a resposta e os motivos pelos quais o cenário não foi confirmado. Uma tentativa que não confirma deixa o cenário inconclusivo, pois a requisição já foi executada. Uma tentativa executa somente o cenário selecionado, sem IA, aquisição de dados ou exploração automática. O resultado atualiza a matriz, contadores, relatório Markdown e análise salva; o histórico de payloads, respostas e evidências aparece nos detalhes do cenário.

O fluxo é: **payload informado → subir API → enviar requisição → comparar resposta**. O servidor envia o body informado sem completar campos, ajustar tipos ou exigir comprovação prévia das condições do cenário. A resposta observada é comparada com a expectativa original; HTTP compatível sem evidência suficiente mantém o cenário inconclusivo. Baselines e fatos antigos não são usados para confirmar por isolamento.

Contrato: `POST /api/analysis/scenarios/revalidate`, com `{ "analysisId": "id retornado em report.analysisId", "scenarioId": "CEN-03", "body": { ... } }`. Método, URL, headers e expectativa vêm da sessão do servidor. A resposta contém `analysisId`, `runtime`, `execution` e `markdown`. Use `body: null` para uma requisição sem corpo.

As análises são persistidas no SQLite, incluindo a matriz original, expectativas, respostas e histórico. Reiniciar o servidor ou expirar o contexto em memória não exige nova análise nem chamadas à IA. A reanálise carrega o projeto atual, executa o cenário salvo contra o código corrigido e registra `sourceFingerprint` e `testedCommit` em cada tentativa; a versão original do relatório permanece intacta. Alterações durante a inicialização da API pedem apenas repetir a tentativa após concluir a edição.

O token da API é armazenado criptografado com ASP.NET Core Data Protection e recuperado após reiniciar o analisador. No Windows, as chaves são protegidas por DPAPI do usuário atual. O token não aparece nos relatórios nem no localStorage das análises. Ele é reutilizado enquanto a API o aceitar; se expirar, informe um novo token no campo existente e a próxima tentativa atualiza a credencial salva. Perder as chaves de proteção exige reinformar o token, mas não refazer a análise. Há um lock por análise e orçamento manual independente da exploração automática. POST/PUT/PATCH/DELETE respeitam `Runtime:AllowWrites`.

O banco padrão é `%LOCALAPPDATA%/EndpointAnalyzer/analyses.db` (personalizável por `Runtime:AnalysisDatabasePath`); as chaves ficam na pasta `keys` ao lado dele. As migrações são automáticas (`PRAGMA user_version=2`); a da versão 1 para a 2 preserva relatórios, sessões e execuções, numera as análises existentes pela data de criação e liga cada uma à anterior do mesmo endpoint.

| Tabela | Conteúdo |
|---|---|
| `analyses` | ID permanente, caminho da solução, método/rota/projeto, versão original, relatório JSON com cenários e datas, ordem de criação (`sequence`) e versão anterior do endpoint (`previous_analysis_id`) |
| `analysis_sessions` | Referência à análise, token protegido, contador de chamadas manuais e último uso |
| `manual_executions` | Histórico por análise/execução: cenário, data, versão testada e JSON da tentativa |

O relatório completo é mantido como snapshot JSON, e o histórico manual também é indexado em tabela própria. Snapshot, histórico e contador são salvos em uma transação. `GET /api/analysis/saved` recupera a análise mais recente (pela ordem de criação, não pela última revalidação) por solução/método/rota/projeto. A interface usa o localStorage como cache e importa automaticamente relatórios antigos através de `POST /api/analysis/restore`, sem IA. O JSON de importação precisa conter o relatório completo (`context.scenarios` e `runtime.matrix`); o download da matriz runtime isolada não contém todo esse contexto.

Configurações em `Runtime`: `AnalysisDatabasePath`, `AnalysisSessionMinutes` (30, retenção apenas em memória), `MaxAnalysisSessions` (20 contextos em memória), `MaxManualRequests` (30 por análise, persistidos), `MaxManualAttemptsPerScenario` (10) e `MaxManualBodyChars` (100000). Erros controlados: JSON inválido (400), análise/cenário/endpoint inexistente (404), alteração durante inicialização ou tentativa concorrente (409), escrita/rota bloqueada (403), limites (413/429) e API indisponível (503).

## Diff entre análises

Cada clique em ANALISAR cria uma nova versão no SQLite, ligada à anterior do mesmo solução + método + rota + projeto; nenhuma versão é sobrescrita, e revalidar uma análise antiga não a torna a mais recente nem muda a ligação. A aba **Diff** do resultado compara a análise exibida com a anterior a partir dos relatórios estruturados já salvos: sem IA, sem executar o endpoint e sem gravar o resultado (é calculado sob demanda e funciona depois de reiniciar o aplicativo).

- **Resumo:** validações e regras (da matriz estruturada; sem ela nas duas versões, das condições do código), efeitos e entidades alteradas, campo a campo (expressão, limite, mensagem, operação, propriedade). A documentação da IA (objetivo, títulos, contexto, mensagens) fica num grupo próprio, separada das mudanças de comportamento.
- **Cenários:** matriz estática com matriz estática e, quando as duas têm, matriz validada em runtime com matriz validada em runtime (status, HTTP observado, esperado × observado). Confirmado → inconclusivo é um alerta, não prova de regressão; inalcançável é um status, não um cenário excluído do código.
- **Correspondência semântica:** os cenários são pareados pelo comportamento (tipo, foco, regra, mensagens), nunca pelo id: CEN-xx, Dx e Cx são renumerados a cada análise. Ordem, formatação e número de linha são ignorados; mudanças só em dados de teste (valores do payload, datas relativas a hoje), origem no código ou ambiente ficam fora dos totais e aparecem sob demanda. Correspondências ambíguas (ou só pela origem no código) são marcadas como **incertas**, nunca como inclusão/remoção.
- **Abas ausentes:** se uma aba (ou a IA, ou o runtime) não foi gerada numa das versões, ela aparece como **não comparável**, nunca como removida.

Contrato: `GET /api/analysis/diff?analysisId={atual}&baseId={opcional}`. Sem `baseId`, a base é a versão anterior salva; com ele, compara com qualquer versão histórica do mesmo endpoint. A resposta traz as versões comparadas (datas, commits, analisador, IA), `counts` (adicionados, removidos, alterados, incertos, não comparáveis) e, por aba e grupo, os itens com `kind`, `impact` (comportamento, runtime, documentação, dados de teste, origem, ambiente), identificador semântico e campos com antes/depois. Credenciais, headers de autorização e campos sensíveis do payload são mascarados. Erros: análise ou base inexistente (404), base igual à atual ou de outro endpoint (400).

## Fica para depois (seção 42 da especificação)

Minimal APIs (`MapPost`), mensageria entre serviços (filas e consumidores em outros processos), continuidade automática de uma análise entre Claude e Codex, persistência em PostgreSQL e captura das alterações em runtime (`SaveChangesInterceptor`).

## Testes

```powershell
dotnet test
node --test tests/frontend/manual-revalidation.test.cjs
node --test tests/frontend/ai-provider.test.cjs
node --test tests/frontend/analysis-queue.test.cjs
```
