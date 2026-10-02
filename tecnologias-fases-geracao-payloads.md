# Tecnologias e Fases — Geração de Payloads por Endpoint

## Fase 1 — Leitura do projeto

**Tecnologias**
- .NET 10
- Roslyn (`Microsoft.CodeAnalysis`)
- `MSBuildWorkspace`

**Função**
- Abrir a `.sln`.
- Localizar endpoints, DTOs, parâmetros, validações e chamadas.
- Montar o fluxo inicial do endpoint.

---

## Fase 2 — Redução de ruído

**Tecnologias**
- Roslyn Semantic Model
- `IOperation`
- `ControlFlowGraph`
- Análise de Dependency Injection

**Função**
- Resolver implementações concretas.
- Preservar tipos genéricos.
- Eliminar caminhos impossíveis.
- Ignorar infraestrutura irrelevante.

---

## Fase 3 — Extração das regras

**Tecnologias**
- Roslyn
- Motor próprio em .NET

**Função**
Transformar o código em regras estruturadas, por exemplo:

```text
nome obrigatório
nome entre 2 e 100 caracteres
unidade deve existir
placa não pode estar duplicada
```

Também identificar:
- pré-condições externas;
- mensagens de erro;
- status HTTP;
- alterações de entidades.

---

## Fase 4 — Geração dos cenários

**Tecnologias / técnicas**
- Motor próprio em .NET
- Decision Tables
- Equivalence Partitioning
- Boundary Value Analysis

**Função**
Gerar cenários representativos.

Exemplo:

```text
nome = null
nome = ""
nome com 1 caractere
nome com 2 caracteres
nome com 100 caracteres
nome com 101 caracteres
```

Cada cenário deve possuir:

```text
Payload
Pré-condições
Resultado esperado
```

---

## Fase 5 — Condições complexas

**Tecnologia**
- Microsoft Z3 (`Microsoft.Z3`)

**Função**
Resolver restrições difíceis.

Exemplo:

```text
quantidade > capacidade / 2
peso * quantidade <= limite
tipo != Bloqueado
```

O Z3 deve ser usado apenas quando necessário, não como núcleo do sistema.

---

## Fase 6 — Apresentação

**Tecnologias**
- Claude ou Codex
- JSON estruturado

**Função**
- Descrever o cenário.
- Explicar a regra.
- Organizar o resultado esperado.
- Melhorar a legibilidade.

A IA não deve decidir sozinha quais caminhos existem no código.

---

# Pipeline final

```text
Projeto .NET
↓
Roslyn
↓
Call Graph filtrado
↓
Control Flow + Data Flow
↓
Regras e condições
↓
Decision Tables
↓
Equivalence Partitioning
↓
Boundary Values
↓
Z3 (casos complexos)
↓
Payloads + pré-condições + resultado esperado
↓
Claude / Codex para apresentação
```

# Ordem de implementação

```text
1. Roslyn + MSBuildWorkspace
2. Redução de ruído e resolução de DI/generics
3. Extração de regras
4. Decision Tables
5. Equivalence Partitioning
6. Boundary Value Analysis
7. Z3
8. IA para apresentação
```

O núcleo do sistema deve ser **determinístico**. A IA deve ficar na camada final de interpretação e apresentação.
