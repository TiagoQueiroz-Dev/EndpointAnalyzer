# EndpointAnalyzer — Geração de cenários por árvore de execução

## 1. Objetivo

Substituir a geração centrada em *candidatos de teste* por uma geração centrada nos **desfechos alcançáveis do fluxo do endpoint**.

- **Raiz:** entrada HTTP (incluindo validação/model binding antes da action).
- **Ramos:** condições, chamadas a métodos, validações e decisões de estado.
- **Folhas:** encerramento por erro, retorno antecipado ou resposta final após os efeitos (inclusive persistência).
- **Cenário:** teste necessário para cobrir uma folha ou uma classe/limite relevante de entrada que a atinge.
- **Princípio:** não criar cenários extras apenas por trocar o payload. Aceitar mais de um teste da mesma validação **somente quando isso acrescentar cobertura justificável**.

> Persistir não encerra automaticamente o caminho: o fluxo deve continuar até `return`, `throw` ou término efetivo. Duas respostas HTTP iguais podem representar desfechos diferentes se exercitarem validações ou efeitos distintos.

## 2. Situação atual e componentes a preservar

| Arquivo | Situação / ação |
|---|---|
| `EndpointAnalyzer.Scanner/CallGraphBuilder.cs` | Reutilizar a cadeia de métodos do endpoint. |
| `EndpointAnalyzer.Scenarios/FlowModel.cs` | Reutilizar regras (`Rules`), saídas (`Exits`), efeitos (`Effects`) e ordem; expandir para representar transições explícitas. |
| `EndpointAnalyzer.Scenarios/SymbolicResolver.cs` | Reutilizar `PathOf`, condições simbólicas e propagação entre métodos; corrigir lacunas encontradas nos caminhos. |
| `EndpointAnalyzer.Scenarios/ValidationRules.cs` | Reutilizar extração de DataAnnotations/FluentValidation e `Probes()` como **fontes de classes de teste**, não como criadores diretos de cenários. |
| `EndpointAnalyzer.Scenarios/Solver.cs` | Reutilizar solver de domínios e Z3 para verificar restrições. |
| `EndpointAnalyzer.Scenarios/ScenarioGenerator.cs` | Refatorar `Execute()` para gerar cenários a partir das folhas cobertas. |
| `EndpointAnalyzer.Scenarios/ScenarioModel.cs` | Preservar as restrições e o `Entry` de cada cenário para materialização em runtime. |
| `EndpointAnalyzer.Runtime/ScenarioExplorer.cs` | Reutilizar execução/validação do esperado, ajustando a seleção dos cenários. |
| `EndpointAnalyzer.Application/AnalysisDiffService.cs` | Parear cenários por identidade estrutural e comparar os resultados esperados. |

Hoje `Execute()` agrega `Variants`, `EarlyExits`, `ValidationCandidates`, `DefaultValueCandidates`, `RuleCandidates` e `BoundaryCandidates`. A deduplicação usa `Signature(scenario)` com URL/payload/pré-condições/status/mensagens. **Esses não devem ser os critérios de identidade de um cenário.**

## 3. Estruturas a adicionar

Criar em `EndpointAnalyzer.Scenarios`:

```csharp
FlowTreeNode     // Id lógico, origem no código, tipo: entry/decision/validation/effect/terminal
FlowTreeEdge     // origem, destino, condição simbólica e sentido (true/false/case)
TerminalOutcome  // regra/retorno final, status, mensagens, exceção e efeitos
PathCandidate    // folha, sequência de decisões, restrições acumuladas e viabilidade
CoverageTarget   // obrigação: desfecho, classe de equivalência, limite ou ramo relevante
ScenarioKey      // identidade estável: alvo estrutural + classe necessária (quando houver)
```

A árvore é uma **visão lógica de execução**. Internamente, use CFG/grafo com junções e laços; não duplique blocos quando dois ramos convergirem para a mesma validação.

Armazenar, por cenário, `ScenarioKey`, `TerminalNodeKey`, `CoveredTargets` e evidências de origem, além dos campos já existentes. Manter `CEN-01`, `CEN-02` etc. somente como IDs de exibição.

## 4. Novo fluxo de geração

```text
Endpoint HTTP
   ↓
CallGraph + regras de validação + CFG interprocedural
   ↓
Árvore lógica de decisões, efeitos e saídas
   ↓
Identificação de folhas/desfechos distintos
   ↓
Restrições do caminho (guardas anteriores + condição-alvo)
   ↓
Solver: SAT / UNSAT / UNKNOWN
   ↓
Obrigação de cobertura por folha e por classe/limite necessário
   ↓
Selecionar conjunto mínimo justificável de cenários
   ↓
Montar payload, pré-condições e resultado esperado
   ↓
Matriz estática → validação runtime → histórico/diff
```

### Fase 1 — Construir o fluxo completo

1. Partir da action e percorrer os métodos de negócio resolvidos pelo `CallGraphBuilder` (incluindo handlers e serviços).
2. Modelar `if/else`, `switch`, expressões condicionais, `return`, `throw`, notificações, guards, exceções tratadas e estados consultados.
3. Incorporar validações de entrada executadas antes da action: model binding, DataAnnotations e FluentValidation **quando efetivamente registrado/executado**.
4. Registrar efeitos condicionais: insert/update/delete, `SaveChanges`, eventos e respostas; preservar a ordem de execução.
5. Tratar chamadas não resolvidas como **lacunas de análise**, nunca presumir seus efeitos ou considerá-las caminhos comprovados.
6. Para laços, adotar cobertura finita configurável (zero, uma e múltiplas iterações relevantes); não prometer enumerar infinitas execuções.

**Saída:** nós, transições, condições e folhas associadas a referências reais do código.

### Fase 2 — Identificar folhas distintas

Registrar uma folha para cada:

- Validação/regra que encerra a requisição, distinguindo regras pelo ponto semântico do código, não pela mensagem HTTP.
- Retorno antecipado que altera os efeitos ou o resultado final.
- Resposta final com conjunto de efeitos diferente (ex.: inseriu vs. não inseriu).
- Exceção/erro tratado relevante que modifica o resultado observado.

**Não** criar outra folha apenas porque duas sequências de entrada chegam à mesma regra sem mudar o desfecho nem acrescentar obrigação de cobertura.

**Atenção:** validações automáticas podem agregar mais de um erro na mesma resposta. Representar a validação individual e as combinações de erros apenas quando forem relevantes para uma obrigação de cobertura; não presumir sempre “primeiro erro interrompe”.

### Fase 3 — Determinar a viabilidade de cada folha

Para cada desfecho, compor:

```text
Restrições = condições do caminho
           + guardas anteriores que devem passar
           + condição que dispara a folha
           + restrições do estado do banco/contexto
```

Consultar o solver e guardar um resultado **trivalente**:

- `SAT`: existe combinação simbólica que satisfaz as restrições (não comprova que o banco real contém os dados).
- `UNSAT`: caminho provadamente contraditório, considerando a semântica suportada.
- `UNKNOWN`: condição opaca, chamadas não resolvidas, limite do solver ou falta de modelo; não classificar como inalcançável.

> Ajustar `ConstraintSolver`: o `null` atual não distingue satisfatibilidade impossível de erro/limite. Nunca converter falha de análise em prova de impossibilidade.

### Fase 4 — Gerar somente as variações necessárias

Definir **obrigações de cobertura** antes de materializar payloads:

1. Pelo menos um teste para cada folha viável identificada.
2. Classes de equivalência com semântica distinta (`null`, vazio, espaços, formato inválido, enum fora do domínio etc.), **quando aplicáveis**.
3. Limites que testem efetivamente lados distintos de uma regra (ex.: comprimento 6/7/8 para exigência de exatamente 7 caracteres).
4. Ramos ou estados que causem efeitos, decisões ou respostas diferentes.
5. Combinações de condições apenas quando necessárias para verificar interação ou um desfecho ainda não coberto.

**Critério para aceitar um novo cenário:** ele deve cobrir ao menos uma obrigação ainda descoberta, ou representar desfecho/semântica que os escolhidos não exercitam.

Exemplo: regra de placa com comprimento exato de 7:

| Caso | Por que existe? |
|---|---|
| Placa com 6 caracteres | Falha pelo limite inferior. |
| Placa com 8 caracteres | Falha pelo limite superior. |
| Placa com 7 caracteres | Confirma fronteira válida; pode ser coberta pelo caminho de sucesso. |
| `null` / vazio / espaços | Criar casos separados **somente se** exercitarem classes/mecanismos relevantes; caso contrário, selecionar um representante da classe. |

A identificação de uma obrigação **não implica** um novo teste: um cenário já escolhido pode cobrir várias obrigações. Aplicar seleção determinística (por exemplo, *greedy set cover*, com desempate estável), registrando o que cada caso cobre. Evitar gerar automaticamente um cenário por `Probe`.

### Fase 5 — Materializar e simular

- Usar as restrições selecionadas no `ScenarioModel.Entry` e produzir payload/precondições com os componentes existentes.
- Calcular o resultado esperado pela execução simbólica **do caminho selecionado**, incluindo regra, resposta e efeitos.
- Validar que o payload realmente atinge a folha-alvo; se atingir outra validação, não marcar como coberta.
- Manter cenário incompleto como `UNKNOWN`/pendente, com justificativa, em vez de inventar resultado esperado.
- Preservar um cenário de sucesso sem `Focus` para o baseline do `ScenarioExplorer` quando houver caminho de sucesso; não exigir baseline quando não existir.

### Fase 6 — Identidade, histórico e diff

`ScenarioKey` **não** pode depender do payload escolhido, da numeração `CEN-xx`, do texto da mensagem ou do status esperado. Propor:

```text
ScenarioKey = hash(
    endpoint + localização lógica da regra/desfecho
    + condição normalizada + tipo de desfecho
    + CoverageTarget específico, se a variação for necessária
)
```

Preferir símbolos/métodos e âncoras semânticas a números de linha; testar alterações que movem código sem mudar comportamento. Em múltiplas ocorrências equivalentes, adicionar discriminador estrutural determinístico.

Na aba **Diff**, parear pelo `ScenarioKey` (com fallback para matrizes antigas) e comparar **somente os campos de resultado esperado**: sucesso/erro, HTTP, mensagens, exceção, efeitos e persistência. Informar: cenário adicionado, removido ou resultado esperado alterado. Mudanças apenas no payload representativo não devem criar diferença funcional.

### Fase 7 — Runtime e cobertura

- Continuar usando `RuntimePayloadMaterializer`, `ScenarioExplorer`, `OutcomeMatcher` e `MatrixReconciler`.
- Executar um caso por obrigação selecionada, reutilizando dados/GETs/baseline quando possível; IA apenas para impasses não resolvidos deterministicamente.
- A matriz tem **uma linha por folha** (no mínimo): a folha sem entrada que a alcance (`UNSAT`, `UNKNOWN` não gerada) entra sem payload, com o motivo, e não é executada em runtime; a equivalente a outra entra com o payload que a atinge e a referência ao outro cenário. Não há seção separada de cobertura.
- `MaxScenarios` corta só variações (classes, limites, ramos, laços), nunca folhas; a nota da matriz informa quantas variações ficaram de fora. `MaxRequests` e os demais limites de runtime continuam explícitos na validação.
- `SAT` estático e `CONFIRMADO` em runtime são resultados diferentes. Não classificar ausência de dados reais como `UNSAT`.

## 5. Alterações diretas no código

1. **Adicionar** `FlowTreeBuilder.cs` e modelos da árvore, aproveitando `FlowModel`/`SymbolicResolver`.
2. **Adicionar** `TerminalOutcomeCollector.cs` para identificar folhas e registrar suas fontes.
3. **Adicionar** `PathFeasibilityService.cs` com `SAT/UNSAT/UNKNOWN`, adaptando `ConstraintSolver`.
4. **Adicionar** `CoveragePlanner.cs` para obrigações, partições, limites e seleção mínima de casos.
5. **Refatorar** `ScenarioGenerator.Run.Execute()` para: árvore → folhas → viabilidade → cobertura → cenários. `Variants()`, `ValidationCandidates()` e `BoundaryCandidates()` deixam de gerar cenários diretamente.
6. **Estender** `ScenarioMatrix`/`Scenario` com identidade estrutural, cobertura e diagnósticos, preservando os campos existentes.
7. **Ajustar** `ScenarioModel` e runtime para materializar os casos selecionados, sem romper baseline ou revalidação manual.
8. **Ajustar** `AnalysisDiffService`/`DiffMatcher` para pareamento estável por identidade e comparação de resultado esperado.
9. **Atualizar** persistência SQLite/serialização de análises com compatibilidade para arquivos históricos sem `ScenarioKey`.

## 6. Testes de aceitação obrigatórios

- [ ] A mesma validação, alcançada por dois ramos equivalentes, **não** gera cenários duplicados sem nova obrigação de cobertura.
- [ ] Validações diferentes que retornam **mesmo HTTP/mensagem** permanecem distintas.
- [ ] `Required`, comprimentos, faixas e formato geram apenas representantes necessários, com evidência da classe/limite coberto.
- [ ] Toda folha `SAT` recebe pelo menos um cenário; `UNSAT` é explicado; `UNKNOWN` é exibido como pendência.
- [ ] Guardas anteriores passam antes de alcançar a validação-alvo; payload não desvia para outra regra.
- [ ] Retorno antecipado sem escrita e sucesso com escrita permanecem separados mesmo com HTTP igual.
- [ ] Persistência seguida de outra validação/erro continua sendo percorrida.
- [ ] `try/catch`, notificação delegada e validação automática produzem desfechos corretos.
- [ ] Laços são cobertos dentro dos limites definidos, sem alegação de exaustividade.
- [ ] Mesma solução/branch/código/configuração gera a mesma árvore, `ScenarioKey` e cobertura (desconsiderando dados de execução voláteis).
- [ ] Alteração apenas do resultado esperado mantém o cenário correspondente e aparece como **alterado** no diff.
- [ ] Limites atingidos mostram o que ficou sem cobertura, sem apagar silenciosamente cenários.
- [ ] Runtime, histórico, revalidação manual e matrizes antigas continuam funcionando.

## 7. Critério de conclusão

A geração estará concluída quando **cada desfecho viável identificado possuir cobertura demonstrável**, cada variação adicional tiver uma obrigação explícita e não houver cenários duplicados apenas por payload. Caminhos `UNKNOWN` e lacunas deverão permanecer visíveis.

**Não afirmar cobertura de “100% de todas as execuções possíveis”**: laços, estados externos, concorrência, chamadas não resolvidas e combinações ilimitadas impedem essa garantia geral. A métrica correta é **cobertura dos desfechos e obrigações identificados pelo modelo**, acompanhada de suas limitações.

## Fontes

- [Código atual — ScenarioGenerator.cs](https://github.com/TiagoQueiroz-Dev/EndpointAnalyzer/blob/main/src/EndpointAnalyzer.Scenarios/ScenarioGenerator.cs)
- [Código atual — FlowModel.cs](https://github.com/TiagoQueiroz-Dev/EndpointAnalyzer/blob/main/src/EndpointAnalyzer.Scenarios/FlowModel.cs)
- [Código atual — ValidationRules.cs](https://github.com/TiagoQueiroz-Dev/EndpointAnalyzer/blob/main/src/EndpointAnalyzer.Scenarios/ValidationRules.cs)
- [Código atual — Solver.cs](https://github.com/TiagoQueiroz-Dev/EndpointAnalyzer/blob/main/src/EndpointAnalyzer.Scenarios/Solver.cs)
- [Código atual — AnalysisDiffService.cs](https://github.com/TiagoQueiroz-Dev/EndpointAnalyzer/blob/main/src/EndpointAnalyzer.Application/AnalysisDiffService.cs)
- [ISTQB CTFL v4.0.1, capítulo 4: equivalência, limites, tabelas de decisão e cobertura estrutural](https://istqb.org/wp-content/uploads/2024/11/ISTQB_CTFL_Syllabus_v4.0.1.pdf)

> Documento de implementação proposto a partir da branch `main` consultada em 13/10/2026. Nenhuma alteração foi aplicada ao repositório.
