# EndpointAnalyzer — Diff enxuta e objetiva

## Objetivo

Simplificar a **aba Diff** para responder rapidamente: **o que mudou, como era antes e como ficou agora?** Exibir os detalhes técnicos somente quando solicitados, sem perder rastreabilidade nem alterar o resultado da comparação semântica.

## Referências adotadas

- **VS Code:** concentra a atenção nas alterações e permite recolher regiões inalteradas. https://code.visualstudio.com/docs/sourcecontrol/staging-commits
- **GitHub Pull Requests:** filtros e visualizações de diferenças adequadas a revisões. https://docs.github.com/en/pull-requests/how-tos/review-pull-requests/reviewing-proposed-changes-in-a-pull-request
- **oasdiff:** distingue alterações relevantes da API do diff completo, incluindo mudanças somente documentais. https://www.oasdiff.com/docs/getting-started

## Estado atual verificado na branch `main`

- `src/EndpointAnalyzer.Application/AnalysisDiffService.cs`: produz grupos, itens, campos `before/after`, classificação e contagens.
- `src/EndpointAnalyzer.Application/AnalysisDiffMatching.cs`: relaciona itens semanticamente; mantém correspondências incertas.
- `src/EndpointAnalyzer.Core/Models/AnalysisDiff.cs`: DTOs `AnalysisDiffResult`, `AnalysisDiffItem`, `AnalysisDiffField` e tipos de impacto.
- `src/EndpointAnalyzer.Api/wwwroot/index.html`: funções `renderDiff`, `diffGroupHtml`, `diffItemHtml`, `diffFieldHtml` e estilos `.diff-*`.
- `tests/frontend/analysis-diff.test.cjs` e `tests/EndpointAnalyzer.Tests/AnalysisDiffTests.cs`: testes existentes da comparação e da renderização.

**Preservar:** análises no SQLite, endpoint `GET /api/analysis/diff?analysisId=...`, seleção de versão-base, detecção por `DiffMatcher`, classificação `incerto`, campos e snapshots. A aba continua **sem executar a API e sem chamar IA**.

## Layout esperado

```text
Diff da análise
Anterior: abc1234 → Atual: def5678                 [Detalhes das versões ▾]

     +1 Adicionado      -1 Removido       ~2 Alterados      [1 incerto, se houver]

[Resumo] [Cenários]               [Todas] [Adicionadas] [Removidas] [Alteradas] [Incertas]

Cenários afetados
┌──────────────────────────────────────────────────────────────┐
│ ALTERADO  Observação com 550 caracteres                      │
│ O cenário passou a aceitar observações de até 600 caracteres│
│                                                              │
│ ANTES: HTTP 400 (esperado) → DEPOIS: HTTP 200 (esperado)     │
│ [Ver alterações do cenário ▾]                                │
└──────────────────────────────────────────────────────────────┘

[Exibir alterações de documentação e dados de teste]
```

Os exemplos são ilustrativos: **não afirmar que houve HTTP observado ou regressão sem evidência da matriz runtime**.

## Regras de exibição

1. **Cabeçalho compacto:** exibir commits (ou datas, se não houver commit) e os totais de **adicionados, removidos e alterados**. Exibir **incertos** separadamente, sem tratá-los como mudanças confirmadas. Versão do analisador, modelo da IA, branch, abas e notas vão para **Detalhes das versões**.
2. **Navegação:** manter `Resumo` e `Cenários`; filtros de tipo em uma única barra, com contagens apenas no cabeçalho. Evitar repetir números por grupo e mensagens como “N iguais”.
3. **Grupos:** renderizar apenas grupos com itens visíveis. Se não houver diferenças para o filtro, mostrar **uma única mensagem**: “Nenhuma alteração neste filtro.”
4. **Card de item:** uma linha com **tipo + título**, no máximo uma frase contextual e uma comparação **Antes → Depois** do campo mais relevante. Remover da visão inicial os IDs `CEN-*`/`D*`, lista de campos, notas extensas e JSONs.
5. **Alterado:** mostrar a alteração mais importante (ex.: `HTTP esperado: 400 → 200`, `limite máximo: 500 → 600`, `runtime: confirmado → inconclusivo`). Não inventar frases ou valores ausentes.
6. **Adicionado/removido:** apresentar um resumo do **novo cenário/regra** ou do item que deixou de existir, em vez de imprimir todos os campos com um lado vazio.
7. **Expansão:** `Ver alterações` abre apenas os **campos diferentes** em tabela `Campo | Antes | Depois`. Um segundo `<details>` chamado **Estrutura completa** mostra os snapshots, origem, IDs, metadados e notas. Preferir `<details>/<summary>` nativos, com acessibilidade por teclado.
8. **Ruído:** dados de teste, origem, ambiente e documentação da IA ficam ocultos por padrão e disponíveis em **Alterações secundárias**. Mudanças de payload **estrutural** ou regra de negócio não são secundárias.
9. **Runtime:** mudanças de status e de HTTP **observado** devem ser descritas como observações da execução. `confirmado → inconclusivo` merece alerta, **não** o rótulo automático de regressão. Se uma versão não tiver runtime, exibir **Não comparável**, jamais “Removido”.
10. **Correspondência incerta:** continuar mostrando `Incerto` com a explicação “Não foi possível associar os itens com segurança”, sem converter ambiguidades em inclusões/remoções.
11. **Agrupamento (evolução adicional):** quando uma regra do Resumo causar alterações em vários cenários, oferecer um bloco consolidado com links para os cenários afetados; agrupar **somente** quando existir vínculo semântico confiável, sem descartar itens ou mascarar diferenças nos totais.

## Exemplo — cenário alterado

**Card fechado:**

```text
[ALTERADO] Observação com 550 caracteres
O limite máximo da observação passou de 500 para 600 caracteres.
ANTES: HTTP 400 esperado  →  DEPOIS: HTTP 200 esperado
[Ver alterações do cenário ▾]
```

**Card expandido:**

| Campo | Antes | Depois |
|---|---|---|
| Limite máximo | 500 | 600 |
| Resultado esperado | Falha | Sucesso |
| HTTP esperado | 400 | 200 |
| Identificador | CEN-03 | CEN-05 |
| Payload | 550 caracteres | 550 caracteres |

**Importante:** IDs e payload inalterado devem ficar no segundo nível (**Estrutura completa**); na primeira expansão mostrar apenas os campos efetivamente modificados. Se existir **HTTP observado** nas duas matrizes runtime, mostrá-lo em linha distinta do esperado.

## Plano de implementação

### 1. Interface (principal mudança)

Arquivo: `src/EndpointAnalyzer.Api/wwwroot/index.html`.

- **`renderDiff()`**: substituir cabeçalho extenso por versão compacta, cards de contadores e uma barra de filtros. Excluir grupos vazios após aplicar o filtro. Mover avisos e metadados para expansão contextual.
- **`diffGroupHtml()`**: título simples + lista de cards. Não repetir contagens nem renderizar mensagens de vazio por grupo.
- **`diffItemHtml()`**: compor `tipo + título + resumo antes/depois + <details>`. Manter as funções de escape (`esc`) para qualquer conteúdo oriundo do relatório.
- **`diffFieldHtml()` / `diffValueHtml()`**: reutilizá-las **somente dentro da expansão**, exibindo apenas campos diferentes. A comparação principal deve ser uma linha compacta calculada a partir de `item.fields`.
- **CSS `.diff-*`**: cards mais baixos, tipografia hierarquizada, bom contraste em tema claro/escuro e layout responsivo sem colunas obrigatórias para telas estreitas.
- **Seleção do destaque:** implementar helper puro `getDiffHighlight(item)` que priorize: (a) alertas runtime; (b) HTTP/resultado esperado; (c) condição/regra/validação/limite; (d) efeitos/persistência; (e) primeiro campo de comportamento. Se não houver valor seguro, exibir apenas título e tipo, sem gerar um resumo especulativo.

### 2. Separar mudanças reais das secundárias

Arquivos: `AnalysisDiff.cs`, `AnalysisDiffService.cs` e `index.html`, **se necessário**.

- Hoje `DiffImpacts.IsPrimary()` inclui `Documentation`: isso faz alterações somente textuais da IA entrarem no total principal.
- Criar na **camada de apresentação** totais derivados por categoria, sem alterar imediatamente os totais existentes na API: **comportamento**, **runtime**, **documentação**, **secundárias** e **incertas**. Documentação não deve inflar a contagem **de comportamento**.
- Manter consistência: os números exibidos devem refletir os filtros ativos e não devem confundir **número de itens** com **número de campos alterados**.
- Se futuramente alterar a semântica de `counts.total`/`hasDifferences`, versionar o contrato e ajustar todos os consumidores e testes; não mudar silenciosamente.

### 3. Não alterar o matching sem motivo

Arquivo: `src/EndpointAnalyzer.Application/AnalysisDiffMatching.cs`.

Manter o algoritmo atual inicialmente. Melhorias de agrupamento ou redução de falsos positivos ficam em etapa separada, com testes que provem associação estável entre versões. **Não esconder alterações reais apenas para deixar a tela menor.**

### 4. Testes de aceitação

Atualizar `tests/frontend/analysis-diff.test.cjs` e, quando houver mudança de contrato, `tests/EndpointAnalyzer.Tests/AnalysisDiffTests.cs`.

- `alterado`: card fechado mostra somente a mudança prioritária; expandido mostra exclusivamente os campos diferentes; estrutura completa disponível.
- `adicionado`/`removido`: nenhuma coluna vazia nem dump integral por padrão.
- Alteração apenas de título/texto da IA: não aparece como mudança de comportamento; pode ser consultada no filtro secundário.
- Mudança somente de data ou ID de teste: oculta por padrão; permanece acessível.
- Comparação incerta: visível no filtro correspondente e jamais confirmada implicitamente.
- Runtime ausente, inconclusivo ou não comparável: não gera falsa remoção, sucesso ou regressão.
- Lista sem mudanças e filtro vazio: uma única mensagem, sem grupos ou contagens repetidas.
- Strings de títulos e payloads escapadas contra HTML; expansão via teclado; responsividade testada.
- Preservar testes de carregamento assíncrono, seleção de versão-base e renderização do histórico.

## Critérios de conclusão

- A pessoa consegue identificar **o que mudou** sem abrir detalhes.
- O card fechado ocupa poucas linhas e não repete campos técnicos.
- A expansão mostra **somente diferenças**; a estrutura completa continua acessível.
- Mudanças de documentação/ambiente não são confundidas com mudanças de comportamento.
- Nenhuma perda de dados, alteração indevida de classificação, execução extra de runtime ou chamada adicional à IA.

**Escopo recomendado:** implementar primeiro apenas a apresentação compacta e seus testes; tratar agrupamento semântico e eventuais mudanças de contrato como melhorias separadas.
