# Análise com IA + Validação Dinâmica de Cenários

## Conceito

O EndpointAnalyzer continua funcionando em dois modos.

### Sem IA

Mantém o comportamento atual:

```text
Roslyn
→ análise estática
→ regras e condições
→ ScenarioGenerator
→ matriz de cenários candidata
```

Nenhum projeto é iniciado e nenhuma request real é executada.

### Com IA

A análise estática continua sendo a base, mas a IA passa a usar o sistema em execução para transformar a matriz candidata em uma matriz validada com dados reais.

```text
Análise estática
→ matriz candidata
→ IA analisa condições/suposições
→ busca dados reais pelos endpoints disponíveis
→ monta contexto reutilizável
→ gera payload válido
→ executa o endpoint
→ explora os demais cenários a partir do contexto conhecido
→ reconcilia matriz estática × runtime
→ matriz final validada
```

A ideia não é tentar valores aleatórios desde o início. Primeiro a IA entende quais dados precisa e busca esses dados no próprio sistema.

---

# O que já existe

O projeto já possui:

- Roslyn e Call Graph;
- remoção de ruído;
- `SymbolicResolver`;
- `ScenarioGenerator`;
- `PayloadBuilder`;
- `ConstraintSolver`;
- Z3 para restrições complexas;
- matriz de cenários estática;
- integração com IA.

Esses componentes continuam sendo utilizados.

---

# Novos componentes

## 1. AppRunner

**Tecnologias**

- .NET
- `System.Diagnostics.Process`
- `HttpClient`
- opcional: `WebApplicationFactory/TestServer`

**Função**

Subir o projeto analisado quando `useAi = true`, aguardar a API ficar disponível e encerrá-la ao terminar.

Preferência: executar o projeto da forma mais próxima possível do ambiente real já configurado.

---

## 2. EndpointCatalog

**Base**

Reutilizar o `EndpointScanner` existente.

**Função**

Disponibilizar para a IA os endpoints, parâmetros, DTOs e retornos conhecidos.

---

## 3. ScenarioRequirementPlanner

**Tecnologia**

- IA já configurada no projeto

**Função**

Converter condições e suposições do cenário em requisitos de dados.

Exemplo:

```text
ProgramacaoTransporte deve existir
PrimeiroPedidoId deve ser null
UnidadeFabril deve existir
RamoAtividade deve ser Planta
Endereco deve existir
```

---

## 4. DataAcquisitionPlanner

**Tecnologias**

- IA
- `EndpointCatalog`

**Função**

Descobrir quais endpoints de leitura devem ser chamados para obter os dados necessários.

Exemplo:

```text
Preciso de UnidadeFabril com RamoAtividade = Planta
↓
usar GET /unidades
```

Não fazer tentativa aleatória de IDs.

---

## 5. ScenarioContext

**Tecnologia**

- objeto/cache em memória

**Função**

Guardar os dados reais encontrados e reutilizá-los entre cenários.

Exemplo:

```json
{
  "programacaoValida": {
    "id": "...",
    "primeiroPedidoId": null
  },
  "unidadePlanta": {
    "id": 37
  },
  "enderecoValido": {
    "id": 921
  }
}
```

Não é necessário banco próprio do EndpointAnalyzer.

---

## 6. RuntimePayloadMaterializer

**Base**

Reutilizar:

- `PayloadBuilder`;
- `ConstraintSolver`;
- `SymbolicResolver`;
- Z3.

**Função**

Substituir condições abstratas pelos valores reais encontrados no `ScenarioContext`.

Resultado:

```text
payload executável
+
route
+
query
+
headers
```

---

## 7. ScenarioExecutor

**Tecnologia**

- `HttpClient`

**Função**

Executar o endpoint alvo e registrar:

- status HTTP;
- response body;
- exceção retornada;
- tempo;
- payload utilizado.

---

## 8. ScenarioExplorer

**Função**

Depois que um payload válido for confirmado, usá-lo como baseline.

Para cada cenário seguinte:

```text
payload válido confirmado
→ altera somente o necessário para atingir o cenário
→ executa
→ analisa resultado
```

A partir desta fase pode existir tentativa e erro, pois o sistema já possui contexto real da base e um payload funcional.

Devem existir limites de requests e de tentativas por cenário.

---

## 9. MatrixReconciler

**Função**

Comparar:

```text
matriz estática
×
execuções reais
```

Classificar os cenários:

```text
CONFIRMADO
→ comportamento reproduzido em runtime

DESCOBERTO EM RUNTIME
→ cenário não previsto pela análise estática

NÃO MATERIALIZADO
→ cenário parece válido, mas não foi possível obter o estado necessário

INCONCLUSIVO
→ ainda existem condições não resolvidas

INALCANÇÁVEL
→ código/runtime demonstram que o cenário não pode ocorrer
```

Somente remover um cenário quando houver evidência suficiente de que ele é realmente inalcançável.

---

# Fluxo de implementação

## Fase 1 — manter o modo atual

Não alterar `BuildContextAsync`.

```text
useAi = false
→ comportamento atual
```

---

## Fase 2 — iniciar runtime apenas com IA

Dentro de:

```csharp
AnalyzeAsync(..., useAi: true)
```

após gerar o contexto estático:

```text
AppRunner.Start
→ aguardar API
```

---

## Fase 3 — planejar requisitos

Enviar para a IA:

- cenário candidato;
- condições;
- suposições;
- código relevante;
- catálogo de endpoints.

A IA retorna um plano estruturado de dados necessários.

---

## Fase 4 — adquirir contexto

Executar prioritariamente endpoints `GET`.

```text
DataAcquisitionPlanner
→ HttpClient
→ ScenarioContext
```

Os dados encontrados devem ser reutilizados por todos os cenários seguintes.

---

## Fase 5 — confirmar baseline

Usar o contexto adquirido para montar o primeiro payload válido.

```text
ScenarioContext
+
PayloadBuilder
+
ConstraintSolver
↓
payload válido
```

Executar o endpoint alvo e confirmar o caminho feliz.

---

## Fase 6 — validar a matriz

Partir do baseline confirmado.

Para cada cenário:

```text
alterar apenas as variáveis necessárias
→ executar
→ comparar resultado esperado × observado
```

Usar tentativas adicionais somente quando o primeiro payload não atingir o comportamento esperado.

---

## Fase 7 — reconciliar

Executar o `MatrixReconciler`.

A matriz exibida na análise com IA passa a ser a matriz validada.

A análise sem IA continua exibindo a matriz estática atual.

---

# Pipeline final

```text
                    SEM IA

Roslyn
↓
ScenarioGenerator
↓
Matriz estática


                    COM IA

Roslyn
↓
ScenarioGenerator
↓
Matriz candidata
↓
AppRunner
↓
ScenarioRequirementPlanner
↓
DataAcquisitionPlanner
↓
GETs direcionados
↓
ScenarioContext
↓
RuntimePayloadMaterializer
↓
Payload baseline válido
↓
ScenarioExecutor
↓
ScenarioExplorer
↓
MatrixReconciler
↓
Matriz validada
```

---

# Tecnologias

```text
.NET 10
Roslyn
System.Diagnostics.Process
HttpClient
Claude/Codex via IAiProvider
Microsoft.Z3
WebApplicationFactory/TestServer (opcional)
```

---

# Regras importantes

- Runtime somente quando `useAi = true`.
- Sem IA, manter exatamente a geração atual.
- Buscar dados antes de tentar valores.
- Reutilizar dados encontrados entre cenários.
- Não fazer brute force de IDs.
- Alterar o mínimo possível entre um cenário e outro.
- Limitar requests e tentativas.
- Preferir GET para aquisição de contexto.
- Executar POST/PUT/PATCH/DELETE somente no ambiente configurado para análise.
- Não remover cenário apenas porque uma execução falhou.
- Registrar payload e resultado real usados para confirmar cada cenário.

---

# Referências técnicas

- ASP.NET Core `WebApplicationFactory` / `TestServer` para execução controlada do sistema em testes de integração.
- Microsoft Research Pex como referência conceitual para combinar análise do programa, execução real e geração direcionada de entradas.
