# Remoção de Ruído na Análise Estática de Endpoints

## 1. Objetivo

Este documento descreve como reduzir o ruído gerado durante a análise estática de endpoints em projetos .NET.

O problema identificado é que o analisador está expandindo abstrações demais e seguindo caminhos que são tecnicamente alcançáveis, mas não representam o comportamento real do endpoint.

Isso gera:

- Call Graphs excessivamente grandes;
- serviços e repositórios irrelevantes;
- entidades que não pertencem ao fluxo real;
- condições duplicadas ou acumuladas demais;
- perda do tipo concreto em abstrações genéricas;
- mistura entre infraestrutura e regra de negócio;
- documentação final difícil de interpretar.

A mudança conceitual principal deve ser:

```text
NÃO:
"seguir tudo que pode ser alcançado"

MAS:
"seguir somente o que influencia o comportamento do endpoint"
```

---

## 2. Tornar a análise orientada ao endpoint

O fluxo desejado é:

```text
Endpoint
    ↓
Receiver concreto
    ↓
Implementação real
    ↓
Fluxo executável
    ↓
Regras relevantes
    ↓
Entidades afetadas
    ↓
Efeitos relevantes
```

O analisador não deve transformar uma chamada abstrata em uma exploração geral do projeto.

---

## 3. Resolver interfaces pela Dependency Injection

### Problema

Ao encontrar:

```csharp
IVeiculoService
```

o analisador não deve buscar automaticamente todas as implementações possíveis.

### Solução

Analisar os registros de DI:

```csharp
services.AddScoped<IVeiculoService, VeiculoService>();
```

Criar um mapa:

```text
IVeiculoService
→ VeiculoService
```

O Call Graph deve seguir somente a implementação registrada naquele contexto.

### Registros a considerar

```text
AddScoped
AddTransient
AddSingleton
TryAddScoped
TryAddTransient
TryAddSingleton
```

---

## 4. Definir prioridade para resolução de chamadas

Ordem recomendada:

```text
1. Tipo concreto conhecido no receiver
2. Implementação registrada na DI
3. Tipo inferido pelo construtor
4. Tipo inferido por atribuição
5. Busca genérica por implementações como último recurso
```

Se ainda existirem várias implementações possíveis, marcar a chamada como:

```text
resolução ambígua
```

em vez de expandir todas automaticamente.

---

## 5. Preservar tipos genéricos concretos

### Problema

Se existe:

```csharp
ServiceBase<Veiculo>
```

com:

```csharp
Repository<TEntity>.Adicionar(...)
```

o resultado não pode ser:

```text
TEntity INSERT
```

### Resultado correto

O contexto deve preservar:

```text
TEntity = Veiculo
```

Portanto:

```text
Veiculo INSERT
```

---

## 6. Criar um contexto de execução por nó

Cada nó do Call Graph deve carregar informações contextuais.

Exemplo conceitual:

```csharp
public class AnalysisExecutionContext
{
    public IMethodSymbol Method { get; set; }

    public Dictionary<ITypeParameterSymbol, ITypeSymbol> GenericTypes { get; set; }

    public Dictionary<IParameterSymbol, ValueInfo> Arguments { get; set; }

    public Dictionary<ISymbol, ITypeSymbol> KnownTypes { get; set; }
}
```

Esse contexto deve preservar:

```text
tipo concreto
receiver
argumentos
generics
retornos
condições conhecidas
```

---

## 7. Propagar tipos dos argumentos

Exemplo:

```csharp
NotifyError(new DomainNotification(...));
```

Depois:

```csharp
void PublicarEvento(Event pEvent)
```

O analisador deve carregar:

```text
pEvent = DomainNotification
```

Se mais adiante encontrar:

```csharp
if (pEvent is ProgramacaoTransporteEvent)
```

esse caminho pode ser descartado para aquela execução.

---

## 8. Eliminar branches impossíveis

A propagação de tipos deve permitir remover caminhos incompatíveis.

Exemplos:

```text
type checks impossíveis
null checks conhecidos
enums conhecidos
booleans conhecidos
pattern matching incompatível
```

Essa etapa é importante para evitar que eventos ou serviços não relacionados contaminem a análise.

---

## 9. Utilizar Control Flow Graph

Não analisar somente a árvore sintática.

Criar um Control Flow Graph para métodos relevantes, permitindo entender:

```text
branches
returns
throws
loops
caminhos alcançáveis
condições acumuladas
```

O objetivo é saber quais caminhos realmente podem levar a uma regra ou efeito.

---

## 10. Utilizar Data Flow

Para cada valor relevante, responder:

```text
onde foi criado?
onde recebeu valor?
onde foi lido?
qual condição depende dele?
qual efeito depende dele?
```

Exemplo:

```text
xUnidadeEhTransportadora
```

Se ele participa de:

```csharp
if (!xUnidadeEhTransportadora)
    throw ...;
```

a consulta que gera esse valor é relevante para a regra de negócio.

---

## 11. Não concatenar condições indefinidamente

Evitar saídas como:

```text
A && B && C && D && E && F && G...
```

Criar um registro de condições:

```json
{
  "C1": "ModelState.IsValid",
  "C2": "xUnidadeEhTransportadora",
  "C3": "xVeiculoTipo != null"
}
```

Depois referenciar:

```text
Veiculo INSERT
Conditions: C1, C2, C3
```

Isso reduz repetição e melhora a leitura.

---

## 12. Separar grafo técnico e grafo de negócio

Manter dois grafos.

### Grafo técnico

Contém tudo que foi resolvido pelo analisador.

Uso:

```text
debug
auditoria
diagnóstico
investigação
```

### Grafo de negócio

Contém apenas:

```text
validações
regras
consultas relevantes
persistência
efeitos externos
```

Esse deve ser o grafo principal exibido ao usuário.

---

## 13. Classificar nós por relevância

Sugestão de categorias:

```text
BUSINESS
VALIDATION
DATA_ACCESS
PERSISTENCE
EXTERNAL_EFFECT
INFRASTRUCTURE
UTILITY
UNKNOWN
```

### BUSINESS

Métodos como:

```text
VeiculoService.PlacaEhValida
VeiculoService.NomeEhValido
```

Devem permanecer.

### VALIDATION

Exemplo:

```text
Veiculo.EhValido
VeiculoValidation.ValidarTipo
```

Devem permanecer.

### DATA_ACCESS

Exemplo:

```text
VeiculoRepository.PossuiPlacaExistente
```

Manter quando o resultado influencia regra, filtro ou efeito.

### PERSISTENCE

Exemplo:

```text
Repository.Adicionar
SaveChangesAsync
```

Sempre relevante.

### INFRASTRUCTURE

Exemplo:

```text
ApiResponse
NotifyError
InMemoryBus
logging
```

Colapsar por padrão.

### UTILITY

Exemplo:

```text
ToString
Equals
SanitizaPlaca
```

Ocultar salvo quando alterar diretamente a interpretação da regra.

---

## 14. Colapsar infraestrutura

Um fluxo como:

```text
ApiController.ObterErrosModel
→ MostrarErrosModel
→ NotifyError
→ InMemoryBus.PublicarEvento
→ SalvarHistorico
→ ...
```

pode ser mostrado simplesmente como:

```text
Retorna erro de validação
```

O mecanismo interno continua disponível no grafo técnico, mas não precisa aparecer no grafo de negócio.

---

## 15. Diferenciar eventos de negócio de notificações de erro

Criar classificação:

```text
BUSINESS_EVENT
DOMAIN_ERROR
INFRA_EVENT
```

Exemplo:

```text
ProgramacaoCriadaEvent
→ BUSINESS_EVENT
```

```text
DomainNotification
→ DOMAIN_ERROR
```

Eventos do tipo `DOMAIN_ERROR` não devem provocar expansão automática de todos os consumidores do barramento.

---

## 16. Não expandir abstrações de Repository indiscriminadamente

Se o endpoint chama:

```text
Repository.Adicionar
```

não há motivo para também explorar automaticamente:

```text
ObterTodos
Atualizar
Excluir
ExistePorId
```

Seguir somente o método realmente invocado.

---

## 17. Resolver o receiver da chamada

Para:

```csharp
_veiculoRepository.PossuiPlacaExistente(...)
```

registrar:

```text
receiver symbol:
_veiculoRepository

receiver type:
IVeiculoRepository

resolved type:
VeiculoRepository
```

O Call Graph deve seguir:

```text
VeiculoRepository.PossuiPlacaExistente
```

não todas as implementações relacionadas a `IRepository`.

---

## 18. Tratar herança de forma contextual

Se:

```text
VeiculoRepository : Repository<Veiculo>
```

seguir o método do `VeiculoRepository` quando ele existir ali.

Subir para:

```text
Repository<Veiculo>
```

somente quando a chamada realmente resolver para um método herdado.

---

## 19. Introduzir conceito de Sink

O analisador deve dar prioridade a operações observáveis.

Exemplos:

```text
THROW
RETURN
INSERT
UPDATE
DELETE
SaveChanges
Publish
Send
HTTP Request
File Write
```

Esses pontos são os efeitos relevantes do fluxo.

---

## 20. Trabalhar de trás para frente

Depois de encontrar um sink, analisar suas dependências.

Exemplo:

```text
Veiculo INSERT
```

Perguntar:

```text
quem criou Veiculo?
quais validações permitem chegar aqui?
quais regras bloqueiam a operação?
quais dados alimentam a entidade?
```

Essa abordagem aproxima a análise da regra de negócio.

---

## 21. Aplicar Forward + Backward Analysis

Estratégia recomendada:

```text
FORWARD
Endpoint
→ encontra possíveis caminhos
→ encontra sinks
```

Depois:

```text
BACKWARD
Sink
→ encontra dependências reais
→ remove caminhos irrelevantes
```

---

## 22. Classificar efeitos

Sugestão:

```text
PRIMARY
SECONDARY
INFRASTRUCTURE
```

Exemplo:

```text
Veiculo INSERT
→ PRIMARY
```

```text
VeiculoCriadoEvent
→ SECONDARY
```

```text
Log técnico
→ INFRASTRUCTURE
```

A visualização principal deve mostrar `PRIMARY` e `SECONDARY`.

---

## 23. Não considerar qualquer Add como entidade do endpoint

Encontrar:

```csharp
repository.Add(x);
```

não é suficiente para concluir que `x` é uma entidade relevante do endpoint.

Validar:

```text
tipo concreto
origem da chamada
contexto de execução
caminho alcançável
relação com o endpoint
```

---

## 24. Criar score de relevância

O score pode ajudar no filtro final.

Exemplo:

```text
+100 altera entidade
+90 representa regra de negócio
+80 gera DomainException
+80 influencia resposta
+70 consulta banco usada em decisão
+70 publica evento de negócio
+50 transforma DTO em entidade

-30 infraestrutura
-50 logging
-60 helper genérico
-70 framework
```

O score deve complementar a análise contextual, nunca substituir DI, tipos, Control Flow e Data Flow.

---

## 25. Manter consultas relevantes mesmo sem alteração

Uma leitura pode representar parte fundamental de uma regra.

Exemplo:

```text
PossuiPlacaExistente
```

Ela deve aparecer porque sustenta a regra:

```text
Não permitir cadastro de veículo com placa duplicada.
```

Portanto, consultas devem permanecer quando seu resultado influencia decisão, retorno ou efeito.

---

## 26. Ocultar helpers sem perder seu significado

Um helper como:

```text
SanitizaPlaca
```

pode ser removido do Call Graph principal.

Mas a regra final pode registrar:

```text
A comparação da placa utiliza valor sanitizado.
```

Ou seja, ocultar o detalhe técnico não significa perder a informação relevante.

---

## 27. Criar níveis de visualização

Sugestão para a interface:

```text
Resumo
Negócio
Técnico
Completo
```

### Resumo

```text
objetivo
regras
entidades
efeitos
```

### Negócio

```text
serviços relevantes
validações
consultas utilizadas em regras
```

### Técnico

```text
repositórios
métodos auxiliares relevantes
persistência
```

### Completo

```text
Call Graph bruto
```

---

## 28. Resultado esperado para o endpoint analisado

Um `POST /veiculo/v1` deveria resultar em algo próximo a:

```text
VeiculoController.AdicionarVeiculoV1
└─ VeiculoWebServiceApplicationService.AdicionarVeiculoV1
   ├─ VeiculoTipoService.ExistePorId
   ├─ UnidadeService.ExistePorId
   ├─ CategoriaVeiculoService.ExistePorId
   ├─ ExisteCarroceriaPerfil
   ├─ ExisteCarroceriaTipo
   ├─ UnidadeService.EhUnidadeTransportadora
   ├─ VeiculoService.PlacaEhValida
   │  ├─ PossuiPlacaExistente
   │  └─ PlacaAntigaExiste
   ├─ VeiculoService.NomeEhValido
   ├─ Veiculo.EhValido
   ├─ VeiculoService.ValidarTamanhoCamposVeiculo
   ├─ VeiculoService.Adicionar
   └─ SalvarAlteracoesAsync
```

E não expandir automaticamente:

```text
InMemoryBus
todos os repositories genéricos
todos os services genéricos
handlers não relacionados
eventos incompatíveis
```

---

## 29. Pipeline recomendado

```text
Endpoint
   ↓
Resolve receiver
   ↓
Resolve DI
   ↓
Resolve generics
   ↓
Propaga tipos
   ↓
Control Flow Graph
   ↓
Data Flow
   ↓
Remove branches impossíveis
   ↓
Encontra sinks
   ↓
Backward slicing
   ↓
Classifica relevância
   ↓
Colapsa infraestrutura
   ↓
Gera análise final
```

---

## 30. Ordem recomendada de implementação

### Etapa 1 — DIResolver

Corrigir a resolução de interfaces.

```text
Interface
→ implementação real registrada
```

### Etapa 2 — GenericTypeResolver

Eliminar tipos como:

```text
TEntity
```

na saída final.

### Etapa 3 — TypePropagationContext

Propagar tipos concretos de argumentos e receivers.

### Etapa 4 — BranchFeasibilityAnalyzer

Eliminar caminhos incompatíveis com os tipos e valores conhecidos.

### Etapa 5 — CallNodeCategory

Classificar cada nó em negócio, validação, infraestrutura etc.

### Etapa 6 — InfrastructureCollapser

Colapsar mecanismos internos que não agregam valor ao usuário.

### Etapa 7 — EffectSinkDetector

Encontrar operações observáveis relevantes.

### Etapa 8 — RelevantCodeSlicer

Executar backward slicing partindo dos sinks.

---

## 31. Critérios de validação

A melhoria deve ser considerada bem-sucedida quando:

```text
1. TEntity não aparecer mais como entidade final.

2. Interfaces resolvidas por DI não expandirem para
   implementações não registradas.

3. Branches impossíveis forem eliminados.

4. Infrastructure code não dominar o Call Graph.

5. DomainNotification não provocar expansão de handlers
   não relacionados.

6. Entidades retornadas representarem efeitos reais do endpoint.

7. Toda regra possuir um caminho de evidência até o endpoint.

8. O grafo de negócio for significativamente menor que o grafo técnico.

9. Cada nó exibido possuir justificativa de relevância.

10. A análise final puder ser entendida sem ler o Call Graph bruto.
```

---

## 32. Regra final

A evolução do analisador deve seguir esta ideia:

```text
NÃO:
"descobrir tudo que o endpoint consegue alcançar"

MAS:
"descobrir somente o que realmente influencia
as regras, decisões e efeitos observáveis daquele endpoint"
```

Esse princípio deve orientar a remoção de ruído em toda a análise estática.
